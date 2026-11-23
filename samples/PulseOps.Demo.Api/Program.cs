using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

const long maxRelayBytes = 192 * 1024;
const string defaultServer = "http://localhost:5080";
const string defaultOrigin = "http://localhost:5091";
const string demoScope = "application=finance-demo&environment=demo&hours=1";
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("The simulated reporting app requires Development and a loopback binding.");

var upstream = RequireLoopbackOrigin(builder.Configuration["PulseOps:Server"] ?? defaultServer);
var browserOrigin = RequireLoopbackOrigin(builder.Configuration["PulseOps:BrowserOrigin"] ?? defaultOrigin);
var relayToken = builder.Configuration["PulseOps:DemoMachineToken"] ?? "pulseops-demo-local-only-91fd-4ad1";
builder.WebHost.UseUrls(browserOrigin.GetLeftPart(UriPartial.Authority));
builder.Services.AddHttpClient("PulseOpsRelay", http =>
{
    http.BaseAddress = upstream;
    http.Timeout = TimeSpan.FromSeconds(8);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    MaxConnectionsPerServer = 16
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    var host = context.Request.Host;
    if (remote is null || (!IPAddress.IsLoopback(remote) &&
        !(remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4()))) ||
        !string.Equals(host.Host, browserOrigin.Host, StringComparison.OrdinalIgnoreCase) ||
        host.Port.GetValueOrDefault(context.Request.IsHttps ? 443 : 80) != browserOrigin.Port)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next(context);
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
// The startup observer runs before WASM. Supply only the public loopback endpoint used by this demo.
app.MapGet("/pulseops/demo-config.js", () =>
{
    var endpoint = System.Text.Json.JsonSerializer.Serialize(new Uri(upstream, "/api/telemetry/v1/startup").AbsoluteUri);
    return Results.Text($"document.getElementById('pulseops-bootstrap').dataset.startupEndpoint = {endpoint};", "application/javascript");
});
var portal = app.MapGroup("/pulseops/portal");
portal.MapGet("", () => OpenPortal("/"));
portal.MapGet("/{view}", (string view) => view switch
{
    "events" => OpenPortal("/logs"),
    "incidents" => OpenPortal("/incidents"),
    "history" => OpenPortal("/history", "&days=7&kind=operation&name=generate-report"),
    _ => Results.NotFound()
});
portal.MapGet("/trace/{traceId}", (string traceId) =>
    traceId.Length == 32 && traceId.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') && traceId.Any(c => c != '0')
        ? OpenPortal("/traces", "&traceId=" + traceId)
        : Results.NotFound());
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", simulation = true }));
app.MapGet("/api/report", async Task<IResult> (string? scenario, CancellationToken cancellationToken) =>
{
    switch (scenario)
    {
        case "slow":
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            break;
        case "timeout":
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        case "http500":
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        case "http503":
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        case "auth":
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        case "invalid":
            return Results.Text("Invalid simulated report", "text/csv");
        case "normal" or "large" or "slow-parse" or "business":
            break;
        default:
            return Results.BadRequest();
    }

    return Results.Stream(async stream =>
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 16_384, leaveOpen: true);
        await writer.WriteLineAsync("ledger,cost-centre,variance".AsMemory(), cancellationToken);
        var costCenters = new[] { "Operations", "Technology", "Risk", "Treasury", "Corporate" };
        var rowCount = scenario == "large" ? 120_000 : 30;
        for (var row = 0; row < rowCount; row++)
        {
            var ledger = 3_000 + row % 20;
            var variance = row % 2 == 0 ? row * 113 % 18_000 : -(row * 167 % 7_000);
            var record = string.Create(CultureInfo.InvariantCulture, $"GL-{ledger},{costCenters[row % costCenters.Length]},{variance}");
            await writer.WriteLineAsync(record.AsMemory(), cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }, "text/csv; charset=utf-8");
});

app.MapPost("/pulseops/telemetry", async Task<IResult> (
    HttpContext context, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    if (!string.Equals(context.Request.Headers.Origin, browserOrigin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal) ||
        !context.Request.HasJsonContentType())
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (context.Request.ContentLength > maxRelayBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        limit.MaxRequestBodySize = maxRelayBytes;

    using var client = clients.CreateClient("PulseOpsRelay");
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry/v1/batch");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relayToken);
    request.Content = new StreamContent(context.Request.Body);
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    try
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Headers.RetryAfter is { } retry)
            context.Response.Headers.RetryAfter = retry.ToString();

        return Results.StatusCode((int)response.StatusCode);
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
        ex is (HttpRequestException or OperationCanceledException))
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapFallbackToFile("index.html");
await app.RunAsync();

IResult OpenPortal(string path, string filter = "") =>
    Results.Redirect(new Uri(upstream, $"{path}?{demoScope}{filter}").AbsoluteUri);

static Uri RequireLoopbackOrigin(string configured)
{
    if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
        uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.PathAndQuery != "/" ||
        uri.Fragment.Length != 0)
    {
        throw new InvalidOperationException("The demo accepts loopback-only HTTP or HTTPS origins.");
    }

    return uri;
}
