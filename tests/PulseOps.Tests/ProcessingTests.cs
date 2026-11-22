using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed class ProcessingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 30, 0, TimeSpan.Zero);
    private string _directory = null!;
    private PlatformOptions _options = null!;
    private AppScope _busy = null!;
    private AppScope _quiet = null!;
    private Storage _storage = null!;

    [SetUp]
    public void Configure()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pulseops-processing-" + Guid.NewGuid().ToString("N"));
        _busy = new() { Application = "busy", Environment = "production", Label = "Busy", AllowedNames = ["report"] };
        _quiet = new() { Application = "quiet", Environment = "production", Label = "Quiet", AllowedNames = ["report"] };
        _options = new()
        {
            DatabasePath = Path.Combine(_directory, "pulseops.db"), MinimumFreeMegabytes = 1, InboxMaximum = 10_000,
            Scopes = [_busy, _quiet]
        };
        _storage = new Storage(_options);
        _storage.Migrate();
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public void FloodingScopeCannotStarveAnotherScope()
    {
        Assert.That(_storage.Ingest(_busy, Enumerable.Range(0, 1_000).Select(_ => Event()), false, Now), Is.EqualTo(1_000));
        Assert.That(_storage.Ingest(_quiet, Enumerable.Range(0, 5).Select(_ => Event()), false, Now), Is.EqualTo(5));

        Assert.That(_storage.Process(Now), Is.EqualTo(Storage.BatchSize));
        Assert.Multiple(() =>
        {
            Assert.That(Pending("quiet"), Is.Zero, "The quiet scope is served in the first batch despite arriving last.");
            Assert.That(Pending("busy"), Is.EqualTo(1_000 - (Storage.BatchSize - 5)), "Unused share flows to the busy scope.");
        });
    }

    [Test]
    public void SingleScopeKeepsArrivalOrder()
    {
        _storage.Ingest(_busy, Enumerable.Range(0, 200).Select(_ => Event()), false, Now);
        _storage.Process(Now);
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(row_id) FILTER (WHERE done IS NOT NULL) < MIN(row_id) FILTER (WHERE done IS NULL) FROM telemetry";
        Assert.That((long)command.ExecuteScalar()!, Is.EqualTo(1));
    }

    [Test]
    public void DefectiveRecordIsQuarantinedWithoutBlockingTheBatch()
    {
        _storage.Ingest(_busy, Enumerable.Range(0, 10).Select(_ => Event()), false, Now);
        long poison = RowIds().ElementAt(3);
        _storage.RecordHook = rowId =>
        {
            if (rowId == poison)
                throw new InvalidOperationException("synthetic defect");
        };

        Assert.That(_storage.Process(Now), Is.EqualTo(10));
        Assert.Multiple(() =>
        {
            Assert.That(Pending("busy"), Is.Zero);
            Assert.That(Scalar("SELECT COUNT(*) FROM dead_letters"), Is.EqualTo(1));
            Assert.That(Scalar("SELECT COUNT(*) FROM dead_letters WHERE reason = 'InvalidOperationException'"), Is.EqualTo(1));
            Assert.That(Scalar("SELECT COUNT(*) FROM telemetry WHERE done IS NOT NULL"), Is.EqualTo(9));
            Assert.That(_storage.Quarantined, Is.EqualTo(1));
        });
        var health = System.Text.Json.JsonSerializer.SerializeToElement(_storage.PlatformHealth());
        Assert.That(health.GetProperty("deadLetters").GetInt64(), Is.EqualTo(1));

        _storage.RecordHook = null;
        Assert.That(_storage.ReplayDeadLetters(), Is.EqualTo((1, 0)));
        Assert.That(Scalar("SELECT COUNT(*) FROM dead_letters"), Is.Zero);
        Assert.That(_storage.Process(Now), Is.EqualTo(1));
        Assert.That(Scalar("SELECT COUNT(*) FROM telemetry WHERE done IS NOT NULL"), Is.EqualTo(10));
    }

    [Test]
    public void ReusedReceiptNumberCanBeQuarantinedTwiceAndBothEventsReplayed()
    {
        _storage.RecordHook = _ => throw new FormatException("synthetic");
        for (int i = 0; i < 2; i++)
        {
            _storage.Ingest(_busy, [Event()], false, Now);
            Assert.That(RowIds(), Is.EqualTo(new long[] { 1 }));
            Assert.That(_storage.Process(Now), Is.EqualTo(1));
        }
        _storage.RecordHook = null;
        Assert.That(_storage.ReplayDeadLetters(), Is.EqualTo((2, 0)));
        Assert.That(_storage.Process(Now), Is.EqualTo(2));
        Assert.That(Scalar("SELECT COUNT(DISTINCT id) FROM telemetry WHERE done IS NOT NULL"), Is.EqualTo(2));
    }

    [Test]
    public void BusyScopesBeyondTheBatchBoundaryGetServedAcrossRestarts()
    {
        for (int i = 0; i < 200; i++)
        {
            var scope = new AppScope { Application = $"scope-{i:000}", Environment = "production" };
            _storage.Ingest(scope, [Event(), Event(), Event()], false, Now);
        }
        Assert.That(_storage.Process(Now), Is.EqualTo(Storage.BatchSize));
        Assert.That(new Storage(_options).Process(Now), Is.EqualTo(Storage.BatchSize));
        Assert.That(Scalar("SELECT COUNT(DISTINCT application) FROM telemetry WHERE done IS NOT NULL"), Is.EqualTo(200));
    }

    [Test]
    public void MigrationRecoversRetainedHistoryFromAnEmptySchemaFourRollupTable()
    {
        _storage.Ingest(_busy, [Event(), Event()], false, Now);
        _storage.Process(Now);
        using (var connection = _storage.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM rollups;
                DROP TABLE processing_cursor;
                PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
        }
        _storage.Migrate();
        var later = Now.AddDays(_options.RetentionDays + 1);
        _storage.Retain(later);
        Assert.That(Scalar("SELECT COUNT(*) FROM telemetry"), Is.EqualTo(2));
        Assert.That(_storage.BackfillRollups(1), Is.EqualTo(1));
        _storage.Retain(later);
        Assert.That(Scalar("SELECT COUNT(*) FROM telemetry"), Is.EqualTo(1));
        Assert.That(_storage.BackfillRollups(1), Is.EqualTo(1));
        _storage.Retain(later);
        Assert.That(Scalar("SELECT COUNT(*) FROM telemetry"), Is.Zero);
        Assert.That(Scalar("SELECT MAX(total) FROM rollups"), Is.EqualTo(2));
        _storage.Migrate();
        Assert.That(_storage.BackfillRollups(), Is.Zero);
    }

    [Test]
    public void ReplaySkipsEventsThatWereAcceptedAgain()
    {
        var item = Event();
        _storage.Ingest(_busy, [item], false, Now);
        _storage.RecordHook = _ => throw new FormatException("synthetic defect");
        _storage.Process(Now);
        _storage.RecordHook = null;
        // The source retried the same event after it left the inbox.
        Assert.That(_storage.Ingest(_busy, [item], false, Now), Is.EqualTo(1));
        Assert.That(_storage.ReplayDeadLetters(), Is.EqualTo((0, 1)));
        Assert.That(Scalar("SELECT COUNT(*) FROM telemetry"), Is.EqualTo(1), "The duplicate event id is not inserted twice.");
        Assert.That(Scalar("SELECT COUNT(*) FROM dead_letters"), Is.Zero);
    }

    [Test]
    public void SystemicFaultsOpenTheCircuitAndKeepTheBacklog()
    {
        _storage.Ingest(_busy, Enumerable.Range(0, 30).Select(_ => Event()), false, Now);
        _storage.RecordHook = _ => throw new InvalidOperationException("synthetic systemic defect");

        Assert.Throws<InvalidOperationException>(() => _storage.Process(Now));
        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT COUNT(*) FROM dead_letters"), Is.EqualTo(20));
            Assert.That(Pending("busy"), Is.EqualTo(10), "Remaining records stay in the durable inbox.");
        });
    }

    [Test]
    public void InfrastructureFaultsAreNotTreatedAsRecordDefects()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Storage.IsRecordFault(new Microsoft.Data.Sqlite.SqliteException("busy", 5)), Is.False);
            Assert.That(Storage.IsRecordFault(new IOException()), Is.False);
            Assert.That(Storage.IsRecordFault(new OperationCanceledException()), Is.False);
            Assert.That(Storage.IsRecordFault(new InvalidOperationException()), Is.True);
            Assert.That(Storage.IsRecordFault(new FormatException()), Is.True);
        });
    }

    [Test]
    public void DeadLettersFollowRawRetention()
    {
        _storage.Ingest(_busy, [Event()], false, Now);
        _storage.RecordHook = _ => throw new InvalidOperationException("synthetic defect");
        _storage.Process(Now);
        _storage.RecordHook = null;
        _storage.Retain(Now.AddDays(_options.RetentionDays + 1));
        Assert.That(Scalar("SELECT COUNT(*) FROM dead_letters"), Is.Zero);
    }

    private static TelemetryEvent Event() => new(Guid.NewGuid(), Now, "1.0.0", Guid.NewGuid(), "api", "report", "success", 120, 200);

    private long Pending(string application) =>
        Scalar($"SELECT COUNT(*) FROM telemetry WHERE done IS NULL AND application = '{application}'");

    private IEnumerable<long> RowIds()
    {
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_id FROM telemetry ORDER BY row_id";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private long Scalar(string sql)
    {
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
