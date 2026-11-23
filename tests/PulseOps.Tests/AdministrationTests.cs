using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed partial class AdministrationTests
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
        _directory = Path.Combine(Path.GetTempPath(), "pulseops-admin-" + Guid.NewGuid().ToString("N"));
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
    public void AuditEntriesFormAVerifiableHashChain()
    {
        long at = Now.ToUnixTimeMilliseconds();
        using (var connection = _storage.Open())
        using (var transaction = connection.BeginTransaction())
        {
            _storage.WriteAudit(connection, transaction, at, "admin-a", "config.proposed", "finance", "production", "change-1",
                """{"entity":"monitor","reason":"Planned <change> & \"review\""}""");
            _storage.WriteAudit(connection, transaction, at + 1, "opérateur", "audit.exported", "", "", "audit",
                """{"days":30,"entries":1}""");
            _storage.WriteAudit(connection, transaction, at + 2, "PulseOps", "config.expired", "finance", "staging", "change-2", "{}");
            transaction.Commit();
        }

        var rows = AuditRows();
        Assert.That(rows.Select(r => r.Seq), Is.EqualTo(new long[] { 1, 2, 3 }));
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].PreviousHash, Is.EqualTo(new string('0', 64)));
            Assert.That(rows[1].PreviousHash, Is.EqualTo(rows[0].Hash));
            Assert.That(rows[2].PreviousHash, Is.EqualTo(rows[1].Hash));
            foreach (var row in rows)
                Assert.That(row.Hash, Is.EqualTo(Canonical(row)), $"Entry {row.Seq} matches the canonical array hash.");
            Assert.That(Verify(), Is.EqualTo(new AuditChain(true, 3, rows[2].Hash, null)));
        });
    }

    [Test]
    public void AuditEntriesAreBoundedStructuredAndScoped()
    {
        using var connection = _storage.Open();
        using var transaction = connection.BeginTransaction();
        void Write(string actor = "admin-a", string action = "config.proposed", string application = "finance",
            string environment = "production", string target = "change-1", string detail = "{}") =>
            _storage.WriteAudit(connection, transaction, 0, actor, action, application, environment, target, detail);
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() => Write(actor: new string('a', 257)));
            Assert.Throws<ArgumentException>(() => Write(actor: ""));
            Assert.Throws<ArgumentException>(() => Write(actor: "admin\nforged"));
            Assert.Throws<ArgumentException>(() => Write(action: new string('a', 65)));
            Assert.Throws<ArgumentException>(() => Write(target: new string('a', 129)));
            Assert.Throws<ArgumentException>(() => Write(detail: "{\"value\":\"" + new string('a', 8192) + "\"}"));
            Assert.Throws<ArgumentException>(() => Write(detail: "free text note"));
            Assert.Throws<ArgumentException>(() => Write(detail: "[1,2]"));
            Assert.Throws<ArgumentException>(() => Write(application: "", environment: "production"));
            Assert.DoesNotThrow(() => Write(actor: new string('a', 256), action: new string('a', 64), target: new string('a', 128)));
        });
        transaction.Commit();
        Assert.That(AuditRows(), Has.Count.EqualTo(1));
    }

    [Test]
    public void TamperedEntriesBreakTheChainAtTheirSequence()
    {
        var alice = Administrator("admin-alice");
        for (int i = 0; i < 3; i++)
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy($"monitor-{i}")), Now).Status, Is.EqualTo(201));
        Assert.That(Verify().Verified, Is.True);

        Execute("DROP TRIGGER audit_log_append_only_update");
        Execute("UPDATE audit_log SET actor = 'mallory' WHERE seq = 2");
        Assert.That(Verify(), Is.EqualTo(new AuditChain(false, 3, AuditRows()[2].Hash, 2)));

        // Recomputing the altered entry's own hash still breaks the link held by the next entry.
        Execute("UPDATE audit_log SET hash = $hash WHERE seq = 2", ("$hash", Canonical(AuditRows()[1])));
        Assert.That(Verify().BrokenAtSeq, Is.EqualTo(3));
        Assert.That(_storage.AuditLog(alice, null, 100, 0)!.Chain.Verified, Is.False);

        var path = Path.Combine(_directory, "broken.ndjson");
        var refused = Assert.Throws<InvalidOperationException>(() => _storage.ExportAudit(path));
        Assert.Multiple(() =>
        {
            Assert.That(refused!.Message, Does.Contain("entry 3"));
            Assert.That(File.Exists(path), Is.False, "A broken chain produces no export file.");
        });
    }

    [Test]
    public void AuditPagesVerifyNewEntriesIncrementallyAndStillCatchAnAlteredAnchor()
    {
        var alice = Administrator("admin-alice");
        Assert.That(_storage.ProposeChange(alice, Upsert(Policy("monitor-0")), Now).Status, Is.EqualTo(201));
        Assert.That(_storage.AuditLog(alice, null, 100, 0)!.Chain, Is.EqualTo(Verify()), "First view verifies the full chain.");

        for (int i = 1; i < 4; i++)
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy($"monitor-{i}")), Now).Status, Is.EqualTo(201));
        var page = _storage.AuditLog(alice, null, 100, 0)!;
        Assert.That(page.Chain, Is.EqualTo(new AuditChain(true, 4, AuditRows()[3].Hash, null)), "Appended entries are verified.");

        // An edit of the last verified entry (possible only with the triggers dropped) is caught on the next view.
        Execute("DROP TRIGGER audit_log_append_only_update");
        Execute("UPDATE audit_log SET actor = 'mallory' WHERE seq = 4");
        Assert.That(_storage.AuditLog(alice, null, 100, 0)!.Chain.BrokenAtSeq, Is.EqualTo(4));
    }

    [Test]
    public void AuditLogRejectsUpdatesAndDeletes()
    {
        Assert.That(_storage.ProposeChange(Administrator("admin-alice"), Upsert(Policy()), Now).Status, Is.EqualTo(201));
        var update = Assert.Throws<SqliteException>(() => Execute("UPDATE audit_log SET actor = 'mallory'"));
        var delete = Assert.Throws<SqliteException>(() => Execute("DELETE FROM audit_log"));
        Assert.Multiple(() =>
        {
            Assert.That(update!.Message, Does.Contain("append-only"));
            Assert.That(delete!.Message, Does.Contain("append-only"));
            Assert.That(AuditRows(), Has.Count.EqualTo(1));
            Assert.That(Verify().Verified, Is.True);
        });
    }

    [Test]
    public void SecondAdministratorAppliesAProposedMonitor()
    {
        var alice = Administrator("admin-alice");
        var bob = Administrator("admin-bob");
        var proposed = _storage.ProposeChange(alice, Upsert(Policy()), Now);
        Assert.That(proposed.Status, Is.EqualTo(201));
        var id = proposed.Id!;
        var pending = _storage.Changes(alice, true, 50, 0)!.Single();
        Assert.Multiple(() =>
        {
            Assert.That((pending.Id, pending.Status, pending.Target, pending.BaseRevision), Is.EqualTo((id, "Pending", "report-p95", (long?)null)));
            Assert.That(pending.CanApprove, Is.False, "Self-approval is off.");
            Assert.That(pending.CanWithdraw, Is.True);
            Assert.That(_storage.Changes(bob, true, 50, 0)!.Single().CanApprove, Is.True);
        });

        Assert.That(_storage.ApproveChange(alice, id, "mine", Now.AddMinutes(1)), Is.EqualTo(new ChangeOutcome(403, "self_approval")));
        Assert.That(Scalar("SELECT COUNT(*) FROM monitor_policies"), Is.Zero);
        Assert.That(_storage.ApproveChange(bob, id, "Matches the latency objective", Now.AddMinutes(2)), Is.EqualTo(ChangeOutcome.Done));

        var payload = JsonSerializer.Serialize(Policy(), JsonSerializerOptions.Web);
        var applied = _storage.Changes(bob, false, 50, 0)!.Single();
        var audit = AuditRows();
        Assert.Multiple(() =>
        {
            Assert.That(Text("SELECT definition FROM monitor_policies WHERE name = 'report-p95'"), Is.EqualTo(payload));
            Assert.That(Text("SELECT revision || '/' || updated_by || '/' || change_id FROM monitor_policies"),
                Is.EqualTo("1/admin-alice/" + id));
            Assert.That(Scalar("SELECT updated FROM monitor_policies"), Is.EqualTo(Now.AddMinutes(2).ToUnixTimeMilliseconds()));
            Assert.That((applied.Status, applied.Decider, applied.DecisionNote, applied.DecidedAt),
                Is.EqualTo(("Applied", "admin-bob", "Matches the latency objective", (DateTimeOffset?)Now.AddMinutes(2))));
            Assert.That(applied.Payload.GetProperty("thresholdMs").GetDouble(), Is.EqualTo(800));
            Assert.That(applied.CanApprove || applied.CanWithdraw, Is.False);
            Assert.That(audit.Select(e => (e.Action, e.Actor, e.Target)),
                Is.EqualTo(new[] { ("config.proposed", "admin-alice", id), ("config.approved", "admin-bob", id) }));
            var proposedDetail = JsonDocument.Parse(audit[0].Detail).RootElement;
            Assert.That(proposedDetail.GetProperty("payloadSha256").GetString(),
                Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))));
            Assert.That(proposedDetail.TryGetProperty("reason", out _), Is.False,
                "Free-text reasons stay in config_changes; the immutable chain never holds text that may need redaction.");
            Assert.That(applied.Reason, Is.EqualTo("Tighten the report latency objective"));
            Assert.That(audit[1].Detail, Is.EqualTo("""{"entity":"monitor","operation":"upsert","target":"report-p95","revision":1}"""));
            Assert.That(Verify().Verified, Is.True);
        });
        Assert.That(_storage.ApproveChange(bob, id, null, Now.AddMinutes(3)), Is.EqualTo(ChangeOutcome.Conflict("not_pending")));

        // The next proposal is based on revision 1 and the other administrator applies revision 2.
        var update = Policy();
        update.ThresholdMs = 700;
        var second = _storage.ProposeChange(bob, Upsert(update, "Lower the threshold after tuning"), Now.AddMinutes(4)).Id!;
        Assert.That(Change(second).BaseRevision, Is.EqualTo(1));
        Assert.That(_storage.ApproveChange(alice, second, null, Now.AddMinutes(5)), Is.EqualTo(ChangeOutcome.Done));
        Assert.That(Text("SELECT revision || '/' || updated_by FROM monitor_policies"), Is.EqualTo("2/admin-bob"));

        // Deleting removes the definition and evaluation state; the delete records the revision it removed.
        Execute("INSERT INTO monitor_state(application, environment, name, state, since, evaluated) VALUES ('finance', 'production', 'report-p95', 'OK', 0, 0)");
        var delete = _storage.ProposeChange(alice, new("monitor", "delete", null, null, "finance", "production", "report-p95",
            "Replaced by the shared monitor"), Now.AddMinutes(6)).Id!;
        Assert.That(_storage.ApproveChange(bob, delete, null, Now.AddMinutes(7)), Is.EqualTo(ChangeOutcome.Done));
        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT COUNT(*) FROM monitor_policies") + Scalar("SELECT COUNT(*) FROM monitor_state"), Is.Zero);
            Assert.That(AuditRows()[^1].Detail, Is.EqualTo("""{"entity":"monitor","operation":"delete","target":"report-p95","revision":2}"""));
        });
    }

    [Test]
    public void SelfApprovalWorksOnlyWhenEnabled()
    {
        _options.Administration.AllowSelfApproval = true;
        var alice = Administrator("admin-alice");
        var proposed = _storage.ProposeChange(alice, Upsert(Policy()), Now).Id!;
        Assert.That(_storage.Changes(alice, true, 50, 0)!.Single().CanApprove, Is.True);
        Assert.That(_storage.ApproveChange(alice, proposed, "Local demo", Now), Is.EqualTo(ChangeOutcome.Done));
        Assert.That(AuditRows().Select(e => (e.Action, e.Actor)),
            Is.EqualTo(new[] { ("config.proposed", "admin-alice"), ("config.approved", "admin-alice") }));
    }

    [Test]
    public void MovedBaseRevisionSupersedesTheChange()
    {
        var (alice, bob) = (Administrator("admin-alice"), Administrator("admin-bob"));
        Apply(alice, bob, Upsert(Policy()));
        var update = Policy();
        update.ThresholdMs = 650;
        var proposed = _storage.ProposeChange(alice, Upsert(update), Now.AddMinutes(1)).Id!;
        Execute("UPDATE monitor_policies SET revision = 2"); // a concurrent restore or apply moved the monitor

        Assert.That(_storage.ApproveChange(bob, proposed, null, Now.AddMinutes(2)), Is.EqualTo(ChangeOutcome.Conflict("stale")));
        var change = Change(proposed);
        var last = AuditRows()[^1];
        Assert.Multiple(() =>
        {
            Assert.That((change.Status, change.Decider), Is.EqualTo(("Superseded", "admin-bob")));
            Assert.That(Text("SELECT revision || definition FROM monitor_policies"), Does.StartWith("2").And.Contain("\"thresholdMs\":800"));
            Assert.That((last.Action, last.Actor, last.Target), Is.EqualTo(("config.superseded", "admin-bob", proposed)));
            Assert.That(last.Detail, Does.Contain("\"cause\":\"stale\"").And.Contain("\"baseRevision\":1").And.Contain("\"currentRevision\":2"));
            Assert.That(Verify().Verified, Is.True);
        });
        Assert.That(_storage.ApproveChange(bob, proposed, null, Now.AddMinutes(3)), Is.EqualTo(ChangeOutcome.Conflict("not_pending")));
    }

    [Test]
    public void ConfigurationManagedMonitorsCannotBeChanged()
    {
        var (alice, bob) = (Administrator("admin-alice"), Administrator("admin-bob"));
        _options.Monitors.Add(Policy("managed-p95"));
        Assert.That(_storage.ProposeChange(alice, Upsert(Policy("managed-p95")), Now),
            Is.EqualTo(ChangeOutcome.Conflict("configuration_managed")));

        // A key that becomes configuration-managed before approval is superseded instead of applied.
        var proposed = _storage.ProposeChange(alice, Upsert(Policy("later-managed")), Now).Id!;
        _options.Monitors.Add(Policy("later-managed"));
        Assert.That(_storage.ApproveChange(bob, proposed, null, Now), Is.EqualTo(ChangeOutcome.Conflict("invalid_now")));
        Assert.Multiple(() =>
        {
            Assert.That((Change(proposed).Status, Change(proposed).DecisionNote),
                Is.EqualTo(("Superseded", "The monitor is now managed by configuration.")));
            Assert.That(Scalar("SELECT COUNT(*) FROM monitor_policies"), Is.Zero);
            Assert.That(AuditRows().Select(e => e.Action), Is.EqualTo(new[] { "config.proposed", "config.superseded" }));
        });

        // A scope removed from configuration also invalidates its pending change.
        var staged = _storage.ProposeChange(alice, Upsert(Policy("staging-p95", _staging)), Now).Id!;
        _options.Scopes.Remove(_staging);
        Assert.That(_storage.ApproveChange(bob, staged, null, Now), Is.EqualTo(ChangeOutcome.Conflict("invalid_now")));
        Assert.That(Change(staged).DecisionNote, Is.EqualTo("The application environment is no longer registered."));
    }

    [Test]
    public void InvalidProposalsAreRejectedWithoutSideEffects()
    {
        var alice = Administrator("admin-alice");
        var invalid = Policy();
        invalid.ThresholdMs = 0;
        Assert.Multiple(() =>
        {
            Assert.That(_storage.ProposeChange(alice, Upsert(invalid), Now),
                Is.EqualTo(ChangeOutcome.Invalid(invalid.Validate(_options.Scopes)!)));
            Assert.That(_storage.ProposeChange(alice, new("monitor", "create", Policy(), null, null, null, null, "Valid reason"), Now).Error,
                Is.EqualTo("invalid_change"));
            Assert.That(_storage.ProposeChange(alice, new("monitor", "upsert", Policy(), null, "finance", "staging", null, "Mismatched scope"), Now).Error,
                Is.EqualTo("invalid_change"));
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy(), "no"), Now).Error, Is.EqualTo("invalid_change"));
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy(), "line one\nline two"), Now).Error, Is.EqualTo("invalid_change"));
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy(), new string('r', 501)), Now).Error, Is.EqualTo("invalid_change"));
            Assert.That(_storage.ProposeChange(alice, new("monitor", "delete", null, null, "finance", "production", "absent", "Remove it"), Now),
                Is.EqualTo(ChangeOutcome.Conflict("not_found")));
            Assert.That(Scalar("SELECT COUNT(*) FROM config_changes") + Scalar("SELECT COUNT(*) FROM audit_log"), Is.Zero);
        });
    }

    [Test]
    public void ProposalsRequireAVisibleScopeAndAProposerRole()
    {
        var stagingOwner = Owner("owner-s", "group-s");
        var unregistered = Policy();
        unregistered.Application = "payments";
        Assert.Multiple(() =>
        {
            Assert.That(_storage.ProposeChange(stagingOwner, Upsert(Policy()), Now), Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.ProposeChange(stagingOwner, new("monitor", "delete", null, null, "finance", "production",
                "report-p95", "Remove it"), Now), Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.ProposeChange(stagingOwner, Maintenance(_production, Now.AddHours(1), Now.AddHours(2)), Now),
                Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.ProposeChange(Administrator("admin-alice"), Upsert(unregistered), Now), Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.ProposeChange(User("developer-s", "Developer", "group-s"), Upsert(Policy(scope: _staging)), Now),
                Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.ProposeChange(Auditor("auditor-s", "group-s"), Upsert(Policy(scope: _staging)), Now),
                Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.ProposeChange(stagingOwner, Upsert(Policy(scope: _staging)), Now).Status, Is.EqualTo(201));
        });
    }

    [Test]
    public void RejectAndWithdrawFollowRolesAndOwnership()
    {
        var owner = Owner("owner-s", "group-s");
        var colleague = Owner("owner-s2", "group-s");
        var outsider = Owner("owner-p", "group-p");
        var auditor = Auditor("auditor-s", "group-s");
        var alice = Administrator("admin-alice");

        var first = _storage.ProposeChange(owner, Upsert(Policy("first", _staging)), Now).Id!;
        Assert.Multiple(() =>
        {
            Assert.That(_storage.RejectChange(owner, first, null, Now), Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.ApproveChange(owner, first, null, Now), Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.RejectChange(alice, first, new string('x', 501), Now).Error, Is.EqualTo("invalid_change"));
            Assert.That(_storage.RejectChange(alice, Guid.NewGuid().ToString("D"), null, Now), Is.EqualTo(ChangeOutcome.Missing));
        });
        Assert.That(_storage.RejectChange(alice, first, "Use the shared latency monitor", Now.AddMinutes(1)), Is.EqualTo(ChangeOutcome.Done));
        var rejected = Change(first);
        Assert.Multiple(() =>
        {
            Assert.That((rejected.Status, rejected.Decider, rejected.DecisionNote),
                Is.EqualTo(("Rejected", "admin-alice", "Use the shared latency monitor")));
            Assert.That(_storage.RejectChange(alice, first, null, Now.AddMinutes(2)), Is.EqualTo(ChangeOutcome.Conflict("not_pending")));
            Assert.That(_storage.WithdrawChange(owner, first, Now.AddMinutes(2)), Is.EqualTo(ChangeOutcome.Conflict("not_pending")));
        });

        var second = _storage.ProposeChange(owner, Upsert(Policy("second", _staging)), Now.AddMinutes(3)).Id!;
        Assert.Multiple(() =>
        {
            Assert.That(_storage.WithdrawChange(colleague, second, Now), Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.WithdrawChange(alice, second, Now), Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.WithdrawChange(auditor, second, Now), Is.EqualTo(ChangeOutcome.Forbidden));
            Assert.That(_storage.WithdrawChange(outsider, second, Now), Is.EqualTo(ChangeOutcome.Missing));
            Assert.That(_storage.Changes(colleague, true, 50, 0)!.Single().CanWithdraw, Is.False);
            Assert.That(_storage.Changes(owner, true, 50, 0)!.Single().CanWithdraw, Is.True);
        });
        Assert.That(_storage.WithdrawChange(owner, second, Now.AddMinutes(4)), Is.EqualTo(ChangeOutcome.Done));
        var audit = AuditRows();
        Assert.Multiple(() =>
        {
            Assert.That((Change(second).Status, Change(second).Decider), Is.EqualTo(("Withdrawn", "owner-s")));
            Assert.That(audit.Select(e => (e.Action, e.Actor)), Is.EqualTo(new[]
            {
                ("config.proposed", "owner-s"), ("config.rejected", "admin-alice"),
                ("config.proposed", "owner-s"), ("config.withdrawn", "owner-s")
            }));
            Assert.That(audit[1].Detail, Is.EqualTo("""{"entity":"monitor","operation":"upsert","target":"first"}"""),
                "Decision notes stay out of the audit detail.");
            Assert.That(_storage.Changes(User("support-s", "ProductionSupport", "group-s"), false, 50, 0), Is.Null);
            Assert.That(_storage.Changes(outsider, false, 50, 0), Is.Empty);
            Assert.That(_storage.Changes(auditor, false, 50, 0)!.Select(c => c.Status), Is.EqualTo(new[] { "Withdrawn", "Rejected" }));
        });
    }

    [Test]
    public void StalePendingChangesExpireInBoundedAuditedBatches()
    {
        _options.Administration.MaximumPendingChanges = 200;
        var alice = Administrator("admin-alice");
        for (int i = 0; i < 101; i++)
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy($"monitor-{i:000}")), Now).Status, Is.EqualTo(201));
        var fresh = _storage.ProposeChange(alice, Upsert(Policy("fresh")), Now.AddHours(60)).Id!;

        var expiry = Now.AddHours(_options.Administration.ChangeExpiryHours).AddMinutes(1);
        _storage.Retain(expiry);
        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT COUNT(*) FROM config_changes WHERE status = 'Pending'"), Is.EqualTo(2));
            Assert.That(Scalar($"SELECT COUNT(*) FROM config_changes WHERE status = 'Expired' AND decider = 'PulseOps' AND decided = {expiry.ToUnixTimeMilliseconds()}"),
                Is.EqualTo(100));
            Assert.That(Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'config.expired' AND actor = 'PulseOps'"), Is.EqualTo(100));
        });
        _storage.Retain(expiry);
        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT COUNT(*) FROM config_changes WHERE status = 'Expired'"), Is.EqualTo(101));
            Assert.That(Change(fresh).Status, Is.EqualTo("Pending"), "Younger changes stay pending.");
            Assert.That(Verify().Verified, Is.True);
        });

        // Expiry is applied before every decision, so an overdue change cannot be approved even before retention runs.
        var bob = Administrator("admin-bob");
        Assert.That(_storage.ApproveChange(bob, Text("SELECT id FROM config_changes WHERE status = 'Expired' LIMIT 1"), null, expiry),
            Is.EqualTo(ChangeOutcome.Conflict("not_pending")));
        Assert.That(_storage.ApproveChange(bob, fresh, null, Now.AddHours(140)), Is.EqualTo(ChangeOutcome.Conflict("not_pending")));
        Assert.That(Change(fresh).Status, Is.EqualTo("Expired"));
    }

    [Test]
    public void PendingChangesAreLimitedAndConflictFree()
    {
        _options.Administration.MaximumPendingChanges = 3;
        var alice = Administrator("admin-alice");
        Assert.Multiple(() =>
        {
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy()), Now).Status, Is.EqualTo(201));
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy()), Now), Is.EqualTo(ChangeOutcome.Conflict("conflict")));
            Assert.That(_storage.ProposeChange(alice, Maintenance(_production, Now, Now.AddHours(1)), Now).Status, Is.EqualTo(201));
            Assert.That(_storage.ProposeChange(alice, Maintenance(_production, Now.AddDays(1), Now.AddDays(1).AddHours(1)), Now),
                Is.EqualTo(ChangeOutcome.Conflict("conflict")), "One pending window creation per scope.");
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy("other")), Now).Status, Is.EqualTo(201));
            Assert.That(_storage.ProposeChange(alice, Upsert(Policy("fourth")), Now), Is.EqualTo(ChangeOutcome.Conflict("too_many_pending")));
        });
    }

    [Test]
    public void MaintenanceWindowsAreCreatedAndCancelledThroughChangeControl()
    {
        var owner = Owner("owner-p", "group-p");
        var alice = Administrator("admin-alice");
        var starts = Now.AddHours(1);
        var ends = Now.AddHours(3);
        var create = _storage.ProposeChange(owner, Maintenance(_production, starts, ends), Now).Id!;
        Assert.That(Change(create).Target, Is.Empty, "The window id is assigned on approval.");
        Assert.That(Change(create).Payload.GetProperty("reason").GetString(), Is.EqualTo("Synthetic SAP kernel upgrade"));
        Assert.That(_storage.ApproveChange(alice, create, null, Now.AddMinutes(5)), Is.EqualTo(ChangeOutcome.Done));

        var window = _storage.MaintenanceWindows(owner, false, Now).Single();
        Assert.Multiple(() =>
        {
            Assert.That((window.Application, window.Environment, window.StartsAt, window.EndsAt, window.Reason),
                Is.EqualTo(("finance", "production", starts, ends, "Synthetic SAP kernel upgrade")));
            Assert.That((window.CreatedBy, window.ChangeId, window.CancelledAt, window.Active),
                Is.EqualTo(("owner-p", create, (DateTimeOffset?)null, false)));
            Assert.That(Change(create).Target, Is.EqualTo(window.Id));
            Assert.That(AuditRows()[^1].Detail,
                Is.EqualTo($$"""{"entity":"maintenance","operation":"create","target":"{{window.Id}}","windowId":"{{window.Id}}"}"""));
            Assert.That(_storage.MaintenanceWindows(owner, false, starts).Single().Active, Is.True);
            Assert.That(_storage.MaintenanceWindows(User("developer-p", "Developer", "group-p"), false, Now), Has.Count.EqualTo(1));
            Assert.That(_storage.MaintenanceWindows(User("developer-s", "Developer", "group-s"), true, Now), Is.Empty);
        });

        Assert.Multiple(() =>
        {
            Assert.That(_storage.ActiveMaintenance("finance", "production", starts.AddMilliseconds(-1)), Is.Null);
            Assert.That(_storage.ActiveMaintenance("finance", "production", starts)?.Id, Is.EqualTo(window.Id));
            Assert.That(_storage.ActiveMaintenance("finance", "production", ends.AddMilliseconds(-1))?.Reason,
                Is.EqualTo("Synthetic SAP kernel upgrade"));
            Assert.That(_storage.ActiveMaintenance("finance", "production", ends), Is.Null, "The end instant is exclusive.");
            Assert.That(_storage.ActiveMaintenance("finance", "staging", starts), Is.Null);
        });

        var cancel = _storage.ProposeChange(owner, new("maintenance", "cancel", null, null, "finance", "production", window.Id,
            "Upgrade postponed"), Now.AddMinutes(10)).Id!;
        Assert.That(_storage.ApproveChange(alice, cancel, null, starts.AddMinutes(30)), Is.EqualTo(ChangeOutcome.Done));
        var cancelled = _storage.MaintenanceWindows(owner, true, Now).Single();
        Assert.Multiple(() =>
        {
            Assert.That(_storage.ActiveMaintenance("finance", "production", starts.AddMinutes(31)), Is.Null);
            Assert.That(_storage.MaintenanceWindows(owner, false, Now), Is.Empty);
            Assert.That((cancelled.CancelledAt, cancelled.Active), Is.EqualTo(((DateTimeOffset?)starts.AddMinutes(30), false)));
            Assert.That(_storage.ProposeChange(owner, new("maintenance", "cancel", null, null, "finance", "production", window.Id,
                "Cancel again"), starts.AddMinutes(40)), Is.EqualTo(ChangeOutcome.Conflict("not_found")));
            Assert.That(Verify().Verified, Is.True);
        });
    }

    [Test]
    public void MaintenanceRequestsAreBounded()
    {
        var owner = Owner("owner-p", "group-p");
        string Detail(ChangeProposal proposal) => _storage.ProposeChange(owner, proposal, Now).Detail ?? "";
        Assert.Multiple(() =>
        {
            Assert.That(Detail(Maintenance(_production, Now.AddHours(2), Now.AddHours(1))), Is.EqualTo("Maintenance must end after it starts."));
            Assert.That(Detail(Maintenance(_production, Now.AddHours(-2), Now)), Is.EqualTo("Maintenance must end in the future."));
            Assert.That(Detail(Maintenance(_production, Now, Now.AddDays(7).AddMinutes(1))), Is.EqualTo("Maintenance windows are limited to 7 days."));
            Assert.That(Detail(Maintenance(_production, Now.AddDays(91), Now.AddDays(91).AddHours(1))), Is.EqualTo("Maintenance must start within 90 days."));
            Assert.That(Detail(Maintenance(_production, Now, Now.AddHours(1), "no")), Does.StartWith("A maintenance reason"));
            Assert.That(Detail(Maintenance(_production, Now, Now.AddHours(1), new string('r', 201))), Does.StartWith("A maintenance reason"));
            Assert.That(Detail(Maintenance(_production, Now, Now.AddHours(1)) with { Environment = "staging" }), Does.StartWith("Use monitor upsert"));
            Assert.That(Detail(Maintenance(_production, Now, Now.AddHours(1)) with { Target = "window" }), Does.StartWith("Use monitor upsert"));
            Assert.That(Detail(new("maintenance", "cancel", null, null, "finance", "production", "not-a-window-id", "Cancel it")),
                Does.StartWith("Cancelling maintenance"));
            Assert.That(_storage.ProposeChange(owner, new("maintenance", "cancel", null, null, "finance", "production",
                Guid.NewGuid().ToString("D"), "Cancel it"), Now), Is.EqualTo(ChangeOutcome.Conflict("not_found")));
            Assert.That(_storage.ProposeChange(owner, Maintenance(_production, Now.AddDays(90), Now.AddDays(97)), Now).Status,
                Is.EqualTo(201), "The 90-day lead and 7-day duration bounds are inclusive.");
        });
    }

    [Test]
    public void ApprovedMaintenanceDefersNotificationsUntilItEnds()
    {
        _options.Webhooks = [new() { Name = "internal", Url = "https://example.invalid/alerts" }];
        var (alice, bob) = (Administrator("admin-alice"), Administrator("admin-bob"));
        var windowId = Change(Apply(alice, bob, Maintenance(_production, Now, Now.AddMinutes(30)))).Target;

        var during = Now.AddMinutes(5);
        _storage.Ingest(_production, [Event(false, during), Event(true, during), Event(true, during)], false, during);
        _storage.Process(during);
        var incident = _storage.Incidents(Filter(_production, during)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(incident.Status, Is.EqualTo("Open"));
            Assert.That(Text("SELECT group_concat(delivery_state) FROM alert_deliveries"), Is.EqualTo("deferred"));
            Assert.That(Text("SELECT group_concat(note, '|') FROM incident_history WHERE incident_id = $id", ("$id", incident.Id)),
                Does.Contain($"Notifications deferred until maintenance window {windowId} ends."));
            Assert.That(_storage.ClaimAlert(during), Is.Null, "Nothing is delivered during the window.");
        });

        // Still failing after the window: the deferred Open is released, not lost.
        var after = Now.AddMinutes(31);
        _storage.Retain(after);
        var opened = _storage.ClaimAlert(after);
        Assert.That((opened?.IncidentId, opened?.Status), Is.EqualTo((incident.Id, "Open")));
        _storage.CompleteAlert(opened!, after, true, false);

        Assert.That(_storage.ChangeIncident(Filter(_production, after), incident.Id, new("Resolved", null, null), "operator-1", after),
            Is.EqualTo(204));
        var resolved = _storage.ClaimAlert(after);
        Assert.That((resolved?.Status, resolved?.Sequence > opened!.Sequence), Is.EqualTo(("Resolved", (bool?)true)));
    }

    [Test]
    public void IncidentOpenedAndResolvedInsideMaintenanceNotifiesNobody()
    {
        _options.Webhooks = [new() { Name = "internal", Url = "https://example.invalid/alerts" }];
        var (alice, bob) = (Administrator("admin-alice"), Administrator("admin-bob"));
        Apply(alice, bob, Maintenance(_production, Now, Now.AddMinutes(30)));

        var during = Now.AddMinutes(5);
        _storage.Ingest(_production, [Event(false, during), Event(true, during), Event(true, during)], false, during);
        _storage.Process(during);
        var incident = _storage.Incidents(Filter(_production, during)).Single();
        var resolvedAt = Now.AddMinutes(10);
        Assert.That(_storage.ChangeIncident(Filter(_production, resolvedAt), incident.Id, new("Resolved", null, null), "operator-1",
            resolvedAt), Is.EqualTo(204));

        _storage.Retain(Now.AddMinutes(31));
        Assert.Multiple(() =>
        {
            Assert.That(_storage.ClaimAlert(Now.AddMinutes(31)), Is.Null, "The receiver was never paged, so it gets no resolution.");
            Assert.That(Text("SELECT group_concat(status || ':' || delivery_state) FROM alert_deliveries"), Is.EqualTo("Open:suppressed"));
        });
    }

    [Test]
    public void ResolutionDuringMaintenanceClosesAnAlertSentBeforeIt()
    {
        _options.Webhooks = [new() { Name = "internal", Url = "https://example.invalid/alerts" }];
        _storage.Ingest(_production, [Event(false, Now), Event(true, Now), Event(true, Now)], false, Now);
        _storage.Process(Now);
        var opened = _storage.ClaimAlert(Now);
        _storage.CompleteAlert(opened!, Now, true, false);
        var incident = _storage.Incidents(Filter(_production, Now)).Single();

        var (alice, bob) = (Administrator("admin-alice"), Administrator("admin-bob"));
        Apply(alice, bob, Maintenance(_production, Now.AddMinutes(1), Now.AddMinutes(60)));
        var during = Now.AddMinutes(10);
        Assert.That(_storage.ChangeIncident(Filter(_production, during), incident.Id, new("Resolved", null, null), "operator-1", during),
            Is.EqualTo(204));
        Assert.That(_storage.ClaimAlert(during)?.Status, Is.EqualTo("Resolved"), "A page that was sent also gets its resolution.");
    }

    [Test]
    public void IncidentUpdatesAreAuditedWithoutNoteOrAssigneeText()
    {
        _storage.Ingest(_production, [Event(false, Now), Event(true, Now), Event(true, Now)], false, Now);
        _storage.Process(Now);
        var incident = _storage.Incidents(Filter(_production, Now)).Single();
        const string note = "synthetic-note-text-must-not-be-audited";
        Assert.Multiple(() =>
        {
            Assert.That(_storage.ChangeIncident(Filter(_production, Now), incident.Id, new("Acknowledged", note, "synthetic-assignee"),
                "operator-1", Now), Is.EqualTo(204));
            Assert.That(_storage.ChangeIncident(Filter(_production, Now), incident.Id, new("Resolved", null, null),
                "operator-2", Now.AddMinutes(1)), Is.EqualTo(204));
            Assert.That(_storage.ChangeIncident(Filter(_production, Now), incident.Id, new("Investigating", null, null),
                "operator-2", Now.AddMinutes(2)), Is.EqualTo(409));
            Assert.That(_storage.ChangeIncident(Filter(_staging, Now), incident.Id, new("Reopened", null, null),
                "operator-3", Now.AddMinutes(2)), Is.EqualTo(404));
        });

        var audit = AuditRows();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Select(e => (e.Action, e.Actor, e.Application, e.Environment, e.Target)), Is.EqualTo(new[]
            {
                ("incident.updated", "operator-1", "finance", "production", incident.Id),
                ("incident.updated", "operator-2", "finance", "production", incident.Id)
            }), "Rejected and unseen changes write no audit entry.");
            Assert.That(audit[0].Detail, Is.EqualTo("""{"from":"Open","to":"Acknowledged","assigneeChanged":true,"noteProvided":true}"""));
            Assert.That(audit[1].Detail, Is.EqualTo("""{"from":"Acknowledged","to":"Resolved","assigneeChanged":false,"noteProvided":false}"""));
            Assert.That(audit.Any(e => e.Detail.Contains(note) || e.Detail.Contains("synthetic-assignee")), Is.False);
            Assert.That(Verify().Verified, Is.True);
        });
    }

    [Test]
    public void ExportWritesAVerifiableNdjsonChainAndNeverOverwrites()
    {
        var empty = Path.Combine(_directory, "empty.ndjson");
        Assert.That(_storage.ExportAudit(empty), Is.EqualTo((0L, new string('0', 64))));
        Assert.That(File.ReadAllBytes(empty), Is.Empty);

        var (alice, bob) = (Administrator("admin-alice"), Administrator("admin-bób"));
        Apply(alice, bob, Upsert(Policy(), "Raise <threshold> & \"review\" für Prüfung"));
        Apply(alice, bob, Maintenance(_production, Now.AddHours(1), Now.AddHours(2)));
        var path = Path.Combine(_directory, "audit.ndjson");
        var (count, head) = _storage.ExportAudit(path);

        var lines = File.ReadAllLines(path);
        Assert.That((count, lines.Length), Is.EqualTo((4L, 4)));
        string previous = new('0', 64);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = JsonDocument.Parse(lines[i]).RootElement;
            string Field(string name) => line.GetProperty(name).GetString()!;
            var entry = new AuditEntry(line.GetProperty("seq").GetInt64(),
                DateTimeOffset.FromUnixTimeMilliseconds(line.GetProperty("atUnixMs").GetInt64()), Field("actor"), Field("action"),
                Field("application"), Field("environment"), Field("target"), Field("detail"), Field("previousHash"), Field("hash"));
            Assert.Multiple(() =>
            {
                Assert.That(entry.Seq, Is.EqualTo(i + 1));
                Assert.That(line.GetProperty("at").GetDateTimeOffset(), Is.EqualTo(entry.At));
                Assert.That(entry.PreviousHash, Is.EqualTo(previous));
                Assert.That(entry.Hash, Is.EqualTo(Canonical(entry)), $"Exported entry {entry.Seq} recomputes.");
            });
            previous = entry.Hash;
        }
        Assert.That(previous, Is.EqualTo(head));
        Assert.That(head, Is.EqualTo(Verify().HeadHash));
        Assert.That(lines[0], Does.Contain("admin-alice"));
        Assert.That(JsonDocument.Parse(lines[1]).RootElement.GetProperty("actor").GetString(), Is.EqualTo("admin-bób"));

        var before = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => _storage.ExportAudit(path));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "An existing export is never overwritten.");
    }

    [Test]
    public async Task AuditorsReadAndExportOnlyVisibleScopes()
    {
        var (alice, bob) = (Administrator("admin-alice"), Administrator("admin-bob"));
        var stagingOwner = Owner("owner-s", "group-s");
        var auditor = Auditor("auditor-s", "group-s");
        Apply(alice, bob, Upsert(Policy()));
        Assert.That(_storage.ProposeChange(stagingOwner, Upsert(Policy("staging-p95", _staging)), Now).Status, Is.EqualTo(201));
        Assert.That(_storage.RecordAuditExport(alice, 30, Now.AddMinutes(1)), Is.Not.Null);

        var page = _storage.AuditLog(auditor, null, 100, 0)!;
        Assert.Multiple(() =>
        {
            Assert.That(page.Chain, Is.EqualTo(Verify()), "The chain summary covers the full log.");
            Assert.That(page.Chain.Entries, Is.EqualTo(4));
            Assert.That(page.Entries.Select(e => (e.Action, e.Environment)), Is.EqualTo(new[] { ("config.proposed", "staging") }));
            Assert.That(_storage.AuditLog(alice, null, 100, 0)!.Entries.Select(e => e.Seq), Is.EqualTo(new long[] { 4, 3, 2, 1 }));
            Assert.That(_storage.AuditLog(alice, null, 2, 1)!.Entries.Select(e => e.Seq), Is.EqualTo(new long[] { 3, 2 }));
            Assert.That(_storage.AuditLog(alice, "audit.exported", 100, 0)!.Entries.Single().Application, Is.Empty);
            Assert.That(_storage.AuditLog(User("developer-s", "Developer", "group-s"), null, 100, 0), Is.Null);
            Assert.That(_storage.AuditLog(stagingOwner, null, 100, 0), Is.Null);
            Assert.That(_storage.RecordAuditExport(User("developer-s", "Developer", "group-s"), 30, Now), Is.Null);
        });

        var export = _storage.RecordAuditExport(auditor, 30, Now.AddMinutes(2))!;
        var lines = await Stream(export);
        var recorded = AuditRows()[^1];
        Assert.Multiple(() =>
        {
            Assert.That(export.Entries, Is.EqualTo(1));
            Assert.That(lines.Select(line => line.GetProperty("environment").GetString()), Is.EqualTo(new[] { "staging" }));
            Assert.That((recorded.Action, recorded.Actor, recorded.Application, recorded.Target, recorded.Detail),
                Is.EqualTo(("audit.exported", "auditor-s", "", "audit", """{"days":30,"entries":1}""")));
            Assert.That(_storage.AuditLog(auditor, null, 100, 0)!.Entries, Has.Count.EqualTo(1),
                "Platform-level export entries stay administrator-only.");
        });

        // An administrator's export covers every scope and platform-level entry committed before it, oldest first.
        var full = _storage.RecordAuditExport(alice, 30, Now.AddMinutes(3))!;
        Assert.That(full.Entries, Is.EqualTo(5));
        Assert.That((await Stream(full)).Select(line => line.GetProperty("seq").GetInt64()), Is.EqualTo(new long[] { 1, 2, 3, 4, 5 }));
        var recent = _storage.RecordAuditExport(alice, 1, Now.AddDays(2))!;
        Assert.That((recent.Entries, (await Stream(recent)).Count), Is.EqualTo((0L, 0)), "Only the requested days are exported.");
        Assert.That(Verify(), Is.EqualTo(new AuditChain(true, 7, AuditRows()[^1].Hash, null)));
    }

    private async Task<List<JsonElement>> Stream(AuditExport export)
    {
        using var output = new MemoryStream();
        await _storage.StreamAudit(output, export, CancellationToken.None);
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
    }

    private string Apply(AdminCaller proposer, AdminCaller approver, ChangeProposal proposal)
    {
        var proposed = _storage.ProposeChange(proposer, proposal, Now);
        Assert.That(proposed.Status, Is.EqualTo(201), proposed.Detail ?? proposed.Error);
        Assert.That(_storage.ApproveChange(approver, proposed.Id!, null, Now), Is.EqualTo(ChangeOutcome.Done));
        return proposed.Id!;
    }

    private ChangeRecord Change(string id) => _storage.Changes(Administrator("admin-reader"), false, 200, 0)!.Single(c => c.Id == id);

    private AdminCaller Administrator(string subject) => User(subject, "PlatformAdministrator");
    private AdminCaller Owner(string subject, params string[] groups) => User(subject, "ApplicationOwner", groups);
    private AdminCaller Auditor(string subject, params string[] groups) => User(subject, "ReadOnlyAuditor", groups);

    private AdminCaller User(string subject, string role, params string[] groups) => AdminCaller.From(new ClaimsPrincipal(
        new ClaimsIdentity([new Claim("sub", subject), new Claim(ClaimTypes.Role, role), .. groups.Select(g => new Claim("groups", g))],
            "test")), _options);

    private MonitorPolicy Policy(string name = "report-p95", AppScope? scope = null) => new()
    {
        Name = name, Application = (scope ?? _production).Application, Environment = (scope ?? _production).Environment,
        Type = "latency", Kind = "api", Target = "report", Percentile = 95, ThresholdMs = 800
    };

    private static ChangeProposal Upsert(MonitorPolicy policy, string reason = "Tighten the report latency objective") =>
        new("monitor", "upsert", policy, null, null, null, null, reason);

    private static ChangeProposal Maintenance(AppScope scope, DateTimeOffset starts, DateTimeOffset ends,
        string reason = "Synthetic SAP kernel upgrade") => new("maintenance", "create", null,
        new MaintenanceRequest(scope.Application, scope.Environment, starts, ends, reason), null, null, null,
        "Planned synthetic maintenance");

    private static TelemetryEvent Event(bool failed, DateTimeOffset at) => new(Guid.NewGuid(), at, "1.0.0", Guid.NewGuid(), "api",
        "report", failed ? "failure" : "success", 200, failed ? 503 : 200);

    private static QueryFilter Filter(AppScope scope, DateTimeOffset now) =>
        QueryFilter.From(new DefaultHttpContext().Request, [scope], now);

    private static AppScope Scope(string environment, string group) => new()
    {
        Application = "finance", Label = "Finance", Environment = environment, ViewerGroups = [group],
        AllowedNames = ["report"], IngestToken = "local-test-only"
    };

    // Independent statement of the canonical form: default System.Text.Json serialization of the field array.
    private static string Canonical(AuditEntry entry) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new object[] { entry.Seq, entry.At.ToUnixTimeMilliseconds(), entry.Actor, entry.Action, entry.Application,
            entry.Environment, entry.Target, entry.Detail, entry.PreviousHash })));

    private AuditChain Verify()
    {
        using var connection = _storage.Open();
        return Storage.VerifyAuditChain(connection);
    }

    private List<AuditEntry> AuditRows()
    {
        using var connection = _storage.Open();
        using var command = connection.Command(null,
            "SELECT seq, at, actor, action, application, environment, target, detail, previous_hash, hash FROM audit_log ORDER BY seq");
        using var row = command.ExecuteReader();
        var rows = new List<AuditEntry>();
        while (row.Read())
            rows.Add(new(row.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(1)), row.GetString(2), row.GetString(3),
                row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9)));
        return rows;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = _storage.Open();
        using var command = connection.Command(null, sql, parameters);
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var connection = _storage.Open();
        using var command = connection.Command(null, sql);
        return (long)command.ExecuteScalar()!;
    }

    private string Text(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = _storage.Open();
        using var command = connection.Command(null, sql, parameters);
        return (string)command.ExecuteScalar()!;
    }
}
