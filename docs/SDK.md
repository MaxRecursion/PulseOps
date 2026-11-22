# Blazor WebAssembly SDK

PulseOps instruments standalone Blazor WebAssembly applications targeting .NET 10. The NuGet package contains the managed instrumentation and the early browser bootstrap. The production collector uses HTTPS and a scoped application/environment registration.

## Set up the application and relay

1. Register the monitored application, environment, and stable telemetry aliases with the PulseOps operator. Provision a machine credential scoped to that application/environment for an approved relay.
2. Add a same-origin JSON POST route such as `/pulseops/telemetry` to the application’s host or gateway. Authenticate the application’s existing SSO session, verify its application/environment access, require the exact browser origin, apply its antiforgery policy, cap the request at the collector limit, and forward only to the configured collector. Keep the machine credential in the relay's secret store. Preserve the collector status, including 202 and its retry guidance. Return 401/403 without redirects on expired authentication.
3. Configure the relay's antiforgery validator to use `X-CSRF-TOKEN` and provide the session token through `GetRelayAntiforgeryTokenAsync` when the host requires a token. This delegate runs only during export with a one-second budget. Keep its result in memory and avoid login flows from the delegate. Never distribute the collector's machine credential with the WASM files.
4. Publish the approved SDK package to your internal package source, then add a package reference to the monitored app. For a local build, run `dotnet pack src/PulseOps.Sdk.Blazor -c Release -o artifacts/nuget` from the PulseOps repository root.

```bash
dotnet add package PulseOps.Sdk.Blazor --version 0.1.0
```

Add the SDK and append the handler to existing named or typed `IHttpClientFactory` registrations. Retain their other handlers, including authentication and any retry behavior.

```csharp
using PulseOps.Sdk.Blazor;

builder.Services.AddPulseOps(options =>
{
    options.Application = "finance-dashboard";
    options.Environment = "production";
    options.Version = "1.4.0";
    options.IngestionEndpoint = "/pulseops/telemetry";
    options.SamplingRate = 0.10;
    options.NavigationRoutes["/reports"] = "reports";
    options.ApiEndpoints["https://sap-gateway.example.internal/api/report"] = "sap-report";
    options.AllowedOperations.Add("generate-report");
});

builder.Services.AddHttpClient("Reports", client =>
{
    client.BaseAddress = new Uri("https://sap-gateway.example.internal/");
}).AddHttpMessageHandler<PulseOpsHttpHandler>();

var host = builder.Build();
await host.Services.GetRequiredService<PulseOpsClient>().StartAsync();
await host.RunAsync();
```

For a parameterised route, register a stable alias and set `PulseOpsHttpHandler.EndpointAlias` on the constructed request. For an existing manually constructed `HttpClient`, insert the DI-provided `PulseOpsHttpHandler` into its handler chain. Keep the outer `HttpClient` and handler lifetimes consistent with the host's existing registrations. Imports of browser-only APIs should stay inside the WASM project.

## Early startup and browser metrics

Optional anonymous intake requires explicit activation and an exact origin on the collector's application/environment scope. It accepts a restricted set of startup milestones and failures and applies rate limiting. Browser requests omit credentials. In `wwwroot/index.html`, put the bootstrap script ahead of the Blazor loader and start it from an external script to avoid a CSP exception for inline JavaScript. For browser metrics and JavaScript errors with anonymous startup disabled, omit `data-startup-endpoint`.

```html
<script src="_content/PulseOps.Sdk.Blazor/bootstrap.js" defer
        data-application="finance-dashboard"
        data-environment="production"
        data-version="1.4.0"
        data-startup-endpoint="https://pulseops.example.internal/api/telemetry/v1/startup"
        data-app-root-id="app"
        data-startup-timeout-ms="30000"></script>
<script src="_framework/blazor.webassembly.js" autostart="false" defer></script>
<script src="start-blazor.js" defer></script>
```

```js
const startup = window.PulseOpsBootstrap
    ? window.PulseOpsBootstrap.start()
    : window.Blazor.start();
startup.catch(handleExistingStartupError);
```

You can pass the same options object previously given to `Blazor.start(options)`. Existing boot-resource overrides and integrity settings are preserved. Add the collector to `connect-src` only when using the direct startup route. Use an application-owned error handler for startup failures. [ASP.NET Core Blazor startup](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/startup?view=aspnetcore-10.0).

Mark the application ready in the first successfully rendered, usable part of the UI. Placing the marker inside the report route or under its error boundary lets the watchdog distinguish startup errors from a working view.

```razor
@using PulseOps.Sdk.Blazor

<PulseOpsErrorBoundary>
    <ChildContent>
        <ReportWorkspace />
        <PulseOpsReady />
    </ChildContent>
    <ErrorContent>
        <p role="alert">The reporting workspace is unavailable.</p>
    </ErrorContent>
</PulseOpsErrorBoundary>
```

If readiness requires a specific authentication or initial-load milestone, call `await pulseOps.MarkApplicationReadyAsync()` at that milestone and omit the component.

## Add a business operation

```csharp
using var operation = pulseOps.StartOperation("generate-report");
try
{
    await GenerateReportAsync(cancellationToken);
    operation.SetSuccess();
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    operation.SetCancelled();
    throw;
}
catch (Exception error)
{
    operation.SetFailure(error);
    throw;
}
```

Nested operations and instrumented HTTP requests share W3C trace context in-process. Their wall-clock durations can overlap, so they cannot be summed as exclusive time. To send `traceparent`, explicitly add the request origin to `TracePropagationOrigins` after the destination owner permits that CORS request header. SAP requests demonstrate client-observed dependency behavior. Backend logs and propagation require cooperation from the SAP team; HTTP 200 responses with business failures need explicit instrumentation.

Use `PulseOpsErrorBoundary` for render failures and `ReportException(exception)` for explicit capture. For existing `ILogger` integrations, add exact application categories to `LoggerCategories`. The provider reports Error or Critical entries that carry an exception; it never calls the message formatter. The browser and task handlers cover runtime events only when the corresponding APIs expose them.

## What the SDK records

| Kind | Fields and supported names |
| --- | --- |
| API | Approved alias, status when observable, latency, outcome, and a bounded cause category |
| Navigation | Approved alias on initial navigation and location change |
| Error | `managed-exception`, `render-exception`, `unobserved-task`, `javascript-error`, `promise-rejection` and a coarse category |
| Operation | Approved alias, elapsed wall time, outcome, and trace relationships |
| Browser metric | Measured `lcp`, `inp`, `cls`, `fcp`, `ttfb`, `navigation-duration`, or `long-task-count` when usable entries are available |
| Session | Random tab session identifier, `opened`, `online`, `offline`, `visible`, `hidden`, and best-effort `ended` |
| Startup | Anonymous milestone or fixed failure name, coarse category, time since the document's performance origin |

Use product-stable, low-cardinality aliases. The wire schema includes event ID, timestamp, application version, and session ID, plus W3C identifiers when available. Exception grouping uses aliases and coarse categories, so unrelated faults in the same category can group together. Stack traces, exception messages, HTTP headers and bodies, raw paths or query parameters, browser fingerprints, and identity attributes are excluded.

Browser metrics are emitted when supported after the initial measurement window and on page hiding. INP uses grouped Event Timing and the browser's native interaction count, with an upper bound on tracked interaction latencies. Missing ranks, hidden-only pages, and unsupported APIs yield no measurement. A back-forward cache restore does not start a separate measurement epoch. The browser can prevent all telemetry when startup JavaScript fails to run or network policy blocks intake. Default boot-fetch failures surfaced only through the Blazor startup rejection are grouped at that level. Independent version/cache mismatch and browser/OS breakdown are pending.

## Reliability and rollout

The managed client samples routine telemetry at the tab level and retains failures before routine events within a bounded in-memory queue. Failed batches retry with jitter. A 202 means accepted by the collector. The browser queue can lose events during overload, long outages, or tab termination. `GetDiagnostics()` reports counts since process startup. Timing and payload overhead should be measured on the monitored application because actual runtime and package composition vary.

Set `Enabled = false` for the managed client, disable individual capture flags, or call `PulseOpsClient.Disable()` from an application-provided configuration kill switch. For anonymous startup, omit the endpoint or set `data-enabled="false"`. An operator can revoke the relay credential or disable anonymous intake to stop server-side receipt. The SDK does not fetch its own remote configuration. Browser transport errors and browser timing exceptions have no role in the app’s business result. Standard .NET 10 WASM runs on a single browser thread, so the SDK keeps JSON batches small and serializes off the business call path rather than promising background-thread serialization.

## Local demo

From the repository root, build both hosts before starting them. Run the collector and sample in separate terminals. The collector database path can point to an unused temporary file for an isolated run.

```powershell
dotnet build
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:PulseOps__Demo = 'true'
$env:PulseOps__DatabasePath = Join-Path ([IO.Path]::GetTempPath()) "pulseops-demo-$(New-Guid).db"
dotnet run --project src/PulseOps.Server --no-build --no-launch-profile --urls http://localhost:5080
```

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project samples/PulseOps.Demo.Api --no-build --no-launch-profile
```

On Unix shells, pass the same settings as environment variables prefixed to each command. Avoid rebuilding the portal while testing a running development host because the host serves fingerprinted assets from the development build output.

Open `http://localhost:5091/` and select a scenario. The workspace uses generated ledger rows. Launch two independent browser contexts to produce independent sessions. Use `Open a page with a startup failure` to deliberately request a missing framework script. With the collector stopped, a normal report still uses the local mock backend and completes. The sample hosts both the browser and mock API on the same loopback origin. It uses a development relay credential that is valid only with the collector’s explicit demo mode. The sample is unsuitable for deployment with live data or enterprise credentials.

## Browser verification

Use a fresh local demo database. Start both demo hosts, then install the matched headless Chromium using the Playwright script generated at build time.

```powershell
dotnet build tests/PulseOps.EndToEndTests
pwsh tests/PulseOps.EndToEndTests/bin/Debug/net10.0/playwright.ps1 install chromium --only-shell
$env:PULSEOPS_E2E = '1'
dotnet test tests/PulseOps.EndToEndTests --no-build
```

On Unix shells use `PULSEOPS_E2E=1 dotnet test tests/PulseOps.EndToEndTests --no-build`. The run covers a simulated 503, durable processing into a scoped incident, a linked session and parent span, portal acknowledgement and resolution, JavaScript and component rendering errors, anonymous startup reporting with no WASM runtime, and a successful business report with browser telemetry requests blocked. Run against disposable demo data because the test changes incident status. If browser downloads need an allowlisted internal mirror, use your organisation's Playwright distribution.

On macOS ARM64 with .NET SDK 10.0.404, two Release publishes of the demo kept the mock business workflow the same and removed the SDK reference, registration, handlers, operation and error hooks, and bootstrap from the baseline. The per-asset Brotli totals under `wwwroot`, excluding the deliberately broken startup page, were 2,843,788 bytes with PulseOps and 2,796,507 bytes without it. The measured increase was **47,281 bytes (46.17 KiB)**. The SDK WebCIL assembly occupied 25,819 Brotli bytes and the startup bridge 3,485 Brotli bytes. This measures packaged browser assets and includes changes from trimming. System CPU and runtime memory require additional profiling.

A separate local Chromium 159 run served both published builds from one loopback host with Brotli and used a fresh browser context for each navigation. It measured navigation start to the first render of the report workspace heading. Two warm-up pairs were discarded, and twenty measured pairs ran in randomised A/B order with the anonymous intake request blocked.

| Release build | Median first render | P95 first render |
| --- | ---: | ---: |
| Without SDK | 172.3 ms | 199.4 ms |
| With SDK | 202.6 ms | 248.6 ms |
| Observed difference | +30.3 ms | +49.2 ms |

These timings represent local Chromium and a small simulated reporting app. Cold-start impact under corporate browser policy, slower devices and enterprise networks remains unmeasured.
