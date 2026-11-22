namespace PulseOps.Sdk.Blazor;

internal sealed class TelemetryBuffer(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<TelemetryEvent>[] _priorities = [new(), new(), new(), new(), new(), new()];
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

                _priorities[victim].Dequeue();
                _count--;
                _dropped++;
            }

            _priorities[priority].Enqueue(item);
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
                while (result.Count < limit && queue.TryDequeue(out var item))
                {
                    _count--;
                    result.Add(item);
                }
            }
            return result;
        }
    }

    public void Processed(int size, bool accepted)
    {
        lock (_gate)
        {
            if (accepted)
                _accepted += size;
            else
                _dropped += size;
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
            _dropped += _count;
            _count = 0;
            foreach (var queue in _priorities)
                queue.Clear();
        }
    }
}
