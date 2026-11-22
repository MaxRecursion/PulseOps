using System.Diagnostics;

namespace PulseOps.Sdk.Blazor;

public sealed class PulseOpsOperation : IDisposable
{
    private readonly PulseOpsClient? _client;
    private readonly string? _name;
    private readonly Activity? _activity;
    private readonly long _started;
    private string _outcome = "unknown";
    private string? _category;
    private bool _finished;

    internal PulseOpsOperation()
    {
    }

    internal PulseOpsOperation(PulseOpsClient client, string name, Activity activity)
    {
        _client = client;
        _name = name;
        _activity = activity;
        _started = Stopwatch.GetTimestamp();
    }

    public void SetSuccess()
    {
        _outcome = "success";
        _category = null;
    }

    public void SetFailure(Exception exception)
    {
        _outcome = "failure";
        _category = PulseOpsClient.ExceptionCategory(exception);
    }

    public void SetCancelled()
    {
        _outcome = "cancelled";
        _category = "cancelled";
    }

    public void Dispose()
    {
        if (_client is null || _finished)
            return;

        _finished = true;
        _activity?.Stop();
        _client.Track("operation", _name!, _outcome, _outcome == "failure" ? 2 : 5,
            Stopwatch.GetElapsedTime(_started).TotalMilliseconds, category: _category, activity: _activity);
        _activity?.Dispose();
    }
}
