using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.JSInterop;

namespace PulseOps.Sdk.Blazor;

public sealed class PulseOpsClient : IAsyncDisposable
{
    private enum BatchDisposition
    {
        Accepted,
        Retry,
        Rejected
    }

    internal static readonly ActivitySource Activities = new("PulseOps.Sdk.Blazor");
    private readonly PulseOpsOptions _options;
    private readonly IJSRuntime _js;
    private readonly NavigationManager _navigation;
    private readonly TelemetryBuffer _buffer;
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly HttpClient _sender = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly CancellationTokenSource _stop = new();
    private readonly Uri? _endpoint;
    private bool _sampled;
    private Guid _sessionId = Guid.NewGuid();
    private bool _disabled;
    private bool _subscribed;
    private int _started;
    private Task? _pump;
    private DotNetObjectReference<PulseOpsClient>? _jsReference;

    public PulseOpsClient(PulseOpsOptions options, IJSRuntime js, NavigationManager navigation)
    {
        _options = options;
        _js = js;
        _navigation = navigation;
        _buffer = new(Math.Clamp(options.QueueCapacity, 1, 2_000));
        _endpoint = ResolveEndpoint(navigation.BaseUri, options.IngestionEndpoint);
        _disabled = !options.Enabled || !options.ConfigurationValid || _endpoint is null;
        SampleSession();
    }

    public bool IsEnabled => !Volatile.Read(ref _disabled);

    public async ValueTask StartAsync()
    {
        if (!IsEnabled || Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _pump = PumpAsync(_stop.Token);
        try
        {
            _jsReference = DotNetObjectReference.Create(this);
            object?[] arguments =
            [
                _jsReference, _options.EnableErrorTracking, _options.EnablePerformanceMonitoring,
                _options.EnableSessionDiagnostics, _options.EnableStartupMonitoring
            ];
            string? browserSession;
            if (_js is IJSInProcessRuntime localRuntime)
            {
                browserSession = localRuntime.Invoke<string?>("PulseOpsBootstrap.attach", arguments);
            }
            else
            {
                using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeLimit.CancelAfter(TimeSpan.FromSeconds(1));
                browserSession = await _js.InvokeAsync<string?>("PulseOpsBootstrap.attach", timeLimit.Token, arguments);
            }
            if (Guid.TryParse(browserSession, out var id) && id != Guid.Empty)
            {
                _sessionId = id;
                SampleSession();
            }
        }
        catch (Exception ex) when (ex is JSException or OperationCanceledException or InvalidOperationException)
        {
            // The SDK still operates with its own session when bootstrap or interop is unavailable.
        }

        if (!IsEnabled)
            return;

        if (_options.EnableNavigationTracking)
        {
            _navigation.LocationChanged += OnLocationChanged;
            TrackNavigation(_navigation.Uri);
        }

        if (_options.EnableErrorTracking)
            TaskScheduler.UnobservedTaskException += OnUnobservedException;

        _subscribed = true;
        if (_options.EnableSessionDiagnostics)
            Track("session", "opened", "success");
    }

    public async ValueTask MarkApplicationReadyAsync()
    {
        try
        {
            // Application readiness also stops the early watchdog when managed transport is unavailable.
            await _js.InvokeVoidAsync("PulseOpsBootstrap.ready");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
        {
        }
    }

    public PulseOpsOperation StartOperation(string name) =>
        IsEnabled && _options.AllowedOperations.Contains(name)
            ? new(this, name, StartActivity(name))
            : new();

    public void ReportException(Exception exception)
    {
        if (_options.EnableErrorTracking)
            Track("error", "managed-exception", "failure", 1, category: ExceptionCategory(exception));
    }

    public PulseOpsDiagnostics GetDiagnostics() => _buffer.Snapshot();

    public void Disable()
    {
        if (Interlocked.Exchange(ref _disabled, true))
            return;

        _stop.Cancel();
        _buffer.Clear();
    }

    internal bool IsIngestionRequest(Uri uri) =>
        _endpoint is not null && Uri.Compare(uri, _endpoint, UriComponents.HttpRequestUrl & ~UriComponents.Query,
            UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;

    internal static Activity StartActivity(string name)
    {
        var parent = Activity.Current;
        if (parent?.IdFormat == ActivityIdFormat.W3C)
        {
            return Activities.StartActivity(name, ActivityKind.Internal, parent.Context)
                ?? new Activity(name).SetIdFormat(ActivityIdFormat.W3C).Start();
        }

        return Activities.StartActivity(name, ActivityKind.Internal)
            ?? new Activity(name).SetIdFormat(ActivityIdFormat.W3C).Start();
    }

    internal void Track(string kind, string name, string outcome, int priority = 5, double? duration = null,
        int? statusCode = null, string? category = null, double? value = null, Activity? activity = null)
    {
        if (!IsEnabled || (!_sampled && priority >= 4))
            return;

        activity ??= Activity.Current;
        var trace = activity is { IdFormat: ActivityIdFormat.W3C } ? activity : null;
        var queued = _buffer.Enqueue(new TelemetryEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, _options.Version, _sessionId, kind, name, outcome,
            duration.HasValue ? Math.Clamp(duration.Value, 0, 86_400_000) : null, statusCode,
            trace?.TraceId.ToHexString(), trace?.SpanId.ToHexString(),
            trace is { ParentSpanId: var parent } && parent != default ? parent.ToHexString() : null,
            category, value), priority);

        if (priority <= 3 || queued >= _options.BatchSize)
            RequestFlush();
    }

    internal void ReportBoundaryError(Exception exception)
    {
        if (_options.EnableErrorTracking)
            Track("error", "render-exception", "failure", 1, category: ExceptionCategory(exception));
    }

    internal static string ExceptionCategory(Exception exception) => exception switch
    {
        TimeoutException => "timeout",
        OperationCanceledException => "cancelled",
        UnauthorizedAccessException => "unauthorized",
        HttpRequestException => "http",
        JSException => "javascript",
        FormatException => "format",
        System.Text.Json.JsonException => "json",
        ArgumentException => "argument",
        NullReferenceException => "null-reference",
        InvalidOperationException => "invalid-operation",
        _ => "application"
    };

    [JSInvokable]
    public void RecordBrowserEvent(string kind, string name, double? value = null)
    {
        if (kind == "metric" && _options.EnablePerformanceMonitoring &&
            value is >= 0 and <= 86_400_000 &&
            name is "lcp" or "inp" or "cls" or "fcp" or "ttfb" or "navigation-duration" or "long-task-count")
        {
            Track(kind, name, "success", category: "measured", value: value);
        }
        else if (kind == "session" && _options.EnableSessionDiagnostics &&
            name is "online" or "offline" or "visible" or "hidden" or "ended")
        {
            Track(kind, name, name == "offline" ? "degraded" : "success",
                name == "offline" ? 3 : 5);
            if (name is "hidden" or "ended")
                RequestFlush();
        }
        else if (kind == "error" && _options.EnableErrorTracking && name is "javascript-error" or "promise-rejection")
        {
            Track(kind, name, "failure", 1, category: "javascript");
        }
    }

    private void SampleSession()
    {
        // Stable within the tab session, so sampled traces remain correlated across reloads.
        var cohort = BitConverter.ToUInt32(_sessionId.ToByteArray(), 0) / (uint.MaxValue + 1d);
        _sampled = cohort < _options.SamplingRate;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args) => TrackNavigation(args.Location);

    private void TrackNavigation(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
            _options.NavigationRoutes.TryGetValue(uri.AbsolutePath, out var alias))
        {
            Track("navigation", alias, "success");
        }
    }

    private void OnUnobservedException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        Track("error", "unobserved-task", "failure", 1, category: ExceptionCategory(args.Exception.GetBaseException()));

    private void RequestFlush()
    {
        try
        {
            _flushSignal.Release();
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
            // A flush is already pending or shutdown is in progress.
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _flushSignal.WaitAsync(_options.FlushInterval, cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var events = _buffer.Take(_options.BatchSize);
                    if (events.Count == 0)
                        break;

                    BatchDisposition disposition;
                    try
                    {
                        disposition = await SendBatchAsync(events, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _buffer.Processed(events, accepted: false);
                        throw;
                    }

                    if (disposition == BatchDisposition.Accepted)
                        _buffer.Processed(events, accepted: true);
                    else if (disposition == BatchDisposition.Rejected)
                        _buffer.Processed(events, accepted: false);
                    else
                    {
                        _buffer.Retry(events, DateTimeOffset.UtcNow);
                        await Task.Delay(_options.FlushInterval, cancellationToken);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<BatchDisposition> SendBatchAsync(List<TelemetryEvent> events, CancellationToken cancellationToken)
    {
        var batch = new TelemetryBatch(1, _options.Application, _options.Environment, events);
        var payload = JsonSerializer.SerializeToUtf8Bytes(batch, TelemetryJsonContext.Default.TelemetryBatch);
        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                    .SetBrowserRequestCredentials(BrowserRequestCredentials.SameOrigin)
                    .SetBrowserRequestMode(BrowserRequestMode.SameOrigin)
                    .SetBrowserRequestOption("redirect", "error");
                if (_options.GetRelayAntiforgeryTokenAsync is { } getToken)
                {
                    using var tokenTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    tokenTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                    string? token;
                    try
                    {
                        token = await getToken(tokenTimeout.Token).AsTask().WaitAsync(tokenTimeout.Token);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        return BatchDisposition.Retry;
                    }

                    if (token is not { Length: > 0 and <= 4096 } ||
                        token.Any(character => character is < '!' or > '~'))
                        return BatchDisposition.Rejected;
                    request.Headers.Add("X-CSRF-TOKEN", token);
                }

                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await _sender.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Accepted)
                    return BatchDisposition.Accepted;
                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    // Operator kill switch: intake for this application is suspended. Stop for this session.
                    _ = Task.Run(Disable, CancellationToken.None);
                    return BatchDisposition.Rejected;
                }

                if (response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                    && (int)response.StatusCode < 500)
                {
                    return BatchDisposition.Rejected;
                }

                var header = response.Headers.RetryAfter;
                retryAfter = header?.Delta ?? header?.Date - DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is (HttpRequestException or OperationCanceledException))
            {
            }

            if (attempt + 1 == maxAttempts)
                break;

            var delay = retryAfter is { TotalMilliseconds: > 0 } serverDelay
                ? Math.Min(serverDelay.TotalMilliseconds, 30_000)
                : (500 << attempt) + Random.Shared.Next(100, 500);
            _buffer.Retried();
            await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
        }

        return BatchDisposition.Retry;
    }

    private static Uri? ResolveEndpoint(string baseAddress, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(baseAddress, UriKind.Absolute, out var origin) ||
            !Uri.TryCreate(origin, endpoint, out var address) ||
            address.Scheme is not ("https" or "http") ||
            (address.Scheme == "http" && !address.IsLoopback) ||
            !string.Equals(origin.GetLeftPart(UriPartial.Authority), address.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase) ||
            address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
        {
            return null;
        }

        return address;
    }

    public async ValueTask DisposeAsync()
    {
        Disable();
        if (_subscribed)
        {
            _navigation.LocationChanged -= OnLocationChanged;
            TaskScheduler.UnobservedTaskException -= OnUnobservedException;
        }

        if (_pump is not null)
            await _pump;

        _jsReference?.Dispose();
        _sender.Dispose();
        _flushSignal.Dispose();
        _stop.Dispose();
    }
}
