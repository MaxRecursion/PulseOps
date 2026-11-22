using System.Net.Http.Json;
using Microsoft.Playwright;
using NUnit.Framework;
using static Microsoft.Playwright.Assertions;

namespace PulseOps.EndToEndTests;

[TestFixture, NonParallelizable, Category("Browser")]
public sealed class BrowserWorkflowTests
{
    private const string Scope = "application=finance-demo&environment=demo&hours=1";
    private readonly Uri _demo = new("http://localhost:5091");
    private readonly Uri _collector = new(Environment.GetEnvironmentVariable("PULSEOPS_E2E_COLLECTOR") ?? "http://localhost:5080");
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private HttpClient? _operatorApi;

    [OneTimeSetUp]
    public async Task StartBrowser()
    {
        if (Environment.GetEnvironmentVariable("PULSEOPS_E2E") != "1")
            Assert.Ignore("Start the two local demo hosts and set PULSEOPS_E2E=1. See docs/SDK.md.");

        Assert.That(_collector.IsLoopback, Is.True, "Browser tests require a disposable local demo collector.");
        _operatorApi = new HttpClient { BaseAddress = _collector, Timeout = TimeSpan.FromSeconds(5) };
        using var collectorResponse = await _operatorApi.GetAsync("/health/ready");
        using var demoResponse = await _operatorApi.GetAsync(new Uri(_demo, "/api/health"));
        Assert.That(collectorResponse.IsSuccessStatusCode, Is.True, "The local demo collector must be ready.");
        Assert.That(demoResponse.IsSuccessStatusCode, Is.True, "The sample must be ready on port 5091.");
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
    public async Task FailedReportCorrelatesWithASessionAndSupportsIncidentTriage()
    {
        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(_demo.AbsoluteUri);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Reporting workspace" })).ToBeVisibleAsync();
        var sessionId = await GetTabSessionId(page);

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
        await page.GetByLabel("Reporting scenario").SelectOptionAsync("http503");
        await page.GetByRole(AriaRole.Button, new() { Name = "Run report" }).ClickAsync();
        await Expect(page.GetByText("The simulated reporting API returned HTTP 503.")).ToBeVisibleAsync();

        var events = await EventuallyAsync(async () =>
        {
            var items = await GetEvents(sessionId);
            return items.Any(item => item is { Kind: "api", StatusCode: 503 }) &&
                   items.Any(item => item is { Kind: "operation", Name: "generate-report", Outcome: "failure" })
                ? items
                : null;
        }, "The PulseOps worker never processed the failed browser request and its operation.");
        var failedRequest = events.Single(item => item is { Kind: "api", StatusCode: 503 });
        var operation = events.Single(item => item is { Kind: "operation", Name: "generate-report", Outcome: "failure" });
        Assert.Multiple(() =>
        {
            Assert.That(failedRequest.Name, Is.EqualTo("report-api"));
            Assert.That(failedRequest.SessionId, Is.EqualTo(sessionId));
            Assert.That(failedRequest.TraceId, Is.Not.Null);
            Assert.That(failedRequest.TraceId, Is.EqualTo(operation.TraceId));
            Assert.That(failedRequest.ParentSpanId, Is.EqualTo(operation.SpanId));
        });

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
        string SessionId, string Kind, string Name, string Outcome, int? StatusCode,
        string? TraceId, string? SpanId, string? ParentSpanId, string? Category, bool Anonymous);

    private sealed record Incident(string Id, string Name, DateTimeOffset LastSeen);
}
