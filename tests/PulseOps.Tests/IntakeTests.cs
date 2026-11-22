using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed class IntakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 30, 0, TimeSpan.Zero);
    private string _directory = null!;
    private PlatformOptions _options = null!;
    private AppScope _scope = null!;
    private Storage _storage = null!;

    [SetUp]
    public void Configure()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pulseops-intake-" + Guid.NewGuid().ToString("N"));
        _scope = new()
        {
            Application = "finance", Environment = "production", Label = "Finance", AllowedNames = ["report"],
            IngestToken = "local-test-only-intake-credential"
        };
        _options = new() { DatabasePath = Path.Combine(_directory, "pulseops.db"), MinimumFreeMegabytes = 1, Scopes = [_scope] };
        _storage = new Storage(_options);
        _storage.Migrate();
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public void SuspendedScopeAnswersGoneWithoutStoringEvents()
    {
        Assert.That(Receive(), Is.EqualTo(StatusCodes.Status202Accepted));
        _scope.IntakeSuspended = true;
        Assert.That(Receive(), Is.EqualTo(StatusCodes.Status410Gone));
        Assert.That(Receive(token: "wrong-credential"), Is.EqualTo(StatusCodes.Status401Unauthorized),
            "The kill switch is disclosed only to authenticated relays.");
        Assert.That(_storage.Process(Now), Is.EqualTo(1));
    }

    [Test]
    public void ExceptionEventsOutliveOtherRawTelemetry()
    {
        _storage.Ingest(_scope, [Event("api"), Event("error")], false, Now);
        _storage.Process(Now);
        _storage.Retain(Now.AddDays(_options.RetentionDays + 1));
        Assert.That(Count(), Is.EqualTo(1), "Only the exception event remains after raw retention.");
        _storage.Retain(Now.AddDays(_options.ErrorRetentionDays + 1));
        Assert.That(Count(), Is.Zero);
    }

    [Test]
    [TestCase(45, 180, 45)]
    [TestCase(3, 10, 10)]
    [TestCase(14, 180, 30)]
    public void ExistingRetentionWindowsRemainValidWhenTheErrorPolicyIsOmitted(int raw, int incidents, int errors)
    {
        var options = new PlatformOptions { DatabasePath = Path.Combine(Path.GetTempPath(), "unused.db"), RetentionDays = raw, IncidentRetentionDays = incidents };
        options.Validate(true, Path.GetTempPath(), true);
        Assert.That(options.ErrorRetentionDays, Is.EqualTo(errors));
    }

    [Test]
    public void RetentionRangesAreValidated()
    {
        var options = new PlatformOptions { DatabasePath = "/tmp/unused.db", RetentionDays = 14, ErrorRetentionDays = 7 };
        Assert.Throws<InvalidOperationException>(() => options.Validate(true, "/tmp", true));
        options.ErrorRetentionDays = 30;
        options.IncidentRetentionDays = 20;
        Assert.Throws<InvalidOperationException>(() => options.Validate(true, "/tmp", true));
    }

    private int Receive(string? token = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + (token ?? _scope.IngestToken);
        var batch = new TelemetryBatch(1, "finance", "production",
            [new TelemetryEvent(Guid.NewGuid(), Now, "1.0.0", Guid.NewGuid(), "api", "report", "success", 12, 200)]);
        var result = Endpoints.Receive(batch, context, _options, _storage, new FixedClock(Now), false);
        return result is IStatusCodeHttpResult { StatusCode: int status } ? status : -1;
    }

    private static TelemetryEvent Event(string kind) =>
        new(Guid.NewGuid(), Now, "1.0.0", Guid.NewGuid(), kind, "report", kind == "error" ? "failure" : "success");

    private long Count()
    {
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry";
        return (long)command.ExecuteScalar()!;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
