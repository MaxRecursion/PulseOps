using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed class MonitorEvaluationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly AppScope _scope = new() { Application = "ledger", Environment = "staging", AllowedNames = ["report"] };
    private string _directory = null!;
    private PlatformOptions _options = null!;
    private Storage _storage = null!;
    private MonitorPolicy _policy = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Directory.CreateTempSubdirectory("pulseops-monitors-").FullName;
        _policy = new() { Application = _scope.Application, Environment = _scope.Environment, Name = "report-health",
            Kind = "api", Target = "report", ThresholdMs = 200, WindowMinutes = 1, EvaluationDelayMinutes = 0,
            MinimumSamples = 2, RecoveryWindows = 1 };
        _options = new() { DatabasePath = Path.Combine(_directory, "events.db"), Scopes = [_scope],
            Monitors = [_policy], MonitorIntervalSeconds = 15 };
        _storage = new Storage(_options);
        _storage.Migrate();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void LatencyAlertAndRecoverySurviveRestartAndDuplicateEvaluation()
    {
        Sample(Now.AddSeconds(-20), 400);
        Sample(Now.AddSeconds(-5), 600);
        Assert.That(_storage.EvaluateMonitors(Now), Is.EqualTo(1));
        var alert = Status();
        Assert.That(alert.State, Is.EqualTo("Alert"));
        Assert.That(alert.Observed, Is.EqualTo(600));
        _storage = new Storage(_options);
        Assert.That(_storage.EvaluateMonitors(Now), Is.Zero);
        Assert.That(Status().IncidentId, Is.EqualTo(alert.IncidentId));
        Assert.That(_storage.MonitorStatuses([]), Is.Empty);

        for (int minute = 1; minute <= 2; minute++)
        {
            Sample(Now.AddMinutes(minute).AddSeconds(-20), 40);
            Sample(Now.AddMinutes(minute).AddSeconds(-5), 60);
            _storage.EvaluateMonitors(Now.AddMinutes(minute));
        }
        Assert.That(Status().State, Is.EqualTo("OK"));
        Assert.That(Count("SELECT COUNT(*) FROM incidents WHERE kind='monitor' AND status='Resolved'"), Is.EqualTo(1));
        Assert.That(Count("SELECT COUNT(*) FROM incident_history"), Is.EqualTo(2));
    }

    [Test]
    public void MissingSignalHonorsInitialGraceAndExcludesAnonymousActivity()
    {
        _policy.Type = "no-data";
        _policy.NoDataMinutes = 2;
        _storage.EvaluateMonitors(Now);
        Assert.That(Status().State, Is.EqualTo("InsufficientData"));
        Sample(Now.AddMinutes(1), 40, anonymous: true);
        _storage.EvaluateMonitors(Now.AddMinutes(1));
        Assert.That(Status().State, Is.EqualTo("InsufficientData"));
        _storage.EvaluateMonitors(Now.AddMinutes(2));
        Assert.That(Status().State, Is.EqualTo("Alert"));
        Assert.That(Status().Observed, Is.EqualTo(2));
    }

    [Test]
    public void ReleaseRegressionUsesTrustedEventsFromTheTwoDeployedVersions()
    {
        _policy.Type = "regression";
        _policy.FailureIncreasePercent = 25;
        _policy.LatencyIncreaseRatio = 0;
        _storage.AddRelease(_scope, new(_scope.Application, _scope.Environment, "v1", "first", "abc"), Now.AddMinutes(-10));
        Sample(Now.AddMinutes(-5), 80, version: "v1");
        Sample(Now.AddMinutes(-4), 80, version: "v1");
        _storage.EvaluateMonitors(Now.AddMinutes(-3));
        Assert.That(Status().State, Is.EqualTo("InsufficientData"));

        _storage.AddRelease(_scope, new(_scope.Application, _scope.Environment, "v2", "second", "def"), Now.AddMinutes(-2));
        Sample(Now.AddSeconds(-40), 80, "failure", version: "v2");
        Sample(Now.AddSeconds(-30), 80, "success", version: "v2");
        Sample(Now.AddSeconds(-20), 80, "failure", version: "canary");
        Sample(Now.AddSeconds(-10), 80, "success", anonymous: true, version: "v2");
        _storage.EvaluateMonitors(Now);
        Assert.That(Status().State, Is.EqualTo("Alert"));
        Assert.That(Status().Observed, Is.EqualTo(50));
        Assert.That(Status().Samples, Is.EqualTo(2));
    }

    private void Sample(DateTimeOffset time, double duration, string outcome = "success", bool anonymous = false, string version = "v1")
    {
        _storage.Ingest(_scope, [new(Guid.NewGuid(), time, version, Guid.NewGuid(), "api", "report", outcome, duration)], anonymous, time);
        _storage.Process(time);
    }

    private MonitorStatus Status() => _storage.MonitorStatuses([_scope]).Single();

    private long Count(string sql)
    {
        using var db = _storage.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
