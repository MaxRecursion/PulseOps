using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed partial class AdministrationTests
{
    [TestCase("approve")]
    [TestCase("reject")]
    [TestCase("withdraw")]
    public void AddressedChangeExpiresAtItsDeadlineEvenBehindAFullCleanupBatch(string decision)
    {
        _options.Administration.MaximumPendingChanges = 150;
        var maker = Administrator("maker");
        var checker = Administrator("checker");
        for (var i = 0; i < 100; i++)
            Assert.That(_storage.ProposeChange(maker, Upsert(Policy($"older-{i}")), Now).Status, Is.EqualTo(201));
        var created = Now.AddMinutes(1);
        var change = _storage.ProposeChange(maker, Upsert(Policy("later")), created).Id!;
        var deadline = created.AddHours(_options.Administration.ChangeExpiryHours);

        var outcome = decision switch
        {
            "approve" => _storage.ApproveChange(checker, change, null, deadline),
            "reject" => _storage.RejectChange(checker, change, null, deadline),
            _ => _storage.WithdrawChange(maker, change, deadline)
        };
        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ChangeOutcome.Conflict("not_pending")));
            Assert.That(Change(change).Status, Is.EqualTo("Expired"));
            Assert.That(AuditRows().Last(e => e.Target == change).Action, Is.EqualTo("config.expired"));
            Assert.That(Scalar("SELECT COUNT(*) FROM monitor_policies"), Is.Zero);
        });
    }

    [Test]
    public void ManagedMonitorCapAllowsEditsDeletionAndRetryAfterCapacityReturns()
    {
        var maker = Administrator("maker");
        var checker = Administrator("checker");
        var waiting = _storage.ProposeChange(maker, Upsert(Policy("waiting")), Now).Id!;
        for (var i = 0; i < 300; i++)
            Apply(maker, checker, Upsert(Policy($"monitor-{i}")));

        Assert.That(_storage.ApproveChange(checker, waiting, null, Now),
            Is.EqualTo(ChangeOutcome.Conflict("managed_monitor_limit")));
        Assert.That(_storage.Changes(checker, true, 10, 0)!.Single().Id, Is.EqualTo(waiting));
        var updated = Policy("monitor-0");
        updated.ThresholdMs = 1500;
        Apply(maker, checker, Upsert(updated));
        Apply(maker, checker, new ChangeProposal("monitor", "delete", null, null, updated.Application,
            updated.Environment, updated.Name, "Retire this monitor"));
        Assert.That(_storage.ApproveChange(checker, waiting, null, Now), Is.EqualTo(ChangeOutcome.Done));
        Assert.That(Scalar("SELECT COUNT(*) FROM monitor_policies"), Is.EqualTo(300));
        Assert.That(Verify().Verified, Is.True);
    }

    [Test]
    public void ExplicitNullMonitorTargetReturnsAValidationError()
    {
        var invalid = Policy();
        invalid.Target = null!;
        Assert.That(_storage.ProposeChange(Administrator("maker"), Upsert(invalid), Now).Status, Is.EqualTo(400));
        Assert.That(AuditRows(), Is.Empty);
    }
}

public sealed partial class NotificationTests
{
    [Test]
    public async Task DispatchRechecksTheCurrentRouteScopeAndSeverity()
    {
        _options.Webhooks = [new() { Name = "pager", Url = "https://hooks.example.test/pulseops" }];
        OpenIncident();
        Assert.That(Destinations(), Is.EquivalentTo(new[] { "finance-oncall", "pager" }));
        _options.EmailRoutes[0].Environment = "staging";
        _options.Webhooks[0].Severities = ["critical"];
        var sender = new RecordingSender();
        var worker = Worker(sender);

        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(sender.Sent, Is.Empty);
            Assert.That(_webhooks.Requests, Is.Empty);
            Assert.That(Delivery("finance-oncall").State, Is.EqualTo("failed"));
            Assert.That(Delivery("pager").State, Is.EqualTo("failed"));
        });
    }
}
