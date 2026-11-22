using System.Diagnostics;

namespace PulseOps.Sdk.Blazor;

public sealed class PulseOpsHttpHandler(PulseOpsClient client, PulseOpsOptions options) : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<string> EndpointAlias = new("PulseOps.EndpointAlias");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (!client.IsEnabled || !options.EnableApiMonitoring || uri is not { IsAbsoluteUri: true } ||
            client.IsIngestionRequest(uri) || ResolveAlias(request, uri) is not { } alias)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        using var activity = PulseOpsClient.StartActivity(alias);
        if (options.TracePropagationOrigins.Contains(uri.GetLeftPart(UriPartial.Authority)) &&
            !request.Headers.Contains("traceparent"))
        {
            request.Headers.TryAddWithoutValidation("traceparent", activity.Id);
        }

        var stopwatch = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            var outcome = status >= 400 ? "failure" : "success";
            client.Track("api", alias, outcome, status >= 400 ? 3 : 5,
                Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds, status, CategorizeStatus(status), activity: activity);
            return response;
        }
        catch (OperationCanceledException exception)
        {
            var timedOut = exception.InnerException is TimeoutException;
            client.Track("api", alias, timedOut ? "failure" : "cancelled", timedOut ? 3 : 4,
                Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds,
                category: timedOut ? "timeout" : "cancelled-or-timeout", activity: activity);
            throw;
        }
        catch (HttpRequestException exception)
        {
            client.Track("api", alias, "failure", 3, Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds,
                category: CategorizeTransport(exception.HttpRequestError), activity: activity);
            throw;
        }
    }

    private string? ResolveAlias(HttpRequestMessage request, Uri uri)
    {
        if (request.Options.TryGetValue(EndpointAlias, out var configured) && options.ApiEndpoints.ContainsValue(configured))
            return configured;

        return options.ApiEndpoints.TryGetValue(uri.GetLeftPart(UriPartial.Path), out var alias)
            ? alias
            : options.ApiEndpoints.GetValueOrDefault(uri.AbsolutePath);
    }

    private static string? CategorizeStatus(int status) => status switch
    {
        401 or 403 => "authentication",
        408 or 504 => "timeout",
        429 => "rate-limited",
        >= 500 => "server-error",
        >= 400 => "client-error",
        _ => null
    };

    private static string CategorizeTransport(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => "dns",
        HttpRequestError.ConnectionError => "connection",
        HttpRequestError.SecureConnectionError => "tls",
        _ => "network-or-browser-policy"
    };
}
