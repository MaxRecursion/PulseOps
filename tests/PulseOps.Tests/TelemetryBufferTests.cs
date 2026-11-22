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
        var inFlight = buffer.Take(1);
        buffer.Enqueue(second, 3);

        buffer.Retry(inFlight, Now);
        var retried = buffer.Take(2);

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
        var inFlight = buffer.Take(1);

        buffer.Enqueue(Event(), 3);
        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(1, 0, 1, 0, 1)));

        buffer.Retry(inFlight, Now);
        buffer.Processed(buffer.Take(1), accepted: false);
        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(1, 0, 2, 0, 0)));
    }

    [Test]
    public void RetryDropsEventsOlderThanTheBoundedPendingAge()
    {
        var buffer = new TelemetryBuffer(1);
        buffer.Enqueue(Event(Now.AddHours(-24)), 1);
        var inFlight = buffer.Take(1);

        buffer.Retry(inFlight, Now);

        Assert.That(buffer.Snapshot(), Is.EqualTo(new PulseOpsDiagnostics(1, 0, 1, 0, 0)));
    }

    private static TelemetryEvent Event(DateTimeOffset? occurred = null) =>
        new(Guid.NewGuid(), occurred ?? Now, "1.0.0", Guid.NewGuid(), "api", "report", "failure");
}
