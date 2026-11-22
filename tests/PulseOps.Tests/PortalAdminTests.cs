using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using PulseOps.Portal.Components;
using PulseOps.Portal.Pages;
using PulseOps.Portal.Services;
using Policy = PulseOps.Server.MonitorPolicy;

namespace PulseOps.Tests;

/// <summary>Monitors, history and change-control pages rendered against contract-shaped JSON from a stub API.</summary>
public sealed class PortalAdminTests
{
    private const string Csrf = "csrf-token-1";
    private const string IncidentId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string ApprovableId = "6b7d1c1e-5c3a-4f07-9d55-1f0e8f0d2a11";
    private const string WithdrawableId = "c2a4b9f0-8e1d-4c4b-a7f3-58e2d7a9b310";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly ScopeEntry[] Scopes =
    [
        new("finance-reports", "production", "Finance Reporting", true),
        new("payments-gateway", "production", "Payments Gateway", true)
    ];
    private static readonly DateTimeOffset Evaluated = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task MonitorsPageShowsStateBadgesThresholdsAndScopedIncidentLinks()
    {
        var api = new StubApi().On(HttpMethod.Get, "/api/v1/monitors", Json(new[]
        {
            Monitor(new Policy { Name = "sap-reporting-p95", Application = "finance-reports", Environment = "production", Type = "latency",
                Target = "sap-reporting", ThresholdMs = 900, WindowMinutes = 60, Severity = "medium" },
                "Alert", "configuration", 0, observed: 1250, baseline: 610, samples: 340, incident: IncidentId),
            Monitor(new Policy { Name = "payments-heartbeat", Application = "payments-gateway", Environment = "production", Type = "no-data",
                WindowMinutes = 15, NoDataMinutes = 30 }, "InsufficientData", "administration", 3, observed: null, baseline: null, samples: 0),
            Monitor(new Policy { Name = "reporting-release", Application = "finance-reports", Environment = "production", Type = "regression",
                Target = "sap-reporting", FailureIncreasePercent = 5, LatencyIncreaseRatio = 1.5 }, "OK", "configuration", 0,
                observed: 2.5, baseline: 2.1, samples: 812),
            Monitor(new Policy { Name = "render-p99", Application = "finance-reports", Environment = "production", Type = "latency",
                Percentile = 99, ThresholdMs = 2500, Enabled = false }, "Disabled", "configuration", 0, observed: null, baseline: null,
                samples: null, evaluated: false)
        }));
        await using var context = CreateContext(api);

        var page = context.Render<Monitors>(parameters => parameters.AddCascadingValue(Operator()));
        page.WaitForAssertion(() => Assert.That(page.FindAll("table.monitor-table tbody tr"), Has.Count.EqualTo(4)), Wait);

        var rows = page.FindAll("table.monitor-table tbody tr");
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].QuerySelector(".badge-danger")?.TextContent.Trim(), Is.EqualTo("Alert"));
            Assert.That(rows[1].QuerySelector(".badge-warning")?.TextContent.Trim(), Is.EqualTo("Insufficient data"));
            Assert.That(rows[2].QuerySelector(".badge-good")?.TextContent.Trim(), Is.EqualTo("OK"));
            Assert.That(rows[3].QuerySelector(".badge-neutral")?.TextContent.Trim(), Is.EqualTo("Disabled"));
            Assert.That(rows[0].TextContent, Does.Contain("p95 >= 900 ms").And.Contain("1.25 s").And.Contain("610 ms").And.Contain("340"));
            Assert.That(rows[1].TextContent, Does.Contain("silent >= 30 min").And.Contain("All API requests").And.Contain("Change control")
                .And.Contain("Revision 3"));
            Assert.That(rows[2].TextContent, Does.Contain("+5 pts or x1.5 p95").And.Contain("2.5%"));
            Assert.That(rows[3].TextContent, Does.Contain("p99 >= 2.50 s").And.Contain("Not yet evaluated"));
            Assert.That(rows[0].QuerySelector("a.record-link")?.GetAttribute("href"),
                Is.EqualTo($"incidents/{IncidentId}?application=finance-reports&environment=production&hours=24"));
            Assert.That(rows[1].QuerySelector("a.record-link"), Is.Null);
            Assert.That(page.Find(".stat-grid").TextContent, Does.Contain("Alerting"));
        });
        Assert.That(api.Requests.Select(request => request.Target), Is.EqualTo(new[] { "/api/v1/monitors" }));
    }

    [Test]
    public async Task MonitorsPageExplainsWhereMonitorsComeFromWhenNoneExist()
    {
        var api = new StubApi().On(HttpMethod.Get, "/api/v1/monitors", "[]");
        await using var context = CreateContext(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("monitors?application=finance-reports");

        var page = context.Render<Monitors>(parameters => parameters.AddCascadingValue(Operator()));

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Contain("No monitors in this scope")), Wait);
        Assert.That(page.Markup, Does.Contain("PulseOps:Monitors").And.Contain("Change control"));
        Assert.That(api.Requests.Single().Target, Is.EqualTo("/api/v1/monitors?application=finance-reports"));
    }

    [Test]
    public async Task ChangeControlOffersApprovalOnlyWhereAllowedAndFlagsDevelopmentSelfApproval()
    {
        var api = ChangeControlApi();
        await using var context = CreateContext(api);

        var page = context.Render<Administration>(parameters => parameters.AddCascadingValue(Operator(selfApproval: true)));
        page.WaitForAssertion(() => Assert.That(page.FindAll("article.change-card"), Has.Count.EqualTo(2)), Wait);

        var cards = page.FindAll("article.change-card");
        var approvable = cards.Single(card => card.TextContent.Contains("Create monitor checkout-p95"));
        var withdrawable = cards.Single(card => card.TextContent.Contains("Schedule maintenance"));
        Assert.Multiple(() =>
        {
            Assert.That(page.Find(".notice[role=note]").TextContent, Does.Contain("Development self-approval is on"));
            Assert.That(Buttons(approvable), Is.EquivalentTo(new[] { "Approve", "Reject" }));
            Assert.That(Buttons(withdrawable), Is.EquivalentTo(new[] { "Withdraw" }));
            Assert.That(page.FindAll("button").Count(button => button.TextContent.Trim().StartsWith("Approve", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(approvable.TextContent, Does.Contain("Latency · All API requests · p95 >= 900 ms").And.Contain("Finance Reporting · Production"));
            Assert.That(withdrawable.TextContent, Does.Contain("Suppress notifications from").And.Contain("UTC"));
            Assert.That(page.Find(".chain-status").TextContent, Does.Contain("Verified"));
            Assert.That(page.Find("a[href='api/v1/admin/audit/export?days=30']").TextContent, Does.Contain("Export NDJSON"));
        });
    }

    [Test]
    public async Task ChangeControlHidesTheSelfApprovalBannerOutsideDevelopment()
    {
        var api = ChangeControlApi();
        await using var context = CreateContext(api);

        var page = context.Render<Administration>(parameters => parameters.AddCascadingValue(Operator()));
        page.WaitForAssertion(() => Assert.That(page.FindAll("article.change-card"), Has.Count.EqualTo(2)), Wait);

        Assert.That(page.FindAll(".notice[role=note]"), Is.Empty);
    }

    [Test]
    public async Task ApprovingSendsTheCsrfHeaderAndDecisionNote()
    {
        var api = ChangeControlApi().On(HttpMethod.Post, $"/api/v1/admin/changes/{ApprovableId}/approve", "", HttpStatusCode.NoContent);
        await using var context = CreateContext(api);
        var page = context.Render<Administration>(parameters => parameters.AddCascadingValue(Operator()));
        page.WaitForAssertion(() => Assert.That(page.FindAll("article.change-card"), Has.Count.EqualTo(2)), Wait);

        var card = page.FindAll("article.change-card").Single(item => item.TextContent.Contains("checkout-p95"));
        card.QuerySelector("textarea")!.Input("Checked against the checkout SLO");
        page.FindAll("article.change-card").Single(item => item.TextContent.Contains("checkout-p95"))
            .QuerySelectorAll("button").Single(button => button.TextContent.Trim().StartsWith("Approve", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.That(page.Find(".inline-notice[role=status]").TextContent, Does.Contain("Approved: Create monitor checkout-p95")), Wait);
        var decision = api.Requests.Single(request => request.Method == HttpMethod.Post);
        Assert.Multiple(() =>
        {
            Assert.That(decision.Target, Is.EqualTo($"/api/v1/admin/changes/{ApprovableId}/approve"));
            Assert.That(decision.Csrf, Is.EqualTo(Csrf));
            Assert.That(decision.Body, Is.EqualTo("{\"note\":\"Checked against the checkout SLO\"}"));
        });
    }

    [Test]
    public async Task RefusedSelfApprovalExplainsThatASecondAdministratorIsRequired()
    {
        var api = ChangeControlApi().On(HttpMethod.Post, $"/api/v1/admin/changes/{ApprovableId}/approve",
            "{\"error\":\"self_approval\"}", HttpStatusCode.Forbidden);
        await using var context = CreateContext(api);
        var page = context.Render<Administration>(parameters => parameters.AddCascadingValue(Operator()));
        page.WaitForAssertion(() => Assert.That(page.FindAll("article.change-card"), Has.Count.EqualTo(2)), Wait);

        page.FindAll("button").Single(button => button.TextContent.Trim().StartsWith("Approve", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.That(page.Find("article.change-card [role=alert]").TextContent,
            Is.EqualTo("A second administrator must approve this change.")), Wait);
    }

    [Test]
    public async Task ChangeControlExplainsAccessWithoutCallingGovernanceEndpoints()
    {
        var api = new StubApi();
        await using var context = CreateContext(api);

        var page = context.Render<Administration>(parameters => parameters.AddCascadingValue(
            Operator(administer: false, propose: false, audit: false) with { Role = "ProductionSupport" }));

        Assert.That(page.Markup, Does.Contain("Change control is not available for your role"));
        Assert.That(page.FindAll("form"), Is.Empty);
        Assert.That(api.Requests, Is.Empty);
    }

    [TestCase("0")]
    [TestCase("86400001")]
    public async Task ProposalFormBlocksAnOutOfRangeThresholdWithoutCallingTheApi(string threshold)
    {
        var api = ChangeControlApi();
        await using var context = CreateContext(api);
        var page = context.Render<Administration>(parameters => parameters.AddCascadingValue(Operator()));
        page.WaitForElement("#monitor-threshold", Wait);

        page.Find("#monitor-name").Change("checkout-p95");
        page.Find("#monitor-threshold").Change(threshold);
        page.Find("#monitor-reason").Input("Checkout latency budget");
        page.Find("form.proposal-form").Submit();

        Assert.That(page.Find("form.proposal-form [role=alert]").TextContent,
            Is.EqualTo("Latency monitors require a supported percentile and a threshold between 1 ms and 24 hours."));
        Assert.That(api.Requests.Where(request => request.Method == HttpMethod.Post), Is.Empty);
    }

    [Test]
    public async Task ProposalFormSubmitsAPolicyTheServerContractAccepts()
    {
        var api = ChangeControlApi().On(HttpMethod.Post, "/api/v1/admin/changes", "{\"id\":\"7d1fb7b2-61c4-4a8e-9a3f-0d6f1c2b9e44\"}",
            HttpStatusCode.Created);
        await using var context = CreateContext(api);
        var page = context.Render<Administration>(parameters => parameters.AddCascadingValue(Operator()));
        page.WaitForElement("#monitor-threshold", Wait);

        page.Find("#monitor-name").Change("checkout-p95");
        page.Find("#monitor-target").Change("sap-reporting");
        page.Find("#monitor-threshold").Change("1200");
        page.Find("#monitor-baseline-days").Change("7");
        page.Find("#monitor-reason").Input("Checkout latency budget agreed with the service owner");
        page.Find("form.proposal-form").Submit();

        page.WaitForAssertion(() => Assert.That(page.Find("form.proposal-form [role=status]").TextContent,
            Does.Contain("Proposal submitted for review as change 7d1fb7b2…9e44")), Wait);
        var post = api.Requests.Single(request => request.Method == HttpMethod.Post);
        Assert.That(post.Csrf, Is.EqualTo(Csrf));
        // The server binds with web defaults and rejects unmapped members, so this proves the portal shape mirrors the contract.
        var proposal = JsonSerializer.Deserialize<PulseOps.Server.ChangeProposal>(post.Body!, JsonSerializerOptions.Web)!;
        var scopes = new[] { new PulseOps.Server.AppScope { Application = "finance-reports", Environment = "production",
            Label = "Finance Reporting", AllowedNames = ["sap-reporting"] } };
        Assert.Multiple(() =>
        {
            Assert.That((proposal.Entity, proposal.Operation, proposal.Reason),
                Is.EqualTo(("monitor", "upsert", "Checkout latency budget agreed with the service owner")));
            Assert.That(proposal.Monitor, Is.Not.Null);
            Assert.That(proposal.Monitor!.Validate(scopes), Is.Null);
            Assert.That((proposal.Monitor.Name, proposal.Monitor.Target, proposal.Monitor.ThresholdMs, proposal.Monitor.BaselineDays),
                Is.EqualTo(("checkout-p95", "sap-reporting", 1200d, 7)));
            Assert.That(proposal.Maintenance, Is.Null);
        });
    }

    [Test]
    public async Task MaintenanceProposalReadsTheEnteredTimesAsUtc()
    {
        var api = ChangeControlApi().On(HttpMethod.Post, "/api/v1/admin/changes", "{\"id\":\"9e3c1f7a-2b44-4d0e-8a51-6c7f0b2d4e18\"}",
            HttpStatusCode.Created);
        await using var context = CreateContext(api);
        var page = context.Render<Administration>(parameters => parameters.AddCascadingValue(Operator()));
        page.WaitForElement("#maintenance-starts", Wait);
        var start = DateTimeOffset.UtcNow.Date.AddDays(10).AddHours(22).AddMinutes(30);

        page.Find("#maintenance-scope").Change("payments-gateway|production");
        page.Find("#maintenance-starts").Change(start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
        page.Find("#maintenance-ends").Change(start.AddHours(3).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
        page.Find("#maintenance-reason").Input("Gateway certificate rotation");
        page.FindAll("form").Single(form => form.QuerySelector("#maintenance-starts") is not null).Submit();

        page.WaitForAssertion(() => Assert.That(api.Requests.Count(request => request.Method == HttpMethod.Post), Is.EqualTo(1)), Wait);
        var proposal = JsonSerializer.Deserialize<PulseOps.Server.ChangeProposal>(
            api.Requests.Single(request => request.Method == HttpMethod.Post).Body!, JsonSerializerOptions.Web)!;
        Assert.Multiple(() =>
        {
            Assert.That((proposal.Entity, proposal.Operation), Is.EqualTo(("maintenance", "create")));
            Assert.That(proposal.Maintenance!.Application, Is.EqualTo("payments-gateway"));
            Assert.That(proposal.Maintenance.StartsAt, Is.EqualTo(new DateTimeOffset(start, TimeSpan.Zero)));
            Assert.That(proposal.Maintenance.StartsAt.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(proposal.Maintenance.EndsAt - proposal.Maintenance.StartsAt, Is.EqualTo(TimeSpan.FromHours(3)));
            Assert.That(MaintenanceRules.ParseUtc("2026-11-22T10:30"), Is.EqualTo(new DateTimeOffset(2026, 11, 22, 10, 30, 0, TimeSpan.Zero)));
        });
    }

    [Test]
    public async Task HistoryShowsApproximatePercentilesResolutionAndSeriesLinks()
    {
        var start = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var api = new StubApi().On(HttpMethod.Get, "/api/v1/history", Json(new
        {
            resolution = "hour", stepMs = 3_600_000, from = start, until = start.AddHours(2), approximatePercentiles = true,
            points = new[]
            {
                Point(start, 120, 6, 410, 880, 1500),
                Point(start.AddHours(1), 0, 0, null, null, null),
                Point(start.AddHours(2), 95, 2, 380, 790, 1320)
            },
            series = new[] { new { application = "finance-reports", environment = "production", kind = "api", name = "sap-reporting",
                total = 215, failures = 8, p95 = 840.0 } }
        }));
        await using var context = CreateContext(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("history?days=7&kind=api");

        var page = context.Render<History>(parameters => parameters.AddCascadingValue(Operator()));

        page.WaitForAssertion(() => Assert.That(page.FindAll("svg[role=img]"), Has.Count.EqualTo(2)), Wait);
        Assert.Multiple(() =>
        {
            Assert.That(page.Find(".panel-note").TextContent,
                Is.EqualTo("Percentiles are approximate (about +/-2%) because they come from mergeable rollup histograms."));
            Assert.That(page.Markup, Does.Contain("Hourly buckets from 15 Nov 2026, 09:00:00 UTC"));
            Assert.That(page.Find(".stat-grid").TextContent, Does.Contain("215").And.Contain("3.7 %").And.Contain("880 ms"));
            Assert.That(page.Find("table.data-table tbody a.record-link").GetAttribute("href"),
                Is.EqualTo("history?application=finance-reports&environment=production&hours=24&days=7&kind=api&name=sap-reporting"));
            Assert.That(api.Requests.Single().Target, Is.EqualTo("/api/v1/history?kind=api&days=7&resolution=auto"));
        });
    }

    [Test]
    public async Task HistoryShowsAnEmptyStateWithoutRollups()
    {
        var start = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var api = new StubApi().On(HttpMethod.Get, "/api/v1/history", Json(new
        {
            resolution = "day", stepMs = 86_400_000, from = start, until = start.AddDays(30), approximatePercentiles = true,
            points = Enumerable.Range(0, 31).Select(day => Point(start.AddDays(day), 0, 0, null, null, null)).ToArray(),
            series = Array.Empty<object>()
        }));
        await using var context = CreateContext(api);

        var page = context.Render<History>(parameters => parameters.AddCascadingValue(Operator()));

        page.WaitForAssertion(() => Assert.That(page.Markup, Does.Contain("No rollup history for this selection")), Wait);
        Assert.That(page.Markup, Does.Contain("Daily buckets"));
        Assert.That(page.FindAll(".panel-note"), Is.Empty);
        Assert.That(page.FindAll("svg[role=img]"), Is.Empty);
        Assert.That(api.Requests.Single().Target, Is.EqualTo("/api/v1/history?kind=api&days=30&resolution=auto"));
    }

    [Test]
    public async Task HistoryReportsAnUnsupportedRangeThroughTheErrorState()
    {
        var api = new StubApi().On(HttpMethod.Get, "/api/v1/history", "{\"error\":\"range_exceeds_hourly_retention\"}", HttpStatusCode.BadRequest);
        await using var context = CreateContext(api);

        var page = context.Render<History>(parameters => parameters.AddCascadingValue(Operator()));

        page.WaitForAssertion(() => Assert.That(page.Find(".state-error[role=alert]").TextContent,
            Does.Contain("Hourly rollups are not kept for the selected range.")), Wait);
    }

    [TestCase(HttpStatusCode.Conflict, "{\"error\":\"stale\"}", "The monitor changed after this proposal. Propose the change again.")]
    [TestCase(HttpStatusCode.Conflict, "{\"error\":\"configuration_managed\"}", "This monitor is managed in configuration and cannot be changed here.")]
    [TestCase(HttpStatusCode.Conflict, "{\"error\":\"not_pending\"}", "This change is no longer pending. It was decided, withdrawn or expired. Refresh to see its status.")]
    [TestCase(HttpStatusCode.Forbidden, "{\"error\":\"self_approval\"}", "A second administrator must approve this change.")]
    [TestCase(HttpStatusCode.BadRequest, "{\"error\":\"invalid_change\",\"detail\":\"Monitor name must be a 1-64 character alias.\"}",
        "Monitor name must be a 1-64 character alias.")]
    [TestCase(HttpStatusCode.BadRequest, "", "PulseOps could not accept the request. Reload the page and try again.")]
    public async Task ChangeErrorsBecomeOperatorMessages(HttpStatusCode status, string body, string expected)
    {
        var api = new StubApi().On(HttpMethod.Post, $"/api/v1/admin/changes/{ApprovableId}/approve", body, status);
        using var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };

        var failure = Assert.ThrowsAsync<PortalRequestException>(() =>
            new PortalApi(http).DecideChangeAsync(ApprovableId, true, null, Csrf, CancellationToken.None));

        Assert.That(failure!.Message, Is.EqualTo(expected));
        Assert.That(failure.Status, Is.EqualTo(status));
        Assert.That(api.Requests.Single().Body, Is.EqualTo("{}"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task BrokenAuditChainIsAnnouncedWithItsSequence()
    {
        await using var context = new BunitContext();
        var result = new AuditResult(new AuditChain(false, 57, "9f2c4e1a7b3d5f60", 42),
            [new AuditEntry(57, Evaluated, "local-demo", "change.approved", "", "", "monitor:checkout-p95", "Approved", "aa11", "bb22")]);

        var log = context.Render<AuditLog>(parameters => parameters
            .AddCascadingValue(Operator())
            .Add(p => p.Result, result)
            .Add(p => p.Offset, 0)
            .Add(p => p.Link, offset => "administration?offset=" + offset));

        Assert.That(log.Find(".chain-status[role=alert] .badge-danger").TextContent.Trim(), Is.EqualTo("Broken at seq 42"));
        Assert.That(log.Find("table tbody tr").TextContent, Does.Contain("Platform").And.Contain("change.approved"));
    }

    private static StubApi ChangeControlApi()
    {
        var created = new DateTimeOffset(2026, 11, 22, 8, 15, 0, TimeSpan.Zero);
        var windowStart = DateTimeOffset.UtcNow.AddDays(2);
        var monitorPayload = new Policy { Name = "checkout-p95", Application = "finance-reports", Environment = "production",
            Type = "latency", ThresholdMs = 900 };
        var pending = new object[]
        {
            Change(ApprovableId, "monitor", "upsert", "finance-reports", "checkout-p95", monitorPayload, created,
                canApprove: true, canWithdraw: false),
            Change(WithdrawableId, "maintenance", "create", "payments-gateway", "",
                new { application = "payments-gateway", environment = "production", startsAt = windowStart, endsAt = windowStart.AddHours(2),
                    reason = "Gateway patching" }, created, canApprove: false, canWithdraw: true)
        };
        return new StubApi()
            .On(HttpMethod.Get, "/api/v1/admin/changes?status=pending", Json(pending))
            .On(HttpMethod.Get, "/api/v1/admin/changes?status=all", "[]")
            .On(HttpMethod.Get, "/api/v1/admin/maintenance", "[]")
            .On(HttpMethod.Get, "/api/v1/monitors", "[]")
            .On(HttpMethod.Get, "/api/v1/admin/audit",
                "{\"chain\":{\"verified\":true,\"entries\":0,\"headHash\":\"\",\"brokenAtSeq\":null},\"entries\":[]}");
    }

    private static object Change(string id, string entity, string operation, string application, string target, object payload,
        DateTimeOffset created, bool canApprove, bool canWithdraw) => new
    {
        id, createdAt = created, proposer = "owner-subject-17", entity, operation, application, environment = "production", target,
        payload, reason = "Agreed in the weekly service review", baseRevision = (long?)null, status = "Pending",
        decidedAt = (DateTimeOffset?)null, decider = (string?)null, decisionNote = (string?)null, canApprove, canWithdraw
    };

    private static object Monitor(Policy definition, string state, string source, long revision, double? observed, double? baseline,
        long? samples, string? incident = null, bool evaluated = true) => new
    {
        application = definition.Application, environment = definition.Environment, name = definition.Name, type = definition.Type,
        kind = definition.Kind, target = definition.Target, severity = definition.Severity, enabled = definition.Enabled, source, revision,
        state, since = evaluated ? Evaluated.AddMinutes(-40) : (DateTimeOffset?)null, evaluatedAt = evaluated ? Evaluated : (DateTimeOffset?)null,
        observed, baseline, samples, detail = $"{state} at the last evaluation", incidentId = incident, definition
    };

    private static object Point(DateTimeOffset at, long total, long failures, double? p50, double? p95, double? p99) => new
    {
        at, total, failures, degraded = 0, cancelled = 0, p50, p95, p99,
        averageMs = total == 0 ? (double?)null : p50, maxMs = total == 0 ? (double?)null : p99 * 1.4
    };

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static string[] Buttons(AngleSharp.Dom.IElement card) => card.QuerySelectorAll("button")
        .Select(button => button.TextContent.Trim().Split(' ')[0]).ToArray();

    private static PortalContext Operator(bool administer = true, bool propose = true, bool audit = true, bool selfApproval = false) =>
        new("Local operator", administer ? "PlatformAdministrator" : propose ? "ApplicationOwner" : "ReadOnlyAuditor", true, true,
            Scopes, Csrf, administer, propose, audit, selfApproval);

    private static BunitContext CreateContext(StubApi api)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped(_ => new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") });
        context.Services.AddScoped<PortalApi>();
        context.Services.AddScoped<LiveUpdates>();
        return context;
    }

    private sealed record RecordedRequest(HttpMethod Method, string Target, string? Csrf, string? Body);

    /// <summary>Answers by method and path (or path-and-query prefix); the longest registered route wins, later ones override.</summary>
    private sealed class StubApi : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Path, HttpStatusCode Status, string Body)> _routes = [];
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public StubApi On(HttpMethod method, string path, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((method, path, status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var target = request.RequestUri!.PathAndQuery;
            var csrf = request.Headers.TryGetValues("X-PulseOps-CSRF", out var values) ? values.Single() : null;
            lock (_requests)
                _requests.Add(new(request.Method, target, csrf, body));

            var route = _routes.Select((item, index) => (item, index))
                .Where(entry => entry.item.Method == request.Method && (target == entry.item.Path ||
                    target.StartsWith(entry.item.Path + (entry.item.Path.Contains('?') ? "" : "?"), StringComparison.Ordinal)))
                .OrderByDescending(entry => entry.item.Path.Length).ThenByDescending(entry => entry.index)
                .Select(entry => ((HttpMethod, string, HttpStatusCode, string)?)entry.item).FirstOrDefault();
            if (route is not { } match)
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
            return new HttpResponseMessage(match.Item3) { Content = new StringContent(match.Item4, Encoding.UTF8, "application/json") };
        }
    }
}
