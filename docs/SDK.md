# Blazor WebAssembly SDK

PulseOps instruments standalone Blazor WebAssembly applications targeting .NET 10. Add the NuGet package to the WebAssembly project that runs in the browser. For a hosted standalone app, the client project runs the SDK and the app host or gateway runs the relay. For an app served as static files, put the relay behind the same public origin through the existing authenticated gateway.

Managed telemetry goes from the browser to an authenticated, same-origin app relay, then to the PulseOps collector over HTTPS. Operators sign in separately to the PulseOps portal. The optional anonymous startup route serves a narrower purpose and has its own setup below.

## Register the collector scope

Start with a staging scope and an approved test account. Agree on the actual application identifier, environment identifier, deployed application version and allowlist before instrumenting the client. Use the same identifiers in the collector, relay and SDK.

| Collector configuration | Set it to |
| --- | --- |
| `PulseOps:Scopes` entry with `Application`, `Environment` and `Label` | One entry per monitored application and environment. Use lowercase slugs up to 40 characters for the identifiers and a readable portal label. |
| `AllowedNames` | The exact API, navigation and operation aliases plus each fixed error, session and metric name emitted by the capture features you enable. The client example below uses `sap-report`, `reports` and `generate-report`. Use the [capture table](#what-the-sdk-records) and the [operations configuration example](Operations.md). One unknown alias rejects the entire batch. |
| `ViewerGroups` | The exact group claims for the scoped operators who will verify the integration. Confirm they also have an accepted PulseOps role. |
| `IngestToken` | A unique machine credential provisioned for this scope through the approved secret store and injected on the collector and relay hosts. |
| `StartupOrigins` | An empty list for managed telemetry. If you enable the optional direct startup route, add the exact app HTTPS origin, including any nondefault port and omitting the trailing slash. |

Record the release identifier in `PulseOpsOptions.Version` as a stable label of up to 32 characters. Use letters, digits, dots, underscores and hyphens, starting with a letter or digit. Deployment markers are a separate CI integration. [Collector configuration and operator access](Operations.md#identity-and-application-registration) · [Release markers](../deployment/teamcity/README.md)

## Configure the app-owned relay

Implement `POST /pulseops/telemetry` on the monitored app’s host or gateway. The SDK includes same-origin credentials with this request. Its exporter uses its own HTTP client, so the app's named business API clients and their authorization handlers do not supply relay credentials.

1. Authenticate the app session and authorize telemetry for that application and environment. Require the exact app origin and validate the app's antiforgery token before forwarding. Return an HTTP error for expired or unauthorized sessions, without redirecting to sign-in.
2. Require JSON and set the request body limit at the proxy and relay before buffering or forwarding. The collector accepts up to 128 KiB per request and 100 events on the managed intake route. The SDK sends at most 50 events per batch. Match the deployed proxy's lower limit if it has one.
3. Bind application and environment on the server. A relay can compare the incoming envelope to its configured scope and reject mismatches, or set the scope from a trusted server-side mapping after authorizing it. Preserve schema version and event IDs. Select the destination and machine credential from server configuration, independently of client input.
4. Forward JSON over HTTPS to `POST /api/telemetry/v1/batch` on the approved collector with the scope’s machine bearer credential. Send only the headers the collector needs. Keep app cookies, browser authorization values and antiforgery tokens on the app side of the relay. Disable upstream redirects and bound the upstream request timeout.
5. Pass through the upstream status and `Retry-After`. Return 202 only after receiving the collector’s 202, which follows its durable commit. Treat upstream timeouts and connection errors as transient failures, for example with 503. Measure safe relay diagnostics such as status, duration and the server-configured scope without logging request bodies or credential values.

For a cookie-authenticated relay, configure the monitored app’s antiforgery validator to accept the `X-CSRF-TOKEN` request header. Issue the matching cookie and request token through the app's authenticated flow and keep the request token in memory. For an ASP.NET Core host, this can use `IAntiforgery.GetAndStoreTokens` to issue the pair and `IsRequestValidAsync` in the relay. Serve any token response with `Cache-Control: no-cache, no-store` and `Pragma: no-cache`. Refresh the app request token with the session and clear the in-memory value on sign-out. PulseOps' operator API uses the separate `X-PulseOps-CSRF` header and its own cookie.

The [demo relay](../samples/PulseOps.Demo.Api/Program.cs) illustrates forwarding and status propagation on loopback. Use your app's production session, scope authorization and antiforgery integration.

## Wire the SDK into the WASM client

Restore the approved `PulseOps.Sdk.Blazor` package from your internal NuGet source. Run this example from the browser project's directory, substituting the version approved for your app. If your app uses central package management, pin the approved version in its `Directory.Packages.props` and add a `PackageReference` in the browser project. [Internal package promotion](../deployment/teamcity/README.md#promote-the-sdk) · [Pack for local development](../README.md#pack-the-blazor-sdk)

```bash
dotnet add package PulseOps.Sdk.Blazor --version 0.1.0
```

In the existing WASM `Program.cs`, add `AddPulseOps` before `builder.Build()` and start the SDK before `host.RunAsync()`. Add `PulseOpsHttpHandler` to the business API clients that should emit observations. Keep the app's authentication, retry and timeout configuration. Verify whether the chosen handler order measures the complete retried request or each attempt, according to how the app's retry handler wraps the chain.

```csharp
using PulseOps.Sdk.Blazor;

builder.Services.AddPulseOps(options =>
{
    options.Application = "finance-dashboard";
    options.Environment = "staging";
    options.Version = "1.4.0";
    options.IngestionEndpoint = "/pulseops/telemetry";
    options.SamplingRate = 1; // Staging acceptance. Use the agreed sampling rate for rollout.
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

The `Reports` registration illustrates the handler attachment. Its callers inject `IHttpClientFactory` and use `CreateClient("Reports")`. For an existing configured client, extend its registration and keep the existing callers and base address. `AddHttpClient` comes from `Microsoft.Extensions.Http`. Add the dependency at an approved .NET 10-compatible version if the client project does not already reference it. If the app is served under a path prefix, use the relay path that the public host actually serves. A path starting with `/` resolves from the origin root.

If components already inject a plain `HttpClient` created by the default Blazor template, replace that simple registration with the factory-backed form below, before `builder.Build()`. The components can keep their existing injection. Set `baseAddress` to the same address the app already uses and add its actual request paths to `ApiEndpoints` with approved aliases. Keep any existing credential handler on the named client.

```csharp
var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddHttpClient("AppApi", client => client.BaseAddress = baseAddress)
    .AddHttpMessageHandler<PulseOpsHttpHandler>();
builder.Services.AddScoped<HttpClient>(services =>
    services.GetRequiredService<IHttpClientFactory>().CreateClient("AppApi"));
```

In the same `AddPulseOps` block for a cookie-authenticated relay, wire the callback to the app's in-memory request-token state. `appAntiforgeryState` here represents the existing or newly integrated app-owned token service.

```csharp
options.GetRelayAntiforgeryTokenAsync =
    _ => ValueTask.FromResult<string?>(appAntiforgeryState.RequestToken);
```

The callback runs during export with a one-second budget. Return `null` while the request token is temporarily unavailable. PulseOps retries with backoff. Let the app handle the login flow. The SDK places the returned token in the relay header, and the machine bearer stays in server configuration.

For exact API URLs, the handler matches the scheme, host and path, or a configured absolute path. For parameterised routes, set an explicitly registered stable alias with `request.Options.Set(PulseOpsHttpHandler.EndpointAlias, "sap-report")` when constructing the request. The alias must already appear as a value in `options.ApiEndpoints` and in the collector scope's `AllowedNames`. For an existing manually constructed `HttpClient`, insert the DI-provided handler into its chain while retaining its lifetime and other handlers.

## Early startup and browser metrics

The managed SDK can record operations, managed exceptions and instrumented HTTP requests on its own. To add browser performance, browser JavaScript errors and browser lifecycle signals, load `_content/PulseOps.Sdk.Blazor/bootstrap.js` before the Blazor loader in the deployed `wwwroot/index.html`. For managed-only collection, omit `data-startup-endpoint` and keep the app's existing startup flow.

For anonymous pre-runtime diagnostics, also register the exact HTTPS app origin under `StartupOrigins` on the matching collector scope and set `data-startup-endpoint`. This route accepts rate-limited startup milestones and failures without browser credentials. Set script attributes from the same application, environment and version used in `AddPulseOps`. The marked startup flow calls `Blazor.start` from a separate script to preserve the app's CSP.

```html
<script src="_content/PulseOps.Sdk.Blazor/bootstrap.js" defer
        data-application="finance-dashboard"
        data-environment="staging"
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

Use your app's existing failure callback in place of `handleExistingStartupError`. Pass the same options object to the chosen start function if the app already uses `Blazor.start(options)`. Keep one start call and retain the app's boot-resource overrides and integrity handling. Add the collector origin to the app's `connect-src` only for the direct startup route. [ASP.NET Core Blazor startup](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/startup?view=aspnetcore-10.0)

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

Inject `PulseOpsClient` into the component or service that owns the operation. Use a stable alias from `AllowedOperations` and the matching collector scope's `AllowedNames`. Declare the operation before the call so nested instrumented requests inherit its trace context.

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

## Verify the integration in staging

Use the consuming app with its actual authentication, relay and reverse proxy, against a dedicated staging scope with generated test inputs. For the pilot, set `SamplingRate = 1` so routine success records are observable. Choose one API alias and one enclosing operation. Record the app version and UTC test window so an operator can find the same events.

1. **Prove the collector and scope are ready.** Check `/health/ready` on the collector for 200. Sign in to the PulseOps portal as a scoped operator and confirm the expected **Application** and **Environment** are selectable. Test that an operator assigned only to another staging scope cannot see the pilot's data.
2. **Prove authenticated receipt.** Sign in to the monitored app and perform the known operation successfully. In the browser's Network panel, wait through the configured `FlushInterval` (10 seconds by default) and find `POST /pulseops/telemetry`. Confirm the destination is the app origin and the response is 202. Inspect the status and timing for evidence. Keep request details, cookies and tokens out of screenshots, HAR files and logs.
3. **Prove processing and useful correlation.** In the PulseOps sidebar, choose **Explore → Logs**. On **Event explorer**, select the same **Application**, **Environment**, **Version** and **Time range**. Filter by **Event type** and the exact **Event alias**, then choose **Search**. Confirm the expected time, outcome, HTTP status, and version. Repeat for the enclosing operation, then open **Session** and **Trace** from the results to verify the two observations correlate. If needed, use **Refresh data** after the worker processes the batch. The HTTP 202 proves receipt. Seeing the new row proves the worker made it queryable in the pilot scope.
4. **Prove failure visibility and business isolation.** In staging, trigger a controlled failure on the approved test operation and verify the matching API status or coarse failure category and operation outcome in **Logs**. Then block only the app's telemetry relay request in a test browser, run an otherwise healthy operation, and confirm its result still succeeds. Restore connectivity in the same tab and confirm a fresh labeled operation reaches the portal. A bounded transient outage can also be used to verify a queued event retries with its original event ID. Do any incident or notification walkthrough against a staging rule and destination with agreed thresholds.
5. **Prove rejection and optional startup separately.** Using a staging test client, verify the relay rejects a signed-out request, a missing or invalid antiforgery token, a mismatched scope and an oversized body without forwarding them. If anonymous startup is enabled, verify a new staging tab yields an `ApplicationReady` event and use an isolated test page to exercise a startup failure. In **Logs**, startup events from this route show **Anonymous startup report**. Verify an unregistered origin cannot submit them.
6. **Record the result before rollout.** Record the SDK and app versions, scope, UTC window, observed POST status, corresponding portal event and correlation result, negative test outcomes, and approved changes. Set the agreed production `SamplingRate` before enabling the SDK for a limited deployment. If needed, turn off the affected capture features or use the managed and bootstrap controls in [Reliability and rollout](#reliability-and-rollout).

## Troubleshoot the integration

| Symptom | First checks |
| --- | --- |
| Browser sends no managed telemetry | Verify `AddPulseOps`, `StartAsync`, `IsEnabled`, matching aliases on the actual HTTP client, and that the request's host and path match. Check `SamplingRate`, capture flags, app token availability and `FlushInterval`. A missing or delayed request-token callback result delays sending. |
| Relay returns 401 or 403 | Check the app session, exact origin, scope authorization and app antiforgery cookie and request token. If the relay reached the collector, inspect the safe upstream status and verify the configured scope and machine credential at both hosts. |
| Relay or collector returns 400 or 413 | Check body limits, the batch contract, allowed aliases including fixed SDK events, version, and client clock. If the relay uses 400 for antiforgery rejection, check the app's validation first. |
| Relay returns 410 | Ask the collector operator to check `IntakeSuspended` for the scope. After authorized reactivation, reload the test app to start a fresh managed client. |
| Relay returns 429 or 503 | Check the upstream status and `Retry-After`, then collector readiness, disk reserve, inbox limits, and request fan-in through the relay. A platform administrator can inspect **Platform health** for **Pending items**, **Oldest pending item**, **Last worker heartbeat**, and **Dead letters**. |
| Relay returns 202 but no portal results appear | Check the portal operator's role and viewer groups, actual app version, UTC time range and exact event filters. Refresh the results and ask a platform administrator to check whether the inbox is draining or records were quarantined. |
| Anonymous startup events are missing | Verify the bootstrap asset, one Blazor start call, the configured startup endpoint, exact origin, `connect-src`, and the point where the app signals readiness. Test the direct startup request separately from the app relay. |

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

The managed client samples routine telemetry at the tab level and retains failures before routine events within a bounded in-memory queue. Transient failures requeue the same event IDs for later attempts, with capped backoff, jitter, and support for `Retry-After`. In-flight events count against the queue limit. The client discards events older than 23 hours when selecting or requeuing a batch, to stay within the collector's one-day event-age limit. Permanent HTTP rejection drops the batch. HTTP 410 also disables managed collection and clears the queue for the session. A 202 means the collector committed the batch. The in-memory queue can lose events during overload, long outages, or tab termination. `GetDiagnostics()` reports counts since process startup. Measure timing and payload overhead on the monitored application because runtime and package composition vary.

Set `Enabled = false` for the managed client, disable individual capture flags, or call `PulseOpsClient.Disable()` from an application-provided configuration kill switch. For anonymous startup, omit the endpoint or set `data-enabled="false"`. An operator can revoke the relay credential or disable anonymous intake to stop server-side receipt. The SDK does not fetch its own remote configuration. Browser transport errors and browser timing exceptions have no role in the app’s business result. Standard .NET 10 WASM runs on a single browser thread, so the SDK keeps JSON batches small and serializes off the business call path rather than promising background-thread serialization.

## Local demo

Use the [one-command demo launcher](../README.md#quick-start) for a fresh SQLite database and both loopback hosts. The [walkthrough](../README.md#run-the-local-demo-workflow) shows how to trigger success, latency, an HTTP 503, and browser diagnostics, then follow each run to its PulseOps trace or scope.

To run the hosts independently, build both before starting them. You can point the collector at an unused temporary database for an isolated run.

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

Open `http://localhost:5091/` and select a scenario. The workspace uses generated ledger rows. Launch two independent browser contexts to produce independent sessions. Use **Open a page with a startup failure** to deliberately request a missing framework script. Block only the telemetry route in a test browser to show that a normal report still completes. The sample hosts both the browser and mock API on the same loopback origin and uses a development relay credential for the collector's explicit demo mode.

## Browser verification

Use a fresh local demo database. Start both demo hosts, then install the matched headless Chromium using the Playwright script generated at build time.

```powershell
dotnet build tests/PulseOps.EndToEndTests
pwsh tests/PulseOps.EndToEndTests/bin/Debug/net10.0/playwright.ps1 install chromium --only-shell
$env:PULSEOPS_E2E = '1'
dotnet test tests/PulseOps.EndToEndTests --no-build
```

On Unix shells use `PULSEOPS_E2E=1 dotnet test tests/PulseOps.EndToEndTests --no-build`. For alternate launcher ports, set `PULSEOPS_E2E_COLLECTOR` and `PULSEOPS_E2E_DEMO` to the printed loopback origins. The browser checks exercise the demo against disposable data, including scoped incident triage, correlated spans, browser and startup errors, and the business workflow with telemetry requests blocked. If browser downloads need an allowlisted internal mirror, use your organisation's Playwright distribution.

On macOS ARM64 with .NET SDK 10.0.404, two Release publishes of the demo kept the mock business workflow the same and removed the SDK reference, registration, handlers, operation and error hooks, and bootstrap from the baseline. The per-asset Brotli totals under `wwwroot`, excluding the deliberately broken startup page, were 2,843,788 bytes with PulseOps and 2,796,507 bytes without it. The measured increase was **47,281 bytes (46.17 KiB)**. The SDK WebCIL assembly occupied 25,819 Brotli bytes and the startup bridge 3,485 Brotli bytes. This measures packaged browser assets and includes changes from trimming. System CPU and runtime memory require additional profiling.

A separate local Chromium 159 run served both published builds from one loopback host with Brotli and used a fresh browser context for each navigation. It measured navigation start to the first render of the report workspace heading. Two warm-up pairs were discarded, and twenty measured pairs ran in randomised A/B order with the anonymous intake request blocked.

| Release build | Median first render | P95 first render |
| --- | ---: | ---: |
| Without SDK | 172.3 ms | 199.4 ms |
| With SDK | 202.6 ms | 248.6 ms |
| Observed difference | +30.3 ms | +49.2 ms |

These timings represent local Chromium and a small simulated reporting app. Cold-start impact under corporate browser policy, slower devices and enterprise networks remains unmeasured.
