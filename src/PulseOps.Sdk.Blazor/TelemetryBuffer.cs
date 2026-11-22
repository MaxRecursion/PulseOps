namespace PulseOps.Sdk.Blazor;

internal sealed class TelemetryBuffer(int capacity)
{
    private static readonly TimeSpan MaximumPendingAge = TimeSpan.FromHours(23);
    private readonly object _gate = new();
    private readonly LinkedList<TelemetryEvent>[] _priorities = [new(), new(), new(), new(), new(), new()];
    private readonly Dictionary<Guid, int> _inFlight = [];
    private int _count;
    private long _queued;
    private long _accepted;
    private long _dropped;
    private long _retries;

    public int Enqueue(TelemetryEvent item, int priority)
    {
        lock (_gate)
        {
            if (_count == capacity)
            {
                var victim = Array.FindLastIndex(_priorities, queue => queue.Count != 0);
                if (victim < priority)
                {
                    _dropped++;
                    return _count;
                }

                _priorities[victim].RemoveFirst();
                _count--;
                _dropped++;
            }

            _priorities[priority].AddLast(item);
            _count++;
            _queued++;
            return _count;
        }
    }

    public List<TelemetryEvent> Take(int limit)
    {
        lock (_gate)
        {
            var result = new List<TelemetryEvent>(Math.Min(limit, _count));
            foreach (var queue in _priorities)
            {
                while (result.Count < limit && queue.First is { } first)
                {
                    queue.RemoveFirst();
                    _inFlight.Add(first.Value.Id, Array.IndexOf(_priorities, queue));
                    result.Add(first.Value);
                }
            }
            return result;
        }
    }

    public void Retry(List<TelemetryEvent> events, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var item in events.AsEnumerable().Reverse())
            {
                if (!_inFlight.Remove(item.Id, out int priority))
                    continue;
                if (item.OccurredAt < now - MaximumPendingAge)
                {
                    _count--;
                    _dropped++;
                    continue;
                }
                _priorities[priority].AddFirst(item);
            }
        }
    }

    public void Processed(List<TelemetryEvent> events, bool accepted)
    {
        lock (_gate)
        {
            foreach (var item in events)
            {
                if (!_inFlight.Remove(item.Id))
                    continue;
                _count--;
                if (accepted)
                    _accepted++;
                else
                    _dropped++;
            }
        }
    }

    public void Retried()
    {
        lock (_gate)
            _retries++;
    }

    public PulseOpsDiagnostics Snapshot()
    {
        lock (_gate)
            return new(_queued, _accepted, _dropped, _retries, _count);
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var queue in _priorities)
            {
                _dropped += queue.Count;
                _count -= queue.Count;
                queue.Clear();
            }
        }
    }
}
