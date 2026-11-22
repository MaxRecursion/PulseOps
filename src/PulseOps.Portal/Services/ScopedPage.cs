using Microsoft.AspNetCore.Components;

namespace PulseOps.Portal.Services;

public abstract class ScopedPage : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private DateTimeOffset _lastRefresh;
    private long _lastRevision;
    private string? _loadedAddress;

    [Inject] protected PortalApi Api { get; set; } = null!;
    [Inject] protected LiveUpdates Live { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;
    [CascadingParameter] protected PortalContext Context { get; set; } = null!;
    [SupplyParameterFromQuery(Name = "application")] public string? Application { get; set; }
    [SupplyParameterFromQuery(Name = "environment")] public string? Environment { get; set; }
    [SupplyParameterFromQuery(Name = "hours")] public int? Hours { get; set; }
    [SupplyParameterFromQuery(Name = "version")] public string? Version { get; set; }
    [SupplyParameterFromQuery(Name = "offset")] public int? Offset { get; set; }

    protected ViewScope Scope => new(Application, Environment, Math.Clamp(Hours ?? 24, 1, 336), Version);
    protected int Skip => Math.Max(0, Offset ?? 0);
    protected bool Loading { get; private set; }
    protected string? Error { get; private set; }
    protected bool SignInRequired { get; private set; }
    protected DateTimeOffset? UpdatedAt { get; private set; }
    protected virtual bool LiveRefresh => false;
    protected CancellationToken Lifetime => _lifetime.Token;

    protected override void OnInitialized()
    {
        if (LiveRefresh)
            _ = MonitorRefreshAsync(Lifetime);
    }

    // Cascading values (the portal context) re-render pages without changing what they show. Reloading only when the
    // address changes avoids duplicate queries against the server's bounded query limiter.
    protected override Task OnParametersSetAsync()
    {
        var address = Navigation.Uri;
        if (address == _loadedAddress && (Loading || UpdatedAt is not null))
            return Task.CompletedTask;
        _loadedAddress = address;
        return RefreshAsync(clear: true);
    }
    protected Task RefreshAsync() => RefreshAsync(clear: false);

    private async Task RefreshAsync(bool clear)
    {
        _request?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _request = request;
        _lastRefresh = DateTimeOffset.UtcNow;
        _lastRevision = Live.Revision;
        Loading = true;
        SignInRequired = false;
        Error = null;
        if (clear)
            UpdatedAt = null;
        try
        {
            await LoadAsync(request.Token);
            if (!request.IsCancellationRequested)
                UpdatedAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) when (request.IsCancellationRequested) { }
        catch (PortalRequestException exception) when (!request.IsCancellationRequested)
        {
            Error = exception.Message;
            SignInRequired = exception.Status == System.Net.HttpStatusCode.Unauthorized;
        }
        catch (Exception) when (!request.IsCancellationRequested)
        {
            Error = "Could not load the latest data. Check your connection and retry.";
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                Loading = false;
            }
            request.Dispose();
        }
    }

    private async Task MonitorRefreshAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (Loading || SignInRequired)
                    continue;

                var elapsed = DateTimeOffset.UtcNow - _lastRefresh;
                if (elapsed >= TimeSpan.FromSeconds(Live.Connected ? 60 : 30) || (Live.Connected && Live.Revision != _lastRevision))
                {
                    await InvokeAsync(async () =>
                    {
                        await RefreshAsync();
                        StateHasChanged();
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    protected abstract Task LoadAsync(CancellationToken cancellationToken);
    protected string AppLabel(string app) => Context.Scopes.FirstOrDefault(s => s.Application == app)?.Label ?? app;

    public virtual void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
