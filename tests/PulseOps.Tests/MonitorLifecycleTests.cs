using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed class MonitorLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 30, 0, TimeSpan.Zero);
    private readonly AppScope _scope = new() { Application = "ledger", Environment = "staging", AllowedNames = ["report"] };
    private string _directory = null!;
    private PlatformOptions _options = null!;
    private MonitorPolicy _policy = null!;
    private Storage _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Directory.CreateTempSubdirectory("pulseops-monitor-lifecycle-").FullName;
        _policy = new() { Application = "ledger", Environment = "staging", Name = "report-health",
            Kind = "api", Target = "report", ThresholdMs = 200, WindowMinutes = 10, EvaluationDelayMinutes = 0,
            MinimumSamples = 1, RecoveryWindows = 1, CooldownMinutes = 2 };
        _options = new() { DatabasePath = Path.Combine(_directory, "events.db"), Scopes = [_scope],
            Monitors = [_policy], MonitorIntervalSeconds = 15 };
        _storage = new Storage(_options);
        _storage.Migrate();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void RemovingAConfiguredMonitorClearsItsHealthStateAndPreservesIncidentHistory()
    {
        Sample(Now.AddSeconds(-10), "v1", "success", 400);
        _storage.EvaluateMonitors(Now);
        Assert.That(Status().State, Is.EqualTo("Alert"));
        _options.Monitors.Clear();
        _storage = new(_options);
        _storage.EvaluateMonitors(Now.AddMinutes(1));
        Assert.That(_storage.MonitorStatuses([_scope]), Is.Empty);
        Assert.That(Scalar("SELECT COUNT(*) FROM monitor_state"), Is.Zero);
        Assert.That(Scalar("SELECT COUNT(*) FROM incidents WHERE kind='monitor'"), Is.EqualTo(1));
    }

    [Test]
    public void RepeatedDeploymentOfSameVersionDoesNotShiftTheComparisonBoundary()
    {
        _policy.Type = "regression";
        _policy.LatencyIncreaseRatio = 0;
        Release("v1", "old-build", -45);
        Sample(Now.AddMinutes(-40), "v1", "success", 100);
        Release("v2", "new-build", -15);
        // Old clients can continue calling while a different version is deployed.
        for (var i = 0; i < 9; i++)
            Sample(Now.AddMinutes(-14).AddSeconds(i), "v1", "failure", 100);
        Sample(Now.AddMinutes(-8), "v2", "failure", 100);
        Release("v2", "restarted-build", -5);
        Sample(Now.AddMinutes(-1), "v2", "success", 100);

        _storage.EvaluateMonitors(Now);
        var status = Status();
        Assert.Multiple(() =>
        {
            Assert.That(status.State, Is.EqualTo("Alert"));
            Assert.That(status.Samples, Is.EqualTo(2));
            Assert.That(status.Observed, Is.EqualTo(50));
            Assert.That(status.Baseline, Is.Zero);
        });
    }

    [Test]
    public void SimultaneousConflictingReleaseMarkersDoNotChooseACohortByBuildName()
    {
        _policy.Type = "regression";
        Release("v1", "first", -45);
        Release("v2", "second", -5);
        Release("v3", "third", -5);
        _storage.EvaluateMonitors(Now);
        Assert.That(Status().State, Is.EqualTo("InsufficientData"));
        Assert.That(Status().Detail, Does.Contain("share a deployment timestamp"));
    }

    [Test]
    public void NeverSeenSignalWaitsForBothInitialGraceAndEvaluationDelayAcrossRestart()
    {
        _policy.Type = "no-data";
        _policy.NoDataMinutes = 5;
        _policy.EvaluationDelayMinutes = 2;
        _storage.EvaluateMonitors(Now);
        _storage.EvaluateMonitors(Now.AddMinutes(1));
        _storage = new(_options);
        _storage.EvaluateMonitors(Now.AddMinutes(6));
        Assert.That(Status().State, Is.EqualTo("InsufficientData"));
        _storage.EvaluateMonitors(Now.AddMinutes(7));
        Assert.That(Status().State, Is.EqualTo("Alert"));
        Assert.That(Status().Observed, Is.EqualTo(5));
    }

    [Test]
    public void ManualResolutionIsRespectedDuringCooldownThenReopensExactlyOnce()
    {
        Sample(Now.AddMinutes(-1), "v1", "success", 900);
        _storage.EvaluateMonitors(Now);
        string incident = Status().IncidentId!;
        var filter = new QueryFilter([_scope], 0, long.MaxValue, null, 10, 0, null, null, null, null);
        Assert.That(_storage.ChangeIncident(filter, incident, new("Resolved", null, null), "operator", Now.AddSeconds(10)),
            Is.EqualTo(204));
        _storage.EvaluateMonitors(Now.AddMinutes(2));
        Assert.That(Status().Detail, Does.Contain("cooldown"));
        Assert.That(Scalar("SELECT COUNT(*) FROM incidents WHERE status='Resolved'"), Is.EqualTo(1));
        _storage.EvaluateMonitors(Now.AddMinutes(3));
        _storage.EvaluateMonitors(Now.AddMinutes(3));
        Assert.That(Scalar("SELECT COUNT(*) FROM incident_history WHERE status='Reopened'"), Is.EqualTo(1));
        Assert.That(Status().IncidentId, Is.EqualTo(incident));
    }

    private void Release(string version, string build, int minutes) =>
        _storage.AddRelease(_scope, new("ledger", "staging", version, build, "abc"), Now.AddMinutes(minutes));

    private void Sample(DateTimeOffset at, string version, string outcome, double duration)
    {
        _storage.Ingest(_scope, [new(Guid.NewGuid(), at, version, Guid.NewGuid(), "api", "report", outcome, duration)],
            false, at);
        _storage.Process(at);
    }

    private MonitorStatus Status() => _storage.MonitorStatuses([_scope]).Single();

    private long Scalar(string sql)
    {
        using var db = _storage.Open();
        using var command = db.Command(null, sql);
        return (long)command.ExecuteScalar()!;
    }
}
