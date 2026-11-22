using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed class RollupTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Hour = new(2026, 11, 21, 6, 0, 0, TimeSpan.Zero);
    // (γ-1)/(γ+1) ≈ 1.961% plus floating-point slack at bucket boundaries.
    private const double Tolerance = 0.0197;
    private static readonly int[] Permilles = [10, 250, 500, 750, 900, 950, 990, 999, 1000];
    private string _directory = null!;
    private PlatformOptions _options = null!;
    private AppScope _production = null!;
    private AppScope _staging = null!;
    private Storage _storage = null!;

    [SetUp]
    public void Configure()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pulseops-rollups-" + Guid.NewGuid().ToString("N"));
        _production = Scope("production", "group-p");
        _staging = Scope("staging", "group-s");
        _options = new()
        {
            DatabasePath = Path.Combine(_directory, "pulseops.db"), MinimumFreeMegabytes = 1, InboxMaximum = 10_000,
            Scopes = [_production, _staging]
        };
        _storage = new Storage(_options);
        _storage.Migrate();
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public void HistogramQuantilesStayWithinTwoPercentOfExactNearestRank()
    {
        var random = new Random(20261121);
        var datasets = new Dictionary<string, double[]>
        {
            ["uniform"] = Samples(5_000, () => 1 + random.NextDouble() * 999),
            ["log-normal"] = Samples(10_000, () => Math.Exp(Math.Log(200) + Gaussian(random) * 1.2)),
            ["pareto"] = Samples(10_000, () => Math.Min(86_400_000, 10 * Math.Pow(1 - random.NextDouble(), -1 / 1.1))),
            ["bimodal-spikes"] = Samples(8_000, () => random.NextDouble() < 0.7
                ? 50 + Gaussian(random) * 5 : 2_000 + random.NextDouble() * 58_000),
            ["microseconds"] = Samples(2_000, () => 0.002 + random.NextDouble() * 0.5)
        };
        foreach (var size in new[] { 1, 2, 3, 7, 20 })
            datasets["small-" + size] = Samples(size, () => 5 + random.NextDouble() * 5_000);

        foreach (var (label, values) in datasets)
        {
            var histogram = new LatencyHistogram();
            foreach (var value in values)
                histogram.Add(value);
            Array.Sort(values);
            Assert.That(histogram.Count, Is.EqualTo(values.Length), label);
            foreach (var permille in Permilles)
            {
                double exact = values[(values.Length * permille + 999) / 1000 - 1];
                foreach (var maximum in new double?[] { null, values[^1] })
                {
                    double actual = histogram.Quantile(permille / 1000.0, maximum)!.Value;
                    Assert.That(Math.Abs(actual - exact) / exact, Is.LessThanOrEqualTo(Tolerance),
                        $"{label} p{permille / 10.0} clamp={maximum is not null}: {actual} vs exact {exact}");
                }
            }
            Assert.That(histogram.Quantile(1, values[^1]), Is.LessThanOrEqualTo(values[^1]), label);
        }
    }

    [Test]
    public void NearestRankMatchesTheSqlIntegerForm()
    {
        for (long n = 1; n <= 20_000; n++)
        {
            foreach (var percentile in MonitorPolicy.Percentiles)
            {
                if (LatencyHistogram.Rank(percentile / 100.0, n) != (n * percentile + 99) / 100)
                    Assert.Fail($"p{percentile} n={n}");
            }
            foreach (var permille in Permilles)
            {
                if (LatencyHistogram.Rank(permille / 1000.0, n) != (n * permille + 999) / 1000)
                    Assert.Fail($"permille {permille} n={n}");
            }
        }
        Assert.That(LatencyHistogram.Rank(0, 10), Is.EqualTo(1));
    }

    [Test]
    public void HistogramMergesSerializesAndRejectsCorruptInput()
    {
        var random = new Random(7);
        var values = Samples(3_000, () => Math.Exp(random.NextDouble() * 12));
        LatencyHistogram whole = new(), left = new(), right = new();
        for (var i = 0; i < values.Length; i++)
        {
            whole.Add(values[i]);
            (i % 3 == 0 ? left : right).Add(values[i]);
        }
        left.Merge(right);
        var bytes = whole.Serialize();
        var copy = LatencyHistogram.Deserialize(bytes);
        Assert.Multiple(() =>
        {
            Assert.That(left.Serialize(), Is.EqualTo(bytes), "Merging is order independent.");
            Assert.That(copy.Serialize(), Is.EqualTo(bytes));
            Assert.That(copy.Count, Is.EqualTo(values.Length));
            Assert.That(copy.Quantile(0.95, null), Is.EqualTo(whole.Quantile(0.95, null)));
            Assert.That(new LatencyHistogram().Serialize(), Is.EqualTo(new byte[] { 1 }));
            Assert.That(LatencyHistogram.Deserialize([1]).Quantile(0.5, null), Is.Null);
        });

        var edge = new LatencyHistogram();
        edge.Add(1);
        edge.Add(-5);
        edge.Add(0);
        edge.Add(double.NaN);
        edge.Add(double.PositiveInfinity);
        edge.Add(1e300);
        Assert.Multiple(() =>
        {
            Assert.That(LatencyHistogram.Index(1), Is.EqualTo(177));
            Assert.That(edge.Count, Is.EqualTo(4), "Non-finite durations are ignored.");
            Assert.That(edge.Serialize(), Is.EqualTo(new byte[] { 1, 0, 0, 2, 0, 0, 0, 177, 0, 1, 0, 0, 0, 0, 16, 1, 0, 0, 0 }),
                "Zero and negative share bucket 0; huge values saturate at index 4096.");
            Assert.That(edge.Quantile(0.25, null), Is.EqualTo(LatencyHistogram.MinimumMs));
            Assert.That(() => edge.Quantile(1.5, null), Throws.TypeOf<ArgumentOutOfRangeException>());
        });

        var tooMany = new byte[1 + 1025 * 6];
        tooMany[0] = 1;
        for (var i = 0; i < 1025; i++)
        {
            tooMany[1 + i * 6] = (byte)i;
            tooMany[2 + i * 6] = (byte)(i >> 8);
            tooMany[3 + i * 6] = 1;
        }
        byte[][] corrupt =
        [
            [], [2], [0], [1, 0], [1, 5, 0, 1, 0, 0],
            [1, 5, 0, 0, 0, 0, 0],
            [1, 5, 0, 1, 0, 0, 0, 4, 0, 1, 0, 0, 0],
            [1, 5, 0, 1, 0, 0, 0, 5, 0, 1, 0, 0, 0],
            [1, 1, 16, 1, 0, 0, 0],
            [1, 0xFF, 0xFF, 1, 0, 0, 0],
            tooMany
        ];
        foreach (var data in corrupt)
            Assert.That(() => LatencyHistogram.Deserialize(data), Throws.TypeOf<FormatException>(), Convert.ToHexString(data));
    }

    [Test]
    public void ProcessingWritesHourlyAndDailyRollupsAndTrustedSignalActivity()
    {
        var early = Now.AddMinutes(-40);
        _storage.Ingest(_production,
        [
            Event(Hour.AddHours(1).AddMinutes(10), 100), Event(Hour.AddHours(1).AddMinutes(11), 300, "failure"),
            Event(Hour.AddHours(1).AddMinutes(12), 200, "degraded"), Event(Hour.AddHours(1).AddMinutes(13), null, "cancelled"),
            Event(Hour.AddHours(1).AddMinutes(14), 50, "unknown"), Event(Hour.AddHours(-7).AddMinutes(30), 120)
        ], false, early);
        _storage.Ingest(_production, [Event(Hour.AddHours(2).AddMinutes(5), 150),
            Event(Hour.AddHours(2).AddMinutes(6), 400, version: "2.0.0")], false, Now);
        _storage.Ingest(_production, [Event(Hour.AddHours(2).AddMinutes(10), 900, kind: "startup", name: "PageLoaded")], true, Now);
        Assert.That(ProcessAll(Now), Is.EqualTo(9));

        long seven = Hour.AddHours(1).ToUnixTimeMilliseconds(), eight = Hour.AddHours(2).ToUnixTimeMilliseconds();
        long yesterdayHour = Hour.AddHours(-7).ToUnixTimeMilliseconds();
        long today = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long yesterday = today - Storage.DayMs;
        Assert.That(Rows().Select(row => row with { Histogram = "" }), Is.EquivalentTo(new[]
        {
            Expected(Storage.HourMs, seven, "api", "report", "1.0.0", false, 5, 1, 1, 1, 1, 4, 650, 300),
            Expected(Storage.HourMs, eight, "api", "report", "1.0.0", false, 1, 1, 0, 0, 0, 1, 150, 150),
            Expected(Storage.HourMs, eight, "api", "report", "2.0.0", false, 1, 1, 0, 0, 0, 1, 400, 400),
            Expected(Storage.HourMs, yesterdayHour, "api", "report", "1.0.0", false, 1, 1, 0, 0, 0, 1, 120, 120),
            Expected(Storage.HourMs, eight, "startup", "PageLoaded", "1.0.0", true, 1, 1, 0, 0, 0, 1, 900, 900),
            Expected(Storage.DayMs, today, "api", "report", "1.0.0", false, 6, 2, 1, 1, 1, 5, 800, 300),
            Expected(Storage.DayMs, today, "api", "report", "2.0.0", false, 1, 1, 0, 0, 0, 1, 400, 400),
            Expected(Storage.DayMs, yesterday, "api", "report", "1.0.0", false, 1, 1, 0, 0, 0, 1, 120, 120),
            Expected(Storage.DayMs, today, "startup", "PageLoaded", "1.0.0", true, 1, 1, 0, 0, 0, 1, 900, 900)
        }));
        Assert.Multiple(() =>
        {
            Assert.That(Rows().All(row => row.Samples == row.Timed), Is.True, "Every timed sample is in the histogram.");
            Assert.That(Activity(), Is.EqualTo(new[] { ("production", "api", "report", early.ToUnixTimeMilliseconds(),
                Now.ToUnixTimeMilliseconds(), Hour.AddHours(2).AddMinutes(6).ToUnixTimeMilliseconds()) }),
                "Anonymous startup observations never feed signal activity.");
        });
    }

    [Test]
    public void DuplicatesRetriesAndRepeatedProcessingNeverDoubleCount()
    {
        var events = Enumerable.Range(0, 10).Select(i => Event(Hour.AddMinutes(i), 100 + i)).ToArray();
        Assert.That(_storage.Ingest(_production, events, false, Now), Is.EqualTo(10));
        Assert.That(_storage.Ingest(_production, events, false, Now), Is.Zero);
        Assert.That(ProcessAll(Now), Is.EqualTo(10));
        Assert.That(ProcessAll(Now), Is.Zero);
        Assert.That(Totals(), Is.EqualTo((10, 10)));

        // A record fault rolls the whole batch back (rollups included) before each record is retried alone.
        _storage.Ingest(_production, Enumerable.Range(0, 10).Select(i => Event(Hour.AddMinutes(20 + i), 200)), false, Now);
        long poison = Scalar("SELECT MAX(row_id) FROM telemetry") - 6;
        _storage.RecordHook = rowId =>
        {
            if (rowId == poison)
                throw new InvalidOperationException("synthetic defect");
        };
        Assert.That(_storage.Process(Now), Is.EqualTo(10));
        Assert.That(Totals(), Is.EqualTo((19, 19)));
        _storage.RecordHook = null;
        Assert.That(_storage.ReplayDeadLetters().Returned, Is.EqualTo(1));
        Assert.That(ProcessAll(Now), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(Totals(), Is.EqualTo((20, 20)));
            Assert.That(Rows().All(row => row.Samples == row.Total), Is.True);
            Assert.That(_storage.BackfillRollups(), Is.Zero, "A fresh database has nothing to backfill.");
        });
    }

    [Test]
    public void BackfillRollsUpRowsProcessedBeforeMigrationExactlyOnce()
    {
        var random = new Random(41);
        string[] names = ["report", "export", "login"];
        string[] outcomes = ["success", "success", "success", "failure", "degraded", "cancelled", "unknown"];
        foreach (var scope in new[] { _production, _staging })
        {
            var events = Enumerable.Range(0, 150).Select(_ => Event(Hour.AddMinutes(-random.Next(0, 3 * 24 * 60)),
                random.Next(4) == 0 ? null : random.Next(1, 5_000), outcomes[random.Next(outcomes.Length)],
                names[random.Next(names.Length)], random.Next(2) == 0 ? "1.0.0" : "1.1.0")).ToArray();
            _storage.Ingest(scope, events[..140], false, Now);
            _storage.Ingest(scope, events[140..].Select(e => e with { Kind = "startup", Name = "PageLoaded" }), true, Now);
        }
        Assert.That(ProcessAll(Now), Is.EqualTo(300));
        var rollups = Rows();
        var activity = Activity();

        // Simulate a database processed by a pre-v4 worker: nothing rolled up, cursor 0, until = newest processed row.
        Execute("""
            DELETE FROM rollups; DELETE FROM signal_activity;
            UPDATE rollup_state SET backfill_cursor = 0, backfill_until = (SELECT MAX(row_id) FROM telemetry WHERE done IS NOT NULL);
            """);
        Assert.That(BackfillRemaining(), Is.GreaterThan(0));
        int consumed = 0, calls = 0;
        for (int step; (step = _storage.BackfillRollups(37)) > 0; calls++)
            consumed += step;
        Assert.Multiple(() =>
        {
            Assert.That(consumed, Is.EqualTo(300));
            Assert.That(calls, Is.EqualTo(9));
            Assert.That(Rows(), Is.EquivalentTo(rollups), "Counts, sums, maxima and histogram bytes match live processing.");
            Assert.That(Activity(), Is.EquivalentTo(activity));
            Assert.That(BackfillRemaining(), Is.Zero);
            Assert.That(_storage.BackfillRollups(), Is.Zero);
        });

        _storage.Ingest(_production, Enumerable.Range(0, 25).Select(i => Event(Hour.AddMinutes(i), 100)), false, Now);
        Assert.That(ProcessAll(Now), Is.EqualTo(25));
        Assert.Multiple(() =>
        {
            Assert.That(Totals(), Is.EqualTo((325, 325)), "Rows processed after migration are counted once, by Process.");
            Assert.That(_storage.BackfillRollups(), Is.Zero);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RowsPendingBelowTheBackfillLimitAreCountedOnce(bool processFirst)
    {
        // Fair-share batches can leave a lower row id pending while later rows are processed.
        _storage.Ingest(_production, Enumerable.Range(0, 10).Select(i => Event(Hour.AddMinutes(i), 100 + i)), false, Now);
        long pending = Scalar("SELECT MIN(row_id) FROM telemetry") + 2;
        Execute($"""
            UPDATE telemetry SET done = {Now.ToUnixTimeMilliseconds()} WHERE row_id != {pending};
            UPDATE rollup_state SET backfill_cursor = 0, backfill_until = (SELECT MAX(row_id) FROM telemetry WHERE done IS NOT NULL);
            """);
        if (processFirst)
            Assert.That(_storage.Process(Now), Is.EqualTo(1));
        while (_storage.BackfillRollups(4) > 0) { }
        if (!processFirst)
            Assert.That(_storage.Process(Now), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(Totals(), Is.EqualTo((10, 10)));
            Assert.That(Rows().All(row => row.Samples == 10 && row.Timed == 10), Is.True);
            Assert.That(Activity(), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void RetentionRemovesExpiredHourlyRowsButKeepsDailyRows()
    {
        _storage.Ingest(_production, [Event(Now.AddDays(-200), 100, name: "legacy")], false, Now.AddDays(-200));
        _storage.Ingest(_production, [Event(Now.AddDays(-40), 100)], false, Now.AddDays(-40));
        _storage.Ingest(_production, [Event(Now.AddHours(-1), 100)], false, Now);
        ProcessAll(Now);
        Assert.That(Rows(), Has.Count.EqualTo(6));

        _storage.Retain(Now);
        var hourly = Rows().Where(row => row.Resolution == Storage.HourMs).Select(row => row.Bucket).ToArray();
        var daily = Rows().Where(row => row.Resolution == Storage.DayMs).Select(row => row.Bucket).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(hourly, Is.EqualTo(new[] { Floor(Now.AddHours(-1), Storage.HourMs) }));
            Assert.That(daily, Is.EquivalentTo(new[] { Floor(Now.AddDays(-40), Storage.DayMs), Floor(Now, Storage.DayMs) }),
                "Daily rows outlive hourly and raw retention.");
            Assert.That(Activity().Select(row => row.Name), Is.EqualTo(new[] { "report" }));
            Assert.That(Scalar("SELECT COUNT(*) FROM telemetry"), Is.EqualTo(1), "Raw telemetry follows its own retention.");
        });
    }

    [Test]
    public void RetentionWorkIsBoundedPerPass()
    {
        long old = Floor(Now.AddDays(-_options.RollupHourlyDays - 1), Storage.HourMs);
        using (var connection = _storage.Open())
        using (var transaction = connection.BeginTransaction())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO rollups VALUES ('finance', 'production', $resolution, $bucket, 'api', $name, '1.0.0', 0,
                    1, 1, 0, 0, 0, 0, 0, NULL, x'01')
                """;
            insert.Parameters.AddWithValue("$resolution", Storage.HourMs);
            insert.Parameters.AddWithValue("$bucket", 0L);
            insert.Parameters.AddWithValue("$name", "");
            for (var i = 0; i < Storage.RollupRetentionBatch + 1_000; i++)
            {
                insert.Parameters["$bucket"].Value = old - i / 10 * Storage.HourMs;
                insert.Parameters["$name"].Value = "n" + i % 10;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        _storage.Retain(Now);
        Assert.That(Scalar("SELECT COUNT(*) FROM rollups"), Is.EqualTo(1_000));
        Assert.That(Scalar("SELECT MIN(bucket) FROM rollups"), Is.EqualTo(old - 99 * Storage.HourMs), "Oldest buckets go first.");
        _storage.Retain(Now);
        Assert.That(Scalar("SELECT COUNT(*) FROM rollups"), Is.Zero);
    }

    [Test]
    public void HistoryQueryValidatesAndSelectsResolution()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Parse(""), Is.EqualTo(new HistoryQuery(null, null, "api", null, null, 30, "day")));
            Assert.That(Parse("?days=7")!.Resolution, Is.EqualTo("hour"));
            Assert.That(Parse("?days=14")!.StepMs, Is.EqualTo(Storage.HourMs));
            Assert.That(Parse("?days=15")!.Resolution, Is.EqualTo("day"));
            Assert.That(Parse("?days=10&resolution=day")!.StepMs, Is.EqualTo(Storage.DayMs));
            Assert.That(Parse("?days=32&resolution=hour")!.Resolution, Is.EqualTo("hour"));
            Assert.That(Parse("?days=180&kind=startup&name=PageLoaded&version=2.8.3&application=finance&environment=staging"),
                Is.EqualTo(new HistoryQuery("finance", "staging", "startup", "PageLoaded", "2.8.3", 180, "day")));
            Assert.That(Error("?days=33&resolution=hour"), Is.EqualTo("range_exceeds_hourly_retention"));
            foreach (var invalid in new[] { "?kind=bogus", "?days=0", "?days=181", "?days=abc", "?days=-1", "?days=%205",
                         "?days=1&days=2", "?resolution=minute", "?name=bad%20name", "?version=" + new string('1', 33),
                         "?application=Finance_App", "?environment=prod%3B" })
                Assert.That(Error(invalid), Is.EqualTo("invalid_history_query"), invalid);
        });
        _options.RollupHourlyDays = 7;
        _options.RollupRetentionDays = 20;
        Assert.Multiple(() =>
        {
            Assert.That(Parse("?days=10")!.Resolution, Is.EqualTo("day"), "Auto never reads past hourly retention.");
            Assert.That(Parse("?days=7")!.Resolution, Is.EqualTo("hour"));
            Assert.That(Parse("")!.Days, Is.EqualTo(20));
            Assert.That(Error("?days=8&resolution=hour"), Is.EqualTo("range_exceeds_hourly_retention"));
        });
    }

    [Test]
    public void HistoryIsScopedZeroFilledFilteredAndApproximate()
    {
        double[] report = [100, 200, 300, 400, 500, 600, 700, 800, 900, 1000];
        _storage.Ingest(_production, [.. report.Select(ms => Event(Hour.AddMinutes(ms / 100), ms)),
            Event(Hour.AddMinutes(20), 1500, "failure"), Event(Hour.AddMinutes(21), 3000, "failure"),
            Event(Hour.AddMinutes(22), null, "cancelled"), Event(Hour.AddMinutes(23), 250, "degraded")], false, Now);
        _storage.Ingest(_production, Enumerable.Range(0, 5).Select(i => Event(Hour.AddHours(2).AddMinutes(i), 80,
            name: "export", version: "2.0.0")), false, Now);
        _storage.Ingest(_production, Enumerable.Range(0, 3).Select(i => Event(Hour.AddMinutes(i), 4_000, kind: "startup",
            name: "PageLoaded")), true, Now);
        _storage.Ingest(_staging, Enumerable.Range(0, 7).Select(i => Event(Hour.AddMinutes(i), 50)), false, Now);
        ProcessAll(Now);

        var query = Parse("?days=1")!;
        var history = _storage.History([_production], query, Now)!;
        var busy = history.Points.Single(point => point.At == Hour);
        double[] timed = [.. report, 1500, 3000, 250];
        Array.Sort(timed);
        Assert.Multiple(() =>
        {
            Assert.That(history.Resolution, Is.EqualTo("hour"));
            Assert.That(history.From, Is.EqualTo(Now.AddDays(-1)));
            Assert.That(history.Points, Has.Count.EqualTo(25));
            Assert.That(history.Points[0].At, Is.EqualTo(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero)));
            Assert.That(history.Points[^1].At, Is.EqualTo(Hour.AddHours(2)));
            Assert.That(history.Points.Zip(history.Points.Skip(1)).All(pair => pair.Second.At - pair.First.At == TimeSpan.FromHours(1)));
            Assert.That(history.Points.Where(point => point.At != Hour && point.At != Hour.AddHours(2)),
                Has.All.Matches<HistoryPoint>(point => point.Total == 0 && point.P50 == null && point.P95 == null &&
                    point.P99 == null && point.AverageMs == null && point.MaxMs == null));
            Assert.That((busy.Total, busy.Failures, busy.Degraded, busy.Cancelled), Is.EqualTo((14L, 2L, 1L, 1L)));
            Assert.That(busy.AverageMs, Is.EqualTo(timed.Sum() / timed.Length).Within(1e-9));
            Assert.That(busy.MaxMs, Is.EqualTo(3000));
            Assert.That(busy.P50!.Value, Is.EqualTo(timed[(timed.Length * 50 + 99) / 100 - 1]).Within(Tolerance * 100).Percent);
            Assert.That(busy.P95, Is.EqualTo(3000), "Clamped to the observed maximum.");
            Assert.That(busy.P99, Is.EqualTo(3000));
            Assert.That(history.Series.Select(line => (line.Name, line.Total, line.Failures)),
                Is.EqualTo(new[] { ("report", 14L, 2L), ("export", 5L, 0L) }));
            Assert.That(history.Series[0].P95, Is.EqualTo(3000));
            Assert.That(history.Series[1].P95!.Value, Is.EqualTo(80).Within(2).Percent);
        });

        var staging = _storage.History([_staging], query, Now)!;
        var visible = Parse("?environment=production")!.Narrow(Access.Visible(Viewer("group-s"), _options));
        Assert.Multiple(() =>
        {
            Assert.That(staging.Points.Sum(point => point.Total), Is.EqualTo(7));
            Assert.That(staging.Series.Select(line => line.Environment), Is.EqualTo(new[] { "staging" }));
            Assert.That(visible, Is.Empty, "A staging viewer cannot narrow into production.");
            Assert.That(_storage.History(visible, query, Now)!.Points.Sum(point => point.Total), Is.Zero);
            Assert.That(Parse("?application=finance&environment=staging")!.Narrow([_production, _staging]), Is.EqualTo(new[] { _staging }));
            Assert.That(Total(Parse("?days=1&version=2.0.0")!), Is.EqualTo(5));
            Assert.That(Total(Parse("?days=1&name=report")!), Is.EqualTo(14));
            Assert.That(Total(Parse("?days=1&name=report&version=2.0.0")!), Is.Zero);
            Assert.That(Total(Parse("?days=1&kind=startup")!), Is.EqualTo(3), "History includes anonymous observations.");
        });

        var daily = _storage.History([_production, _staging], Parse("?days=30")!, Now)!;
        Assert.Multiple(() =>
        {
            Assert.That(daily.StepMs, Is.EqualTo(Storage.DayMs));
            Assert.That(daily.Points, Has.Count.EqualTo(31));
            Assert.That(daily.Points[^1].Total, Is.EqualTo(26));
            Assert.That(_storage.History([_production], query, Now, rowLimit: 1), Is.Null, "Two hourly api rows exceed a cap of one.");
            Assert.That(_storage.History([_production], query, Now, rowLimit: 2), Is.Not.Null);
        });

        var json = JsonSerializer.SerializeToElement(history, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Multiple(() =>
        {
            Assert.That(json.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
                { "resolution", "stepMs", "from", "until", "approximatePercentiles", "points", "series" }));
            Assert.That(json.GetProperty("points")[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
                { "at", "total", "failures", "degraded", "cancelled", "p50", "p95", "p99", "averageMs", "maxMs" }));
            Assert.That(json.GetProperty("points")[0].GetProperty("p95").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(json.GetProperty("series")[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[]
                { "application", "environment", "kind", "name", "total", "failures", "p95" }));
        });

        long Total(HistoryQuery filter) => _storage.History([_production], filter, Now)!.Points.Sum(point => point.Total);
    }

    [Test]
    public void HistoricalLatencyUsesOnlyTrustedHourlyRows()
    {
        var trusted = Enumerable.Range(0, 100).Select(i => 100.0 + i).ToArray();
        _storage.Ingest(_production, trusted.Select(ms => Event(Hour.AddMinutes(ms % 60), ms, kind: "startup", name: "PageLoaded")),
            false, Now);
        _storage.Ingest(_production, Enumerable.Range(0, 100).Select(i => Event(Hour.AddMinutes(5), 5_000 + i, kind: "startup",
            name: "PageLoaded")), true, Now);
        _storage.Ingest(_staging, Enumerable.Range(0, 100).Select(i => Event(Hour.AddMinutes(5), 5_000 + i, kind: "startup",
            name: "PageLoaded")), true, Now);
        ProcessAll(Now);

        long since = Hour.ToUnixTimeMilliseconds(), until = since + Storage.HourMs;
        using var connection = _storage.Open();
        var named = _storage.HistoricalLatency(connection, null, "finance", "production", "startup", "PageLoaded", since, until, 95);
        var any = _storage.HistoricalLatency(connection, null, "finance", "production", "startup", null, since, until, 95);
        Assert.Multiple(() =>
        {
            Assert.That(named.Samples, Is.EqualTo(100));
            Assert.That(named.ValueMs!.Value, Is.EqualTo(trusted[94]).Within(Tolerance * 100).Percent);
            Assert.That(any, Is.EqualTo(named));
            Assert.That(_storage.HistoricalLatency(connection, null, "finance", "production", "startup", "", since, until, 50).ValueMs!.Value,
                Is.EqualTo(trusted[49]).Within(Tolerance * 100).Percent);
            Assert.That(_storage.HistoricalLatency(connection, null, "finance", "staging", "startup", null, since, until, 95),
                Is.EqualTo(new LatencySample(0, null)), "Anonymous rows never feed baselines.");
            Assert.That(_storage.HistoricalLatency(connection, null, "finance", "production", "startup", null, until, until + Storage.HourMs, 95),
                Is.EqualTo(new LatencySample(0, null)), "Buckets are selected in [since, until).");
            Assert.That(_storage.HistoricalLatency(connection, null, "finance", "production", "api", null, since, until, 95).Samples, Is.Zero);
            Assert.That(() => _storage.HistoricalLatency(connection, null, "finance", "production", "startup", null, since, until, 0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void CorruptStoredHistogramIsRebuiltWithoutFailingProcessing()
    {
        _storage.Ingest(_production, Enumerable.Range(0, 3).Select(i => Event(Hour.AddMinutes(i), 100)), false, Now);
        ProcessAll(Now);
        Execute($"UPDATE rollups SET histogram = x'DEADBEEF' WHERE resolution = {Storage.HourMs}");
        long since = Hour.ToUnixTimeMilliseconds();
        using (var connection = _storage.Open())
            Assert.That(_storage.HistoricalLatency(connection, null, "finance", "production", "api", null, since, since + Storage.HourMs, 95),
                Is.EqualTo(new LatencySample(0, null)));
        var point = _storage.History([_production], Parse("?days=1")!, Now)!.Points.Single(p => p.At == Hour);
        Assert.That((point.Total, point.P95, point.MaxMs), Is.EqualTo((3L, (double?)null, (double?)100)));

        _storage.Ingest(_production, Enumerable.Range(0, 2).Select(i => Event(Hour.AddMinutes(10 + i), 200)), false, Now);
        Assert.That(_storage.Process(Now), Is.EqualTo(2));
        var hourly = Rows().Single(row => row.Resolution == Storage.HourMs);
        var daily = Rows().Single(row => row.Resolution == Storage.DayMs);
        Assert.Multiple(() =>
        {
            Assert.That((hourly.Total, hourly.Timed, hourly.Sum, hourly.Max), Is.EqualTo((5L, 5L, 700.0, (double?)200)));
            Assert.That(hourly.Samples, Is.EqualTo(2), "The histogram restarts from the new samples.");
            Assert.That((daily.Total, daily.Samples), Is.EqualTo((5L, 5L)));
            Assert.That(Scalar("SELECT COUNT(*) FROM dead_letters"), Is.Zero);
        });
    }

    [Test]
    public void RollupBatchCostIsMeasured()
    {
        // 128-event batches over 20 keys (4 names x 5 versions in one hour): 40 rollup rows and 4 activity rows per batch.
        const int batches = 12;
        string[] names = ["report", "export", "login", "search"];
        string[] versions = ["1.0.0", "1.1.0", "1.2.0", "2.0.0", "2.1.0"];
        var random = new Random(5);
        _storage.Ingest(_production, Enumerable.Range(0, batches * Storage.BatchSize).Select(i => Event(Hour.AddSeconds(i),
            random.Next(20, 2_000), name: names[i % 4], version: versions[i / 4 % 5])), false, Now);
        var process = new List<double>();
        for (var watch = Stopwatch.StartNew(); _storage.Process(Now) > 0; watch.Restart())
            process.Add(watch.Elapsed.TotalMilliseconds);
        Assert.That(Rows(), Has.Count.EqualTo(40));

        Execute("""
            DELETE FROM rollups; DELETE FROM signal_activity;
            UPDATE rollup_state SET backfill_cursor = 0, backfill_until = (SELECT MAX(row_id) FROM telemetry);
            """);
        var backfill = new List<double>();
        for (var watch = Stopwatch.StartNew(); _storage.BackfillRollups(Storage.BatchSize) > 0; watch.Restart())
            backfill.Add(watch.Elapsed.TotalMilliseconds);
        Assert.That(Totals(), Is.EqualTo((batches * Storage.BatchSize, batches * Storage.BatchSize)));
        TestContext.Out.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Process(128 events, 20 keys) ms: median {Median(process):F2}, max {process.Max():F2}; BackfillRollups(128) ms: median {Median(backfill):F2}, max {backfill.Max():F2}"));
        Assert.That(Median(backfill), Is.LessThan(250), "Generous guard against pathological regressions only.");

        static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
    }

    private static double[] Samples(int count, Func<double> next) => Enumerable.Range(0, count).Select(_ => next()).ToArray();

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    private static TelemetryEvent Event(DateTimeOffset at, double? duration, string outcome = "success", string name = "report",
        string version = "1.0.0", string kind = "api") =>
        new(Guid.NewGuid(), at, version, Guid.NewGuid(), kind, name, outcome, duration);

    private static AppScope Scope(string environment, string group) => new()
    {
        Application = "finance", Label = "Finance", Environment = environment, ViewerGroups = [group],
        AllowedNames = ["report", "export", "login", "search", "legacy"]
    };

    private static ClaimsPrincipal Viewer(string group) =>
        new(new ClaimsIdentity([new(ClaimTypes.Role, "Developer"), new("groups", group)], "test"));

    private HistoryQuery? Parse(string query) =>
        HistoryQuery.TryParse(Request(query), _options, out var parsed, out _) ? parsed : null;

    private string Error(string query) =>
        HistoryQuery.TryParse(Request(query), _options, out _, out var error) ? "" : error;

    private static IQueryCollection Request(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context.Request.Query;
    }

    private int ProcessAll(DateTimeOffset now)
    {
        int total = 0;
        for (int step; (step = _storage.Process(now)) > 0;)
            total += step;
        return total;
    }

    private static long Floor(DateTimeOffset at, long step) => at.ToUnixTimeMilliseconds() / step * step;

    private static Row Expected(long resolution, long bucket, string kind, string name, string version, bool anonymous,
        long total, long successes, long failures, long degraded, long cancelled, long timed, double sum, double? max) =>
        new("production", resolution, bucket, kind, name, version, anonymous, total, successes, failures, degraded, cancelled,
            timed, sum, max, timed, "");

    private sealed record Row(string Environment, long Resolution, long Bucket, string Kind, string Name, string Version,
        bool Anonymous, long Total, long Successes, long Failures, long Degraded, long Cancelled, long Timed, double Sum,
        double? Max, long Samples, string Histogram);

    private List<Row> Rows()
    {
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT environment, resolution, bucket, kind, name, version, anonymous, total, successes, failures, degraded,
                cancelled, timed, duration_sum, duration_max, histogram FROM rollups
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            var blob = (byte[])reader.GetValue(15);
            long samples;
            try { samples = LatencyHistogram.Deserialize(blob).Count; }
            catch (FormatException) { samples = -1; }
            rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetBoolean(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetDouble(13),
                reader.IsDBNull(14) ? null : reader.GetDouble(14), samples, Convert.ToHexString(blob)));
        }
        return rows;
    }

    private List<(string Environment, string Kind, string Name, long First, long Last, long Occurred)> Activity()
    {
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT environment, kind, name, first_received, last_received, last_occurred FROM signal_activity";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string, long, long, long)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetInt64(5)));
        return rows;
    }

    private (long Hourly, long Daily) Totals() =>
        (Scalar($"SELECT COALESCE(SUM(total), 0) FROM rollups WHERE resolution = {Storage.HourMs}"),
         Scalar($"SELECT COALESCE(SUM(total), 0) FROM rollups WHERE resolution = {Storage.DayMs}"));

    private long BackfillRemaining() =>
        JsonSerializer.SerializeToElement(_storage.PlatformHealth()).GetProperty("rollupBackfillRemaining").GetInt64();

    private void Execute(string sql)
    {
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
