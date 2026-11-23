using NUnit.Framework;
using PulseOps.Sdk.Blazor;

namespace PulseOps.Tests;

public sealed class TelemetryBufferTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 30, 0, TimeSpan.Zero);

    [Test]
    public void RetryKeepsInFlightCapacityAndRestoresOriginalOrder()
    {
        var buffer = new TelemetryBuffer(2);
        var first = Event();
        var second = Event();
        buffer.Enqueue(first, 3);
        var inFlight = buffer.Take(1, Now);
        buffer.Enqueue(second, 3);

        buffer.Retry(inFlight, Now);
        var retried = buffer.Take(2, Now);

        Assert.Multiple(() =>
        {
            Assert.That(buffer.Snapshot().Pending, Is.EqualTo(2));
            Assert.That(retried.Select(item => item.Id), Is.EqualTo(new[] { first.Id, second.Id }));
        });
        buffer.Processed(retried, accepted: true);
        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(2, 2, 0, 0, 0)));
    }

    [Test]
    public void FullQueueCountsInFlightItemsAgainstCapacity()
    {
        var buffer = new TelemetryBuffer(1);
        var first = Event();
        buffer.Enqueue(first, 3);
        var inFlight = buffer.Take(1, Now);

        buffer.Enqueue(Event(), 3);
        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(1, 0, 1, 0, 1)));

        buffer.Retry(inFlight, Now);
        buffer.Processed(buffer.Take(1, Now), accepted: false);
        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(1, 0, 2, 0, 0)));
    }

    [Test]
    public void RetryDropsEventsOlderThanTheBoundedPendingAge()
    {
        var buffer = new TelemetryBuffer(1);
        buffer.Enqueue(Event(Now.AddHours(-24)), 1);
        var inFlight = buffer.Take(1, Now.AddHours(-2));

        buffer.Retry(inFlight, Now);

        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(1, 0, 1, 0, 0)));
    }

    [Test]
    public void TakeExcludesExpiredEventsFromABatchOfFreshEvents()
    {
        var buffer = new TelemetryBuffer(3);
        var failure = Event(Now.AddMinutes(-1));
        var routine = Event(Now);
        buffer.Enqueue(Event(Now.AddHours(-24)), 1);
        buffer.Enqueue(failure, 1);
        buffer.Enqueue(routine, 5);

        var batch = buffer.Take(2, Now);

        Assert.That(batch, Is.EqualTo(new[] { failure, routine }));
        buffer.Processed(batch, accepted: true);
        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(3, 2, 1, 0, 0)));
    }

    [Test]
    public void ClosingDiscardsPendingEventsAndPreventsALateRetryOrEnqueue()
    {
        var buffer = new TelemetryBuffer(2);
        buffer.Enqueue(Event(), 1);
        var inFlight = buffer.Take(1, Now);
        buffer.Enqueue(Event(), 5);

        buffer.Close();
        buffer.Retry(inFlight, Now);
        buffer.Processed(inFlight, accepted: false);
        buffer.Enqueue(Event(), 1);

        Assert.That(buffer.Take(2, Now), Is.Empty);
        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(2, 0, 2, 0, 0)));
    }

    private static TelemetryEvent Event(DateTimeOffset? occurred = null) =>
        new(Guid.NewGuid(), occurred ?? Now, "1.0.0", Guid.NewGuid(), "api", "report", "failure");
}
