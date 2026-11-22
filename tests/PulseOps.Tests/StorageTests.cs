using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed class StorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 30, 0, TimeSpan.Zero);
    private string _directory = null!;
    private PlatformOptions _options = null!;
    private AppScope _production = null!;
    private AppScope _staging = null!;
    private Storage _storage = null!;

    [SetUp]
    public void Configure()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pulseops-tests-" + Guid.NewGuid().ToString("N"));
        _production = Scope("production", "group-p");
        _staging = Scope("staging", "group-s");
        _options = new()
        {
            DatabasePath = Path.Combine(_directory, "pulseops.db"), MinimumFreeMegabytes = 1,
            Scopes = [_production, _staging],
            Rules = [new() { Kind = "api", MinimumSamples = 3, MinimumFailures = 2, FailurePercent = 50 }]
        };
        _storage = new Storage(_options);
        _storage.Migrate();
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public void DurableInboxSurvivesRestartAndProcessesIdempotently()
    {
        var first = Event(false);
        var events = new[] { first, Event(true), Event(true) };
        Assert.That(_storage.Ingest(_production, events, false, Now), Is.EqualTo(3));
        Assert.That(_storage.Ingest(_production, [first], false, Now), Is.Zero);
        Assert.That(_storage.Incidents(Filter(_production)), Is.Empty);
        Assert.That(_storage.IsReady(Now), Is.False);

        var restarted = new Storage(_options);
        restarted.AssertReady();
        Assert.That(restarted.Process(Now), Is.EqualTo(3));
        Assert.That(restarted.Process(Now), Is.Zero);
        Assert.That(restarted.IsReady(Now), Is.True);
        var incident = restarted.Incidents(Filter(_production)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(incident.Count, Is.EqualTo(2));
            Assert.That(incident.Status, Is.EqualTo("Open"));
            Assert.That(incident.Evidence, Does.StartWith("2 failures in 3 observations"));
            Assert.That(restarted.Incidents(Filter(_staging)), Is.Empty);
        });
    }

    [Test]
    public void InstanceSerializesConcurrentIngestWithoutDuplicates()
    {
        var item = Event(true);
        var results = Enumerable.Range(0, 4).AsParallel().Select(_ =>
            new Storage(_options).Ingest(_production, [item], false, Now)).ToArray();
        Assert.That(results.Sum(), Is.EqualTo(1));
        Assert.That(_storage.Process(Now), Is.EqualTo(1));
    }

    [Test]
    public void IncidentWritesRespectScopeAndPersistHistory()
    {
        _storage.Ingest(_production, [Event(false), Event(true), Event(true)], false, Now);
        _storage.Process(Now);
        var item = _storage.Incidents(Filter(_production)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(_storage.IncidentDetails(Filter(_staging), item.Id), Is.Null);
            Assert.That(_storage.ChangeIncident(Filter(_staging), item.Id, new("Resolved", null, null), "other", Now), Is.EqualTo(404));
            Assert.That(_storage.ChangeIncident(Filter(_production), item.Id, new("Acknowledged", "Inspect SAP", "Operations"), "test", Now), Is.EqualTo(204));
            Assert.That(_storage.ChangeIncident(Filter(_production), item.Id, new("Resolved", null, null), "test", Now), Is.EqualTo(204));
            Assert.That(_storage.ChangeIncident(Filter(_production), item.Id, new("Investigating", null, null), "test", Now), Is.EqualTo(409));
            Assert.That(_storage.ChangeIncident(Filter(_production), item.Id, new("Reopened", null, null), "test", Now), Is.EqualTo(204));
        });
        Assert.That(_storage.Incidents(Filter(_production)).Single().Assignee, Is.EqualTo("Operations"));
    }

    [Test]
    public void ScopeAuthorizationUsesExactGroupAndEnvironment()
    {
        var viewer = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.Role, "Developer"), new("groups", "group-s")], "test"));
        Assert.Multiple(() =>
        {
            Assert.That(Access.Visible(viewer, _options), Is.EqualTo(new[] { _staging }));
            Assert.That(Access.CanManage(viewer), Is.False);
            Assert.That(Access.Visible(new ClaimsPrincipal(), _options), Is.Empty);
        });
    }

    [Test]
    public void BrowserValidationRejectsArbitraryFieldsAndMetadata()
    {
        var item = Event(false);
        var startup = item with { Kind = "startup", Name = "PageLoaded", StatusCode = null, TraceId = null };
        Assert.Multiple(() =>
        {
            Assert.That(Validation.IsEvent(item, _production, Now, false), Is.True);
            Assert.That(Validation.IsEvent(item with { Name = "secret-value" }, _production, Now, false), Is.False);
            Assert.That(Validation.IsEvent(item with { Category = "secret-value" }, _production, Now, false), Is.False);
            Assert.That(Validation.IsEvent(item with { DurationMs = double.NaN }, _production, Now, false), Is.False);
            Assert.That(Validation.IsEvent(startup, _production, Now, true), Is.True);
            Assert.That(Validation.IsEvent(startup with { StatusCode = 200 }, _production, Now, true), Is.False);
            Assert.That(Validation.IsEvent(item, _production, Now, true), Is.False);
            Assert.That(Validation.IsEvent(item with { OccurredAt = Now.AddDays(-5) }, _production, Now, false), Is.False);
        });
    }

    [Test]
    public void RejectedBatchRollsBackAllRowsAtCapacity()
    {
        _options.InboxMaximum = 100;
        Assert.That(_storage.Ingest(_production, Enumerable.Range(0, 99).Select(_ => Event(false)), false, Now), Is.EqualTo(99));
        Assert.That(_storage.Ingest(_production, [Event(false), Event(true)], false, Now), Is.Null);
        Assert.That(_storage.Process(Now), Is.EqualTo(99));
    }

    [Test]
    public void BackupAndRestorePreservePendingRowsAndSchema()
    {
        var snapshot = Path.Combine(_directory, "snapshot.db");
        _storage.Ingest(_production, [Event(false)], false, Now);
        _storage.Backup(snapshot);
        Storage.VerifyBackup(snapshot);
        _storage.Ingest(_production, [Event(false)], false, Now);
        _storage.Restore(snapshot);
        _storage.AssertReady();
        Assert.That(_storage.Process(Now), Is.EqualTo(1));
    }

    [Test]
    public void BackupPreflightRejectsAnIntactSnapshotForAnotherSchema()
    {
        var snapshot = Path.Combine(_directory, "older-schema.db");
        _storage.Backup(snapshot);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = snapshot }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 4";
            command.ExecuteNonQuery();
        }

        Assert.DoesNotThrow(() => Storage.VerifyBackup(snapshot));
        Assert.Throws<InvalidOperationException>(() => Storage.VerifyBackup(snapshot, requireCurrentSchema: true));
    }

    [Test]
    public void WebhookTransitionOutboxSurvivesRetryAndLeasesAcrossWorkers()
    {
        _options.Webhooks = [new() { Name = "internal", Url = "https://example.invalid/alerts" }];
        _storage.Ingest(_production, [Event(false), Event(true), Event(true)], false, Now);
        _storage.Process(Now);
        var first = _storage.ClaimAlert(Now);
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.Status, Is.EqualTo("Open"));
        Assert.That(new Storage(_options).ClaimAlert(Now), Is.Null);
        Assert.That(_storage.Process(Now), Is.Zero);

        var reclaimed = _storage.ClaimAlert(Now.AddMinutes(3));
        Assert.That(reclaimed?.Id, Is.EqualTo(first.Id));
        Assert.That(reclaimed?.Attempt, Is.EqualTo(2));
        _storage.CompleteAlert(first, Now.AddMinutes(3), false, false);
        _storage.CompleteAlert(reclaimed!, Now.AddMinutes(3), true, false);
        Assert.That(_storage.ClaimAlert(Now.AddMinutes(6)), Is.Null);
    }

    [Test]
    public void DataCatBooleanSearchMatchesOnlyVisibleParameterizedEvents()
    {
        _storage.Ingest(_production, [Event(false), Event(true)], false, Now);
        _storage.Ingest(_staging, [Event(true)], false, Now);
        _storage.Process(Now);
        Assert.That(EventSearch.TryCompile("(outcome:failure OR outcome:degraded) AND status:>=500", out var search), Is.True);
        Assert.That((System.Collections.ICollection)_storage.Events(Filter(_production), search), Has.Count.EqualTo(1));
        Assert.That((System.Collections.ICollection)_storage.Events(Filter(_production) with { Scopes = [] }, search), Is.Empty);
        Assert.That(EventSearch.TryCompile("name:report OR", out _), Is.False);
        Assert.That(EventSearch.TryCompile("AND name:report", out _), Is.False);
        Assert.That(EventSearch.TryCompile("actor:report OR status:500", out _), Is.False);
    }

    [Test]
    public void RecoveryRequiresFullyObservedConsecutiveWindows()
    {
        _options.Rules[0].RecoveryPercent = 0;
        _storage.Ingest(_production, [Event(false), Event(true), Event(true)], false, Now);
        _storage.Process(Now);

        var firstWindow = Now.AddMinutes(5);
        _storage.Ingest(_production, Enumerable.Range(0, 3).Select(_ => Event(false) with
            { OccurredAt = firstWindow }), false, firstWindow);
        _storage.Process(firstWindow);
        Assert.That(_storage.Incidents(Filter(_production)).Single().Status, Is.EqualTo("Open"));

        var secondWindow = Now.AddMinutes(11);
        _storage.Ingest(_production, Enumerable.Range(0, 3).Select(_ => Event(false) with
            { OccurredAt = secondWindow }), false, secondWindow);
        _storage.Process(secondWindow);
        var filter = QueryFilter.From(new DefaultHttpContext().Request, [_production], secondWindow);
        Assert.That(_storage.Incidents(filter).Single().Status, Is.EqualTo("Resolved"));
    }

    [Test]
    public void OverviewSeparatesCancelledRequestsFromSuccessfulOnes()
    {
        _storage.Ingest(_production, [Event(false), Event(true), Event(false) with
            { Outcome = "cancelled", StatusCode = null }], false, Now);
        _storage.Process(Now);
        var overview = System.Text.Json.JsonSerializer.SerializeToElement(_storage.Overview(Filter(_production)));
        Assert.Multiple(() =>
        {
            Assert.That(overview.GetProperty("requests").GetInt64(), Is.EqualTo(3));
            Assert.That(overview.GetProperty("failedRequests").GetInt64(), Is.EqualTo(1));
            Assert.That(overview.GetProperty("successfulRequests").GetInt64(), Is.EqualTo(1));
        });
    }

    private static TelemetryEvent Event(bool failed) => new(Guid.NewGuid(), Now, "1.0.0", Guid.NewGuid(), "api", "report",
        failed ? "failure" : "success", 200, failed ? 503 : 200);

    private static AppScope Scope(string environment, string group) => new()
    {
        Application = "finance", Label = "Finance", Environment = environment, ViewerGroups = [group],
        AllowedNames = ["report"], IngestToken = "local-test-only"
    };

    private static QueryFilter Filter(AppScope scope) => QueryFilter.From(new DefaultHttpContext().Request, [scope], Now);
}
