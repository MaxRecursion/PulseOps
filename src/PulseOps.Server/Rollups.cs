using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PulseOps.Server;

// Long-range rollups: one row per (scope, resolution, UTC bucket by event time, kind, name, version, anonymous) with
// outcome counts, duration sum/maximum and a mergeable latency histogram (Histogram.cs). Hourly rows serve monitor
// baselines and short history, daily rows long history. Rollups outlive raw telemetry retention by design.
public sealed partial class Storage
{
    public const long HourMs = 3_600_000;
    public const long DayMs = 86_400_000;
    /// <summary>GET /history refuses ranges that would read more rollup rows than this.</summary>
    internal const int HistoryRowLimit = 200_000;
    internal const int HistorySeriesLimit = 50;
    /// <summary>Rollup rows removed per resolution, and signal activity rows removed, per retention pass.</summary>
    internal const int RollupRetentionBatch = 5000;
    private const int BackfillBatchMaximum = 10_000;
    private static readonly long[] Resolutions = [HourMs, DayMs];
    private static readonly string[] RollupKeyNames =
        ["$app", "$env", "$resolution", "$bucket", "$kind", "$name", "$version", "$anonymous"];

    // Exactly-once aggregation. Process() calls ApplyRollups inside the IMMEDIATE transaction that marks the batch done;
    // BackfillRollups() aggregates done rows with backfill_cursor < row_id <= backfill_until under the same write lock.
    // Processing.cs alone does not keep rows processed after migration out of that range: fair-share batches commit rows
    // out of row_id order across scopes, so pending rows can sit below the newest processed row, and an INTEGER PRIMARY
    // KEY without AUTOINCREMENT reuses ids after the highest rows are deleted. So ApplyRollups leaves rows inside the
    // open range to the backfill, which reads every done row there before its cursor passes it; a row still pending when
    // the cursor passes is aggregated here later because it is then at or below the cursor. Invariant: each processed
    // row is counted once, by the path chosen from rollup_state when its done state commits; the range only shrinks.
    // Signal activity is idempotent (MIN/MAX), so it is always applied for the whole batch.
    /// <summary>Aggregates a processed batch into hourly and daily rollups and signal activity inside the caller's transaction.</summary>
    private void ApplyRollups(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<PendingItem> items)
    {
        var (cursor, until) = BackfillRange(connection, transaction);
        UpsertRollups(connection, transaction, cursor < until
            ? items.Where(item => item.RowId <= cursor || item.RowId > until).ToList() : items);
        UpsertActivity(connection, transaction, items);
    }

    /// <summary>Rolls up at most <paramref name="maximum"/> rows processed before schema 4. Returns rows consumed.</summary>
    public int BackfillRollups(int maximum = 2000)
    {
        int limit = Math.Clamp(maximum, 1, BackfillBatchMaximum);
        using var connection = Open();
        // Once complete the range never reopens, so the worker loop stops taking the write lock for it.
        var (cursor, until) = BackfillRange(connection, null);
        if (cursor >= until)
            return 0;
        using var transaction = connection.BeginTransaction();
        (cursor, until) = BackfillRange(connection, transaction);
        if (cursor >= until)
            return 0;
        using var select = connection.Command(transaction, $"""
            SELECT {PendingColumns} FROM telemetry
            WHERE row_id > $cursor AND row_id <= $until AND done IS NOT NULL ORDER BY row_id LIMIT $limit
            """, ("$cursor", cursor), ("$until", until), ("$limit", limit));
        var items = Read(select, ReadPending);
        UpsertRollups(connection, transaction, items);
        UpsertActivity(connection, transaction, items);
        // Pending rows in the skipped part of the range are aggregated by Process once they complete.
        using var advance = connection.Command(transaction, "UPDATE rollup_state SET backfill_cursor = $cursor WHERE id = 1",
            ("$cursor", items.Count < limit ? until : items[^1].RowId));
        advance.ExecuteNonQuery();
        transaction.Commit();
        return items.Count;
    }

    /// <summary>Deletes expired hourly/daily rollups and stale signal activity in bounded batches.</summary>
    private void RetainRollups(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        // Oldest buckets first through rollup_retention, then primary-key deletes: at most one batch per resolution.
        using var rollups = connection.Command(transaction, """
            DELETE FROM rollups WHERE (application, environment, resolution, bucket, kind, name, version, anonymous) IN (
                SELECT application, environment, resolution, bucket, kind, name, version, anonymous FROM rollups
                WHERE resolution = $resolution AND bucket < $cutoff ORDER BY bucket LIMIT $limit)
            """, ("$resolution", HourMs), ("$cutoff", Floor(now.AddDays(-options.RollupHourlyDays), HourMs)),
            ("$limit", RollupRetentionBatch));
        rollups.ExecuteNonQuery();
        Set(rollups, ("$resolution", DayMs), ("$cutoff", Floor(now.AddDays(-options.RollupRetentionDays), DayMs)));
        rollups.ExecuteNonQuery();
        using var activity = connection.Command(transaction, """
            DELETE FROM signal_activity WHERE (application, environment, kind, name) IN (
                SELECT application, environment, kind, name FROM signal_activity WHERE last_received < $cutoff LIMIT $limit)
            """, ("$cutoff", now.AddDays(-options.RollupRetentionDays).ToUnixTimeMilliseconds()),
            ("$limit", RollupRetentionBatch));
        activity.ExecuteNonQuery();
    }

    /// <summary>Approximate latency percentile from trusted (anonymous = 0) hourly rollups with buckets in [since, until).</summary>
    internal LatencySample HistoricalLatency(SqliteConnection connection, SqliteTransaction? transaction, string application,
        string environment, string kind, string? name, long since, long until, int percentile)
    {
        if (percentile is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile));
        using var command = connection.Command(transaction, $"""
            SELECT duration_max, histogram FROM rollups
            WHERE application = $app AND environment = $env AND resolution = $resolution AND bucket >= $since
                AND bucket < $until AND kind = $kind AND {(string.IsNullOrEmpty(name) ? "1" : "name = $name")}
                AND anonymous = 0 AND timed > 0
            """, ("$app", application), ("$env", environment), ("$resolution", HourMs), ("$since", since),
            ("$until", until), ("$kind", kind), ("$name", name));
        var merged = new LatencyHistogram();
        double? maximum = null;
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                maximum = Max(maximum, rows.NullableDouble(0));
                if (ReadHistogram(rows, 1) is { } histogram)
                    merged.Merge(histogram);
            }
        }
        return merged.Count == 0 ? new(0, null) : new(merged.Count, merged.Quantile(percentile / 100.0, maximum));
    }

    /// <summary>
    /// Zero-filled rollup history over <paramref name="scopes"/>, which the caller has authorized and narrowed, or null
    /// when the range holds more than <paramref name="rowLimit"/> rollup rows. Includes anonymous rows, like the overview.
    /// </summary>
    internal HistoryResult? History(AppScope[] scopes, HistoryQuery query, DateTimeOffset now, int rowLimit = HistoryRowLimit)
    {
        long step = query.StepMs;
        var from = now.AddDays(-query.Days);
        long first = Floor(from, step);
        var points = new RollupTotals?[(Floor(now, step) - first) / step + 1];
        var series = new List<(string Application, string Environment, string Name, long Total, long Failures, RollupTotals Latency)>();
        if (scopes.Length > 0)
        {
            using var connection = Open();
            // One read snapshot keeps the row cap, the series ranking and the points consistent.
            using var transaction = connection.BeginTransaction(deferred: true);
            string filter = $"""
                (application, environment) IN (VALUES {string.Join(',', scopes.Select((_, i) => $"($app{i},$env{i})"))})
                AND resolution = $resolution AND bucket BETWEEN $first AND $last AND kind = $kind
                AND {(query.Name is null ? "1" : "name = $name")} AND {(query.Version is null ? "1" : "version = $version")}
                """;
            SqliteCommand Prepare(string sql)
            {
                var command = connection.Command(transaction, sql, ("$resolution", step), ("$first", first),
                    ("$last", first + (points.Length - 1) * step), ("$kind", query.Kind), ("$name", query.Name),
                    ("$version", query.Version), ("$limit", rowLimit + 1L), ("$series", HistorySeriesLimit));
                for (var i = 0; i < scopes.Length; i++)
                {
                    command.Parameters.AddWithValue($"$app{i}", scopes[i].Application);
                    command.Parameters.AddWithValue($"$env{i}", scopes[i].Environment);
                }
                return command;
            }

            using (var count = Prepare($"SELECT COUNT(*) FROM (SELECT 1 FROM rollups WHERE {filter} LIMIT $limit)"))
            {
                if ((long)count.ExecuteScalar()! > rowLimit)
                    return null;
            }
            using (var top = Prepare($"""
                       SELECT application, environment, name, SUM(total) AS requests, SUM(failures) FROM rollups
                       WHERE {filter} GROUP BY application, environment, name
                       ORDER BY requests DESC, application, environment, name LIMIT $series
                       """))
                series = Read(top, row => (row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3),
                    row.GetInt64(4), new RollupTotals()));
            var latency = series.ToDictionary(line => (line.Application, line.Environment, line.Name), line => line.Latency);
            using var rows = Prepare($"""
                SELECT application, environment, name, bucket, total, failures, degraded, cancelled, timed, duration_sum,
                    duration_max, histogram
                FROM rollups WHERE {filter}
                """);
            using var reader = rows.ExecuteReader();
            while (reader.Read())
            {
                var point = points[(reader.GetInt64(3) - first) / step] ??= new();
                var maximum = reader.NullableDouble(10);
                var histogram = ReadHistogram(reader, 11);
                point.Total += reader.GetInt64(4);
                point.Failures += reader.GetInt64(5);
                point.Degraded += reader.GetInt64(6);
                point.Cancelled += reader.GetInt64(7);
                point.Timed += reader.GetInt64(8);
                point.DurationSum += reader.GetDouble(9);
                point.DurationMax = Max(point.DurationMax, maximum);
                if (histogram is not null)
                    point.Histogram.Merge(histogram);
                if (latency.TryGetValue((reader.GetString(0), reader.GetString(1), reader.GetString(2)), out var line))
                {
                    line.DurationMax = Max(line.DurationMax, maximum);
                    if (histogram is not null)
                        line.Histogram.Merge(histogram);
                }
            }
        }
        return new(query.Resolution, step, from, now, true,
            points.Select((point, slot) => Point(first + slot * step, point)).ToArray(),
            series.Select(line => new HistorySeries(line.Application, line.Environment, query.Kind, line.Name, line.Total,
                line.Failures, line.Latency.Histogram.Quantile(0.95, line.Latency.DurationMax))).ToArray());

        static HistoryPoint Point(long at, RollupTotals? totals) => totals is null
            ? new(DateTimeOffset.FromUnixTimeMilliseconds(at), 0, 0, 0, 0, null, null, null, null, null)
            : new(DateTimeOffset.FromUnixTimeMilliseconds(at), totals.Total, totals.Failures, totals.Degraded,
                totals.Cancelled, totals.Histogram.Quantile(0.50, totals.DurationMax),
                totals.Histogram.Quantile(0.95, totals.DurationMax), totals.Histogram.Quantile(0.99, totals.DurationMax),
                totals.Timed > 0 ? totals.DurationSum / totals.Timed : null, totals.DurationMax);
    }

    private static void UpsertRollups(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<PendingItem> items)
    {
        var batch = new Dictionary<RollupKey, RollupTotals>();
        foreach (var item in items)
        {
            foreach (var resolution in Resolutions)
            {
                var key = new RollupKey(item.Application, item.Environment, resolution, item.Occurred / resolution * resolution,
                    item.Kind, item.Name, item.Version, item.Anonymous);
                if (!batch.TryGetValue(key, out var totals))
                    batch.Add(key, totals = new());
                totals.Add(item);
            }
        }
        if (batch.Count == 0)
            return;
        // One prepared read and one prepared upsert serve every key of the batch.
        using var select = connection.Command(transaction, """
            SELECT total, successes, failures, degraded, cancelled, timed, duration_sum, duration_max, histogram FROM rollups
            WHERE application = $app AND environment = $env AND resolution = $resolution AND bucket = $bucket
                AND kind = $kind AND name = $name AND version = $version AND anonymous = $anonymous
            """, KeyParameters());
        using var upsert = connection.Command(transaction, """
            INSERT INTO rollups(application, environment, resolution, bucket, kind, name, version, anonymous,
                total, successes, failures, degraded, cancelled, timed, duration_sum, duration_max, histogram)
            VALUES ($app, $env, $resolution, $bucket, $kind, $name, $version, $anonymous,
                $total, $successes, $failures, $degraded, $cancelled, $timed, $sum, $max, $histogram)
            ON CONFLICT(application, environment, resolution, bucket, kind, name, version, anonymous) DO UPDATE SET
                total = excluded.total, successes = excluded.successes, failures = excluded.failures,
                degraded = excluded.degraded, cancelled = excluded.cancelled, timed = excluded.timed,
                duration_sum = excluded.duration_sum, duration_max = excluded.duration_max, histogram = excluded.histogram
            """, [.. KeyParameters(), ("$total", 0L), ("$successes", 0L), ("$failures", 0L), ("$degraded", 0L),
            ("$cancelled", 0L), ("$timed", 0L), ("$sum", 0.0), ("$max", null), ("$histogram", Array.Empty<byte>())]);
        foreach (var (key, delta) in batch)
        {
            SetKey(select, key);
            var totals = delta;
            using (var row = select.ExecuteReader())
            {
                if (row.Read())
                {
                    // A corrupt stored histogram is rebuilt from the new samples only; its counts are kept and added.
                    totals = new()
                    {
                        Total = row.GetInt64(0), Successes = row.GetInt64(1), Failures = row.GetInt64(2),
                        Degraded = row.GetInt64(3), Cancelled = row.GetInt64(4), Timed = row.GetInt64(5),
                        DurationSum = row.GetDouble(6), DurationMax = row.NullableDouble(7),
                        Histogram = ReadHistogram(row, 8) ?? new()
                    };
                    totals.Merge(delta);
                }
            }
            SetKey(upsert, key);
            Set(upsert, ("$total", totals.Total), ("$successes", totals.Successes), ("$failures", totals.Failures),
                ("$degraded", totals.Degraded), ("$cancelled", totals.Cancelled), ("$timed", totals.Timed),
                ("$sum", totals.DurationSum), ("$max", totals.DurationMax), ("$histogram", totals.Histogram.Serialize()));
            upsert.ExecuteNonQuery();
        }
    }

    private static void UpsertActivity(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<PendingItem> items)
    {
        var batch = new Dictionary<(string Application, string Environment, string Kind, string Name), (long First, long Last, long Occurred)>();
        foreach (var item in items)
        {
            if (item.Anonymous)
                continue;
            var key = (item.Application, item.Environment, item.Kind, item.Name);
            batch[key] = batch.TryGetValue(key, out var seen)
                ? (Math.Min(seen.First, item.Received), Math.Max(seen.Last, item.Received), Math.Max(seen.Occurred, item.Occurred))
                : (item.Received, item.Received, item.Occurred);
        }
        if (batch.Count == 0)
            return;
        using var upsert = connection.Command(transaction, """
            INSERT INTO signal_activity(application, environment, kind, name, first_received, last_received, last_occurred)
            VALUES ($app, $env, $kind, $name, $first, $last, $occurred)
            ON CONFLICT(application, environment, kind, name) DO UPDATE SET
                first_received = MIN(first_received, excluded.first_received),
                last_received = MAX(last_received, excluded.last_received),
                last_occurred = MAX(last_occurred, excluded.last_occurred)
            """, ("$app", ""), ("$env", ""), ("$kind", ""), ("$name", ""), ("$first", 0L), ("$last", 0L), ("$occurred", 0L));
        foreach (var ((application, environment, kind, name), (first, last, occurred)) in batch)
        {
            Set(upsert, ("$app", application), ("$env", environment), ("$kind", kind), ("$name", name),
                ("$first", first), ("$last", last), ("$occurred", occurred));
            upsert.ExecuteNonQuery();
        }
    }

    private static (long Cursor, long Until) BackfillRange(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var state = connection.Command(transaction, "SELECT backfill_cursor, backfill_until FROM rollup_state WHERE id = 1");
        using var row = state.ExecuteReader();
        return row.Read() ? (row.GetInt64(0), row.GetInt64(1)) : (0, 0);
    }

    /// <summary>The stored histogram, or null when it is missing or corrupt. Corrupt rollup bytes never fail a caller.</summary>
    private static LatencyHistogram? ReadHistogram(SqliteDataReader row, int ordinal)
    {
        if (row.IsDBNull(ordinal))
            return null;
        try
        {
            return LatencyHistogram.Deserialize(row.GetFieldValue<byte[]>(ordinal));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static (string, object?)[] KeyParameters() => [.. RollupKeyNames.Select(name => (name, (object?)DBNull.Value))];

    private static void SetKey(SqliteCommand command, RollupKey key) => Set(command, ("$app", key.Application),
        ("$env", key.Environment), ("$resolution", key.Resolution), ("$bucket", key.Bucket), ("$kind", key.Kind),
        ("$name", key.Name), ("$version", key.Version), ("$anonymous", key.Anonymous));

    private static void Set(SqliteCommand command, params ReadOnlySpan<(string Name, object? Value)> values)
    {
        foreach (var (name, value) in values)
            command.Parameters[name].Value = value ?? DBNull.Value;
    }

    private static long Floor(DateTimeOffset at, long step) => at.ToUnixTimeMilliseconds() / step * step;

    private static double? Max(double? left, double? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private readonly record struct RollupKey(string Application, string Environment, long Resolution, long Bucket,
        string Kind, string Name, string Version, bool Anonymous);

    private sealed class RollupTotals
    {
        public long Total { get; set; }
        public long Successes { get; set; }
        public long Failures { get; set; }
        public long Degraded { get; set; }
        public long Cancelled { get; set; }
        public long Timed { get; set; }
        public double DurationSum { get; set; }
        public double? DurationMax { get; set; }
        public LatencyHistogram Histogram { get; init; } = new();

        public void Add(PendingItem item)
        {
            Total++;
            switch (item.Outcome)
            {
                case "success": Successes++; break;
                case "failure": Failures++; break;
                case "degraded": Degraded++; break;
                case "cancelled": Cancelled++; break;
            }
            if (item.DurationMs is not double duration)
                return;
            Timed++;
            DurationSum += duration;
            DurationMax = Max(DurationMax, duration);
            Histogram.Add(duration);
        }

        public void Merge(RollupTotals other)
        {
            Total += other.Total;
            Successes += other.Successes;
            Failures += other.Failures;
            Degraded += other.Degraded;
            Cancelled += other.Cancelled;
            Timed += other.Timed;
            DurationSum += other.DurationSum;
            DurationMax = Max(DurationMax, other.DurationMax);
            Histogram.Merge(other.Histogram);
        }
    }
}

internal readonly record struct LatencySample(long Samples, double? ValueMs);

/// <summary>Validated GET /history parameters. Application and environment only narrow the caller's visible scopes.</summary>
internal sealed record HistoryQuery(string? Application, string? Environment, string Kind, string? Name, string? Version,
    int Days, string Resolution)
{
    public long StepMs => Resolution == "hour" ? Storage.HourMs : Storage.DayMs;

    public AppScope[] Narrow(AppScope[] visible) => visible.Where(scope =>
        (Application is null || scope.Application == Application) &&
        (Environment is null || scope.Environment == Environment)).ToArray();

    /// <summary>Parses the query string. On failure <paramref name="error"/> is the 400 error code.</summary>
    public static bool TryParse(IQueryCollection query, PlatformOptions options, [NotNullWhen(true)] out HistoryQuery? parsed,
        out string error)
    {
        parsed = null;
        error = "invalid_history_query";
        string? Text(string key) => query[key].ToString() is { Length: > 0 } value ? value : null;
        var (application, environment, name, version) = (Text("application"), Text("environment"), Text("name"), Text("version"));
        var kind = Text("kind") ?? "api";
        var resolution = Text("resolution") ?? "auto";
        int days = Math.Min(30, options.RollupRetentionDays);
        if ((Text("days") is { } value && !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out days)) ||
            days < 1 || days > options.RollupRetentionDays || !Validation.Kinds.Contains(kind) ||
            resolution is not ("auto" or "hour" or "day") ||
            (application is not null && !Validation.IsSlug(application)) ||
            (environment is not null && !Validation.IsSlug(environment)) ||
            (name is not null && !Validation.IsAlias(name)) || (version is not null && !Validation.IsVersion(version)))
            return false;
        if (resolution == "hour" && days > options.RollupHourlyDays)
        {
            error = "range_exceeds_hourly_retention";
            return false;
        }
        parsed = new(application, environment, kind, name, version, days,
            resolution != "auto" ? resolution : days <= 14 && days <= options.RollupHourlyDays ? "hour" : "day");
        error = "";
        return true;
    }
}

internal sealed record HistoryPoint(DateTimeOffset At, long Total, long Failures, long Degraded, long Cancelled,
    double? P50, double? P95, double? P99, double? AverageMs, double? MaxMs);

internal sealed record HistorySeries(string Application, string Environment, string Kind, string Name, long Total,
    long Failures, double? P95);

internal sealed record HistoryResult(string Resolution, long StepMs, DateTimeOffset From, DateTimeOffset Until,
    bool ApproximatePercentiles, IReadOnlyList<HistoryPoint> Points, IReadOnlyList<HistorySeries> Series);

internal static class RollupEndpoints
{
    public static void Map(RouteGroupBuilder api, PlatformOptions options) =>
        api.MapGet("/history", (HttpContext context, Storage storage, TimeProvider clock) =>
        {
            if (!HistoryQuery.TryParse(context.Request.Query, options, out var query, out var error))
                return Results.BadRequest(new { error });
            return storage.History(query.Narrow(Access.Visible(context.User, options)), query, clock.GetUtcNow()) is { } history
                ? Results.Ok(history) : Results.BadRequest(new { error = "range_too_large" });
        });
}
