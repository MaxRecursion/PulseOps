using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;
using static Microsoft.Playwright.Assertions;

namespace PulseOps.EndToEndTests;

[TestFixture, NonParallelizable, Category("Browser")]
public sealed class BrowserWorkflowTests
{
    private const string DemoApplication = "finance-demo";
    private const string DemoEnvironment = "demo";
    private const string Scope = "application=" + DemoApplication + "&environment=" + DemoEnvironment + "&hours=1";
    private readonly Uri _demo = new(Environment.GetEnvironmentVariable("PULSEOPS_E2E_DEMO") ?? "http://localhost:5091");
    private readonly Uri _collector = new(Environment.GetEnvironmentVariable("PULSEOPS_E2E_COLLECTOR") ?? "http://localhost:5080");
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private HttpClient? _operatorApi;

    [OneTimeSetUp]
    public async Task StartBrowser()
    {
        if (Environment.GetEnvironmentVariable("PULSEOPS_E2E") != "1")
            Assert.Ignore("Start the two local demo hosts and set PULSEOPS_E2E=1. See docs/SDK.md.");

        Assert.That(IsLoopbackOrigin(_demo), Is.True, "PULSEOPS_E2E_DEMO must point to a loopback HTTP or HTTPS origin.");
        Assert.That(IsLoopbackOrigin(_collector), Is.True, "PULSEOPS_E2E_COLLECTOR must point to a disposable loopback HTTP or HTTPS origin.");
        _operatorApi = new HttpClient { BaseAddress = _collector, Timeout = TimeSpan.FromSeconds(5) };
        using var collectorResponse = await _operatorApi.GetAsync("/health/ready");
        using var demoResponse = await _operatorApi.GetAsync(new Uri(_demo, "/api/health"));
        Assert.That(collectorResponse.IsSuccessStatusCode, Is.True, "The local demo collector must be ready.");
        Assert.That(demoResponse.IsSuccessStatusCode, Is.True, "The sample must be ready at the configured demo origin.");
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("PULSEOPS_CHROMIUM_EXECUTABLE")
        });
    }

    [OneTimeTearDown]
    public async Task StopBrowser()
    {
        if (_browser is not null)
            await _browser.CloseAsync();
        _playwright?.Dispose();
        _operatorApi?.Dispose();
    }

    [Test]
    public async Task QuickReportScenariosLinkToMatchingTracesAndSupportIncidentTriage()
    {
        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(_demo.AbsoluteUri);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Reporting workspace" })).ToBeVisibleAsync();
        var sessionId = await GetTabSessionId(page);

        var successTrace = await RunReportAsync(page, "Run successful report", sessionId, "success", 200);
        await Expect(page.GetByText("Completed", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Cell, new() { Name = "GL-3000" })).ToBeVisibleAsync();
        await VerifyRunLinkAsync(page, successTrace, "success", 200);

        var slowTrace = await RunReportAsync(page, "Simulate slow report", sessionId, "success", 200, minimumDuration: 2_900);
        await VerifyRunLinkAsync(page, slowTrace, "success", 200);

        var portal = await context.NewPageAsync();
        portal.PageError += (_, error) => TestContext.Error.WriteLine(error);
        portal.Console += (_, message) =>
        {
            if (message.Type == "error")
                TestContext.Error.WriteLine(message.Text);
        };
        await portal.GotoAsync(new Uri(_collector, "/incidents?" + Scope).AbsoluteUri);
        await Expect(portal.GetByRole(AriaRole.Heading, new() { Name = "Incident activity" })).ToBeVisibleAsync(new() { Timeout = 30_000 });

        var failureStart = DateTimeOffset.UtcNow;
        var failureTrace = await RunReportAsync(page, "Trigger HTTP 503", sessionId, "failure", 503);
        Assert.That(failureTrace, Is.Not.EqualTo(successTrace));
        await Expect(page.GetByText("The simulated reporting API returned HTTP 503.")).ToBeVisibleAsync();
        await VerifyRunLinkAsync(page, failureTrace, "failure", 503);

        var incident = await EventuallyAsync(async () =>
        {
            var incidents = await _operatorApi!.GetFromJsonAsync<List<Incident>>("/api/v1/incidents?" + Scope);
            return incidents?.FirstOrDefault(item =>
                item.Name == "report-api" && item.LastSeen >= failureStart.AddMilliseconds(-10));
        }, "No incident recorded the current 503 from the sample application.");

        var incidentLink = portal.Locator($"a[href^='incidents/{incident.Id}']");
        await Expect(incidentLink).ToBeVisibleAsync(new() { Timeout = 35_000 });
        await incidentLink.ClickAsync();
        await Expect(portal.GetByRole(AriaRole.Heading, new() { Name = "Observed evidence" })).ToBeVisibleAsync();

        await portal.GetByLabel("Status").SelectOptionAsync("Acknowledged");
        await portal.GetByLabel("Comment").FillAsync("Acknowledged simulated gateway failure.");
        await portal.GetByRole(AriaRole.Button, new() { Name = "Save update" }).ClickAsync();
        await Expect(portal.GetByText("Acknowledged simulated gateway failure.")).ToBeVisibleAsync();

        await portal.GetByLabel("Status").SelectOptionAsync("Resolved");
        await portal.GetByLabel("Comment").FillAsync("Resolved simulated gateway failure.");
        await portal.GetByRole(AriaRole.Button, new() { Name = "Save update" }).ClickAsync();
        await Expect(portal.GetByText("Resolved simulated gateway failure.")).ToBeVisibleAsync();

        await portal.GotoAsync(new Uri(_collector, $"/sessions/{sessionId}?" + Scope).AbsoluteUri);
        await Expect(portal.GetByRole(AriaRole.Heading, new() { Name = "Session timeline" })).ToBeVisibleAsync();
        await Expect(portal.GetByText("HTTP 503", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Test]
    public async Task MissingFrameworkAssetIsReportedBeforeWebAssemblyStarts()
    {
        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(_demo, "/boot-failure.html").AbsoluteUri);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Startup failure" })).ToBeVisibleAsync();
        var sessionId = await GetTabSessionId(page);
        Assert.That(await page.EvaluateAsync<bool>("() => typeof globalThis.Blazor === 'undefined'"), Is.True);

        var events = await EventuallyAsync(async () =>
        {
            var items = await GetEvents(sessionId);
            return items.Any(item => item.Name == "AssetLoadFailure") &&
                   items.Any(item => item.Name == "BlankScreen") ? items : null;
        }, "The anonymous resource failure and readiness watchdog were not received.");
        Assert.That(events.Where(item => item.Kind == "startup").All(item => item.Anonymous), Is.True);
    }

    [Test]
    public async Task JavaScriptAndRenderingExceptionsUseBoundedSafeCategories()
    {
        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(_demo.AbsoluteUri);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Reporting workspace" })).ToBeVisibleAsync();
        var sessionId = await GetTabSessionId(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Raise a JavaScript error" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Raise a rendering error" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Rendering failure recorded.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Recover this panel" }).ClickAsync();
        await Expect(page.GetByText("Rendering failure recorded.")).ToBeHiddenAsync();

        var events = await EventuallyAsync(async () =>
        {
            var items = await GetEvents(sessionId);
            return items.Any(item => item is { Kind: "error", Name: "javascript-error" }) &&
                   items.Any(item => item is { Kind: "error", Name: "render-exception" }) ? items : null;
        }, "The browser did not deliver both runtime errors for the current session.");

        Assert.That(events.Where(item => item.Kind == "error").Select(item => item.Category),
            Is.EquivalentTo(new[] { "javascript", "invalid-operation" }));
    }

    [Test]
    public async Task ReportingStillWorksWhenBrowserTelemetryNetworkIsUnavailable()
    {
        await using var context = await _browser!.NewContextAsync();
        await context.RouteAsync("**/api/telemetry/v1/startup", route => route.AbortAsync());
        await context.RouteAsync("**/pulseops/telemetry", route => route.AbortAsync());
        var page = await context.NewPageAsync();
        await page.GotoAsync(_demo.AbsoluteUri);
        await page.GetByLabel("Reporting scenario").SelectOptionAsync("normal");
        await page.GetByRole(AriaRole.Button, new() { Name = "Run report" }).ClickAsync();

        await Expect(page.GetByText("Completed", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Cell, new() { Name = "GL-3000" })).ToBeVisibleAsync();
    }

    private async Task<string> RunReportAsync(IPage page, string button, string sessionId, string outcome, int statusCode,
        int? minimumDuration = null)
    {
        var relay = new Uri(_demo, "/pulseops/telemetry").AbsoluteUri;
        var runLink = page.GetByRole(AriaRole.Link, new() { Name = "View this run in PulseOps", Exact = true });
        var priorLink = await runLink.CountAsync() == 0 ? null : await runLink.GetAttributeAsync("href");
        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync(),
            candidate => candidate.Url == relay && candidate.Request.Method == "POST" && candidate.Status == 202,
            new() { Timeout = 30_000 });
        if (priorLink is not null)
            await Expect(runLink).Not.ToHaveAttributeAsync("href", priorLink);
        const string tracePrefix = "/pulseops/portal/trace/";
        await Expect(runLink).ToHaveAttributeAsync("href", new Regex("^" + tracePrefix + "[0-9a-f]{32}$"));
        var traceId = (await runLink.GetAttributeAsync("href"))![tracePrefix.Length..];

        // Deliberately query every visible scope. A copy in another demo scope must fail the test.
        var events = await EventuallyAsync(async () =>
        {
            var items = await _operatorApi!.GetFromJsonAsync<List<ObservedEvent>>(
                "/api/v1/events?hours=1&traceId=" + Uri.EscapeDataString(traceId)) ?? [];
            return items.Any(item => item is { Kind: "api", Name: "report-api" } && item.StatusCode == statusCode) &&
                   items.Any(item => item is { Kind: "operation", Name: "generate-report" } && item.Outcome == outcome)
                ? items
                : null;
        }, $"The worker did not process the accepted {outcome} report and its API request.");

        var request = events.Single(item => item is { Kind: "api", Name: "report-api" } && item.StatusCode == statusCode);
        var operation = events.Single(item => item is { Kind: "operation", Name: "generate-report" });
        Assert.Multiple(() =>
        {
            Assert.That(events.Select(item => (item.Application, item.Environment)), Has.All.EqualTo((DemoApplication, DemoEnvironment)));
            Assert.That(events.Select(item => item.SessionId), Has.All.EqualTo(sessionId));
            Assert.That(request.Outcome, Is.EqualTo(outcome));
            Assert.That(request.TraceId, Is.EqualTo(operation.TraceId));
            Assert.That(operation.TraceId, Is.EqualTo(traceId));
            Assert.That(operation.SpanId, Is.Not.Null.And.Not.Empty);
            Assert.That(request.ParentSpanId, Is.EqualTo(operation.SpanId));
            if (minimumDuration is int duration)
                Assert.That(request.DurationMs, Is.GreaterThanOrEqualTo(duration));
        });
        return traceId;
    }

    private async Task VerifyRunLinkAsync(IPage page, string traceId, string outcome, int statusCode)
    {
        var runLink = page.GetByRole(AriaRole.Link, new() { Name = "View this run in PulseOps", Exact = true });
        var trace = await page.RunAndWaitForPopupAsync(() => runLink.ClickAsync());
        await Expect(trace.GetByRole(AriaRole.Heading, new() { Name = "Trace explorer" })).ToBeVisibleAsync(new() { Timeout = 30_000 });

        var destination = new Uri(trace.Url);
        Assert.Multiple(() =>
        {
            Assert.That(destination.GetLeftPart(UriPartial.Authority), Is.EqualTo(_collector.GetLeftPart(UriPartial.Authority)));
            Assert.That(destination.AbsolutePath, Is.EqualTo("/traces"));
        });
        await Expect(trace.GetByRole(AriaRole.Combobox, new() { NameRegex = new Regex("^Application") })).ToHaveValueAsync(DemoApplication);
        await Expect(trace.GetByRole(AriaRole.Combobox, new() { NameRegex = new Regex("^Environment") })).ToHaveValueAsync(DemoEnvironment);
        await Expect(trace.GetByLabel("Trace ID", new() { Exact = true })).ToHaveValueAsync(traceId);

        var correlated = trace.Locator("section").Filter(new()
        {
            Has = trace.GetByRole(AriaRole.Heading, new() { Name = "Correlated events" })
        });
        var request = correlated.GetByRole(AriaRole.Row).Filter(new() { HasText = "report-api" });
        var operation = correlated.GetByRole(AriaRole.Row).Filter(new() { HasText = "generate-report" });
        await Expect(request).ToContainTextAsync($"HTTP {statusCode}");
        await Expect(operation.GetByText(outcome, new() { Exact = true })).ToBeVisibleAsync();
        await trace.CloseAsync();
    }

    private static bool IsLoopbackOrigin(Uri uri) =>
        uri.IsLoopback && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Fragment.Length == 0;

    private async Task<List<ObservedEvent>> GetEvents(string sessionId) =>
        await _operatorApi!.GetFromJsonAsync<List<ObservedEvent>>("/api/v1/events?" + Scope + "&sessionId=" + sessionId) ?? [];

    private static async Task<string> GetTabSessionId(IPage page)
    {
        var value = await page.EvaluateAsync<string?>("() => sessionStorage.getItem('pulseops.session')");
        Assert.That(value, Is.Not.Null.And.Not.Empty);
        return value!;
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, string message) where T : class
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            while (true)
            {
                if (await read() is { } value)
                    return value;
                await Task.Delay(TimeSpan.FromMilliseconds(250), budget.Token);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            throw new AssertionException(message);
        }
    }

    private sealed record ObservedEvent(
        string Application, string Environment, string SessionId, string Kind, string Name, string Outcome, int? StatusCode, double? DurationMs,
        string? TraceId, string? SpanId, string? ParentSpanId, string? Category, bool Anonymous);

    private sealed record Incident(string Id, string Name, DateTimeOffset LastSeen);
}
