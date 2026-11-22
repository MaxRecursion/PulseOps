using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace PulseOps.Portal.Services;

public sealed class LiveUpdates(NavigationManager navigation) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private HubConnection? _hub;
    private Task? _reconnect;
    private long _revision;

    public bool Connected => _hub?.State == HubConnectionState.Connected;
    public long Revision => Interlocked.Read(ref _revision);
    public event Action? ConnectionChanged;

    public void Start()
    {
        if (_hub is not null)
            return;

        _hub = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri("hubs/live"))
            .WithAutomaticReconnect()
            .Build();
        _hub.On("Changed", () => { Interlocked.Increment(ref _revision); });
        _hub.Reconnecting += _ => { ConnectionChanged?.Invoke(); return Task.CompletedTask; };
        _hub.Reconnected += _ =>
        {
            Interlocked.Increment(ref _revision);
            ConnectionChanged?.Invoke();
            return Task.CompletedTask;
        };
        _hub.Closed += _ => { ConnectionChanged?.Invoke(); return Task.CompletedTask; };
        _reconnect = MaintainConnectionAsync(_stop.Token);
    }

    private async Task MaintainConnectionAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            do
            {
                if (_hub?.State == HubConnectionState.Disconnected)
                {
                    try
                    {
                        await _hub.StartAsync(cancellationToken);
                        Interlocked.Increment(ref _revision);
                        ConnectionChanged?.Invoke();
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Queries keep using their polling fallback until the connection succeeds.
                    }
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_reconnect is not null)
            await _reconnect;
        if (_hub is not null)
            await _hub.DisposeAsync();
        _stop.Dispose();
    }
}
