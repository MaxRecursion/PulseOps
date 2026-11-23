using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using PulseOps.Server;

var arguments = CommandLine.Parse(args);
var useExecutableDirectory = OperatingSystem.IsWindows() &&
    Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = arguments.Remaining,
    ContentRootPath = useExecutableDirectory ? AppContext.BaseDirectory : null
});
builder.Host.UseWindowsService();
if (arguments.Config is not null)
{
    builder.Configuration.AddJsonFile(arguments.Config, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(arguments.Remaining);
}

var development = builder.Environment.IsDevelopment();
var options = builder.Configuration.GetSection("PulseOps").Get<PlatformOptions>() ?? new();
options.Validate(development, builder.Environment.ContentRootPath, arguments.Mode != "web");
var storage = new Storage(options);
if (arguments.Mode is "migrate" or "backup" or "restore" or "verify-backup" or "export-audit" or "replay-dead-letters")
{
    switch (arguments.Mode)
    {
        case "migrate": storage.Migrate(); break;
        case "backup": storage.Backup(arguments.Path!); break;
        case "restore": storage.Restore(arguments.Path!); break;
        case "verify-backup": Storage.VerifyBackup(arguments.Path!, requireCurrentSchema: true); break;
        case "export-audit":
            storage.AssertReady();
            var (exported, head) = storage.ExportAudit(arguments.Path!);
            Console.WriteLine($"Exported {exported} verified audit entries. Chain head {head}.");
            break;
        case "replay-dead-letters":
            storage.AssertReady();
            var replay = storage.ReplayDeadLetters();
            Console.WriteLine($"Returned {replay.Returned} dead-letter records to the inbox. " +
                $"Skipped {replay.Duplicates} duplicates and deferred {replay.Deferred} for inbox capacity. " +
                $"{replay.Remaining} records remain.");
            if (replay.PausedForDisk)
                Console.WriteLine("Replay paused because the configured free-space reserve is not available.");
            break;
    }
    Console.WriteLine($"PulseOps {arguments.Mode} completed.");
    return;
}

if (development && arguments.Mode == "web")
    storage.Migrate();
else
    storage.AssertReady();

if (arguments.Mode == "worker")
{
    var worker = Host.CreateApplicationBuilder(arguments.Remaining);
    worker.Configuration.AddConfiguration(builder.Configuration);
    worker.Services.AddWindowsService();
    ConfigureProcessing(worker.Services, options, storage, run: true);
    using var host = worker.Build();
    await host.RunAsync();
    return;
}

ConfigureProcessing(builder.Services, options, storage, options.EmbeddedWorker);

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 131_072;
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = 131_072);
builder.Services.AddProblemDetails();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 8192);
var protection = builder.Services.AddDataProtection().SetApplicationName("PulseOps");
if (options.DataProtectionPath.Length > 0)
{
    protection.PersistKeysToFileSystem(Directory.CreateDirectory(options.DataProtectionPath));
    if (OperatingSystem.IsWindows())
        protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddAntiforgery(a =>
{
    a.HeaderName = "X-PulseOps-CSRF";
    a.Cookie.Name = development ? "PulseOps.Csrf" : "__Host-PulseOps.Csrf";
    a.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
var identity = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = development ? "PulseOps.Session" : "__Host-PulseOps.Session";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
        cookie.SlidingExpiration = false;
        cookie.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        cookie.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
var oidc = options.Auth.Authority.Length > 0;
if (oidc)
{
    identity.AddOpenIdConnect("oidc", auth =>
    {
        auth.Authority = options.Auth.Authority;
        auth.ClientId = options.Auth.ClientId;
        auth.ClientSecret = options.Auth.ClientSecret;
        auth.ResponseType = "code";
        auth.UsePkce = true;
        auth.MapInboundClaims = false;
        auth.SaveTokens = false;
        auth.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = options.Auth.RoleClaim
        };
    });
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("operator", p => p.RequireAuthenticatedUser().RequireRole(Access.Roles))
    .AddPolicy("administrator", p => p.RequireAuthenticatedUser().RequireRole("PlatformAdministrator"));
builder.Services.AddCors(cors => cors.AddPolicy("startup", policy =>
{
    policy.WithOrigins(options.Scopes.SelectMany(s => s.StartupOrigins).Distinct().ToArray())
        .WithMethods("POST").WithHeaders("Content-Type").WithExposedHeaders("Retry-After")
        .SetPreflightMaxAge(TimeSpan.FromMinutes(5));
}));
builder.Services.AddRateLimiter(rate =>
{
    rate.RejectionStatusCode = 429;
    rate.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "5";
        return ValueTask.CompletedTask;
    };
    rate.AddPolicy("startup", context => Limit(context, 10));
    rate.AddPolicy("ingest", context => Limit(context, 1800));
    rate.AddPolicy("query", context => RateLimitPartition.GetConcurrencyLimiter("queries", _ =>
        new ConcurrencyLimiterOptions { PermitLimit = 6, QueueLimit = 0 }));

    static RateLimitPartition<string> Limit(HttpContext context, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
            _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = perMinute, QueueLimit = 0 });
});

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/auth"))
    {
        // The exact values antiforgery requires, so issuing a CSRF token does not log a header override on every load.
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        ctx.Response.Headers.Pragma = "no-cache";
    }
    if (!development && !ctx.Request.IsHttps && !ctx.Request.Path.StartsWithSegments("/health"))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (development && options.Demo && !System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? System.Net.IPAddress.None))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
if (!development)
    app.UseHsts();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
if (development && options.Demo)
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new(ClaimTypes.Name, "Local operator"), new(ClaimTypes.NameIdentifier, "local-demo"),
                 new(ClaimTypes.Role, "PlatformAdministrator")], "local-demo"));
        await next();
    });
    DemoData.Seed(storage, options, TimeProvider.System.GetUtcNow());
}
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (Storage db, TimeProvider clock) =>
{
    try
    {
        if (db.IsReady(clock.GetUtcNow()))
            return Results.Ok(new { status = "ready" });
    }
    catch (SqliteException) { }
    catch (InvalidOperationException) { }
    catch (IOException) { }
    return Results.Json(new { status = "unready" }, statusCode: 503);
});
app.MapGet("/auth/login", () => oidc ? Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/" }, ["oidc"]) : Results.Redirect("/"));
app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery csrf) =>
{
    if (!await csrf.IsRequestValidAsync(context))
        return Results.BadRequest();
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
}).RequireAuthorization("operator");

app.MapPost("/api/telemetry/v1/batch", (TelemetryBatch batch, HttpContext context, PlatformOptions settings, Storage db, TimeProvider clock) =>
    Endpoints.Receive(batch, context, settings, db, clock, false)).RequireRateLimiting("ingest");
app.MapPost("/api/telemetry/v1/startup", (TelemetryBatch batch, HttpContext context, PlatformOptions settings, Storage db, TimeProvider clock) =>
    Endpoints.Receive(batch, context, settings, db, clock, true)).RequireCors("startup").RequireRateLimiting("startup");
app.MapPost("/api/telemetry/v1/releases", Endpoints.Release).RequireRateLimiting("ingest");

var api = app.MapGroup("/api/v1").RequireAuthorization("operator").RequireRateLimiting("query");
api.MapGet("/context", (HttpContext ctx, IAntiforgery csrf) => new
{
    name = ctx.User.Identity?.Name ?? "Operator",
    role = Access.Roles.FirstOrDefault(ctx.User.IsInRole),
    canManage = Access.CanManage(ctx.User),
    canAdminister = Access.CanAdminister(ctx.User),
    canPropose = Access.CanPropose(ctx.User),
    canAudit = Access.CanAudit(ctx.User),
    selfApproval = options.Administration.AllowSelfApproval,
    development = options.Demo,
    scopes = Access.Visible(ctx.User, options).Select(s => new { s.Application, s.Environment, s.Label, s.Demo }),
    csrfToken = csrf.GetAndStoreTokens(ctx).RequestToken
});
api.MapGet("/overview", (HttpContext ctx, TimeProvider clock) => storage.Overview(Filter(ctx, clock)));
api.MapGet("/dependencies", (HttpContext ctx, TimeProvider clock) => storage.Dependencies(Filter(ctx, clock)));
api.MapGet("/events", (HttpContext ctx, TimeProvider clock) =>
    EventSearch.TryCompile(ctx.Request.Query["q"].ToString(), out var search)
        ? Results.Ok(storage.Events(Filter(ctx, clock), search))
        : Results.BadRequest(new { error = "invalid_search" }));
api.MapGet("/sessions", (HttpContext ctx, TimeProvider clock) => storage.Sessions(Filter(ctx, clock)));
api.MapGet("/releases", (HttpContext ctx, TimeProvider clock) => storage.Releases(Filter(ctx, clock)));
api.MapGet("/incidents", (HttpContext ctx, TimeProvider clock) => storage.Incidents(Filter(ctx, clock)));
api.MapGet("/incidents/{id:guid}", (Guid id, HttpContext ctx, TimeProvider clock) =>
    storage.IncidentDetails(Filter(ctx, clock), id.ToString("D")) is { } incident ? Results.Ok(incident) : Results.NotFound());
api.MapPost("/incidents/{id:guid}", async (Guid id, IncidentChange change, HttpContext ctx, IAntiforgery csrf,
    TimeProvider clock, IHubContext<LiveHub> live) =>
{
    if (!Access.CanManage(ctx.User))
        return Results.Forbid();
    if (!await csrf.IsRequestValidAsync(ctx) || change.Note is { Length: > 500 } ||
        change.Assignee is { Length: > 80 } || change.Status is { Length: > 20 })
        return Results.BadRequest();
    int status = storage.ChangeIncident(Filter(ctx, clock), id.ToString("D"), change, Access.Actor(ctx.User), clock.GetUtcNow());
    if (status == 204)
        await live.Clients.Group("platform").SendAsync("Changed", ctx.RequestAborted);
    return Results.StatusCode(status);
});
api.MapGet("/platform", (Storage database, TimeProvider clock) => database.PlatformHealth(clock.GetUtcNow()))
    .RequireAuthorization("administrator");
RollupEndpoints.Map(api, options);
MonitorEndpoints.Map(api, options);
AdministrationEndpoints.Map(api, options);
app.MapHub<LiveHub>("/hubs/live", hub => hub.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization("operator");

app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await app.RunAsync();

QueryFilter Filter(HttpContext context, TimeProvider clock) =>
    QueryFilter.From(context.Request, Access.Visible(context.User, options), clock.GetUtcNow());

static void ConfigureProcessing(IServiceCollection services, PlatformOptions settings, Storage store, bool run)
{
    services.AddSingleton(settings);
    services.AddSingleton(store);
    services.AddSingleton(TimeProvider.System);
    if (!run)
        return;
    services.AddHostedService<Processor>();
    services.AddHostedService<MonitorWorker>();
    if (settings.Probes.Count > 0)
        services.AddHostedService<SyntheticChecks>();
    if (settings.Webhooks.Count > 0 || settings.EmailRoutes.Count > 0)
        services.AddHostedService<NotificationWorker>();
    services.AddHttpClient("outbound", client => client.Timeout = TimeSpan.FromSeconds(10)).RemoveAllLoggers()
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });
}

public partial class Program { }
