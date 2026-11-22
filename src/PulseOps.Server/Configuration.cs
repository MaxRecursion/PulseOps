using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace PulseOps.Server;

public sealed class PlatformOptions
{
    public string DatabasePath { get; set; } = "data/pulseops.db";
    public string DataProtectionPath { get; set; } = "";
    public bool EmbeddedWorker { get; set; } = true;
    public bool Demo { get; set; }
    public int RetentionDays { get; set; } = 14;
    private int? _errorRetentionDays;
    /// <summary>Defaults to 30 days, bounded by the configured raw and incident retention windows.</summary>
    public int ErrorRetentionDays
    {
        get => _errorRetentionDays ?? Math.Max(RetentionDays, Math.Min(30, IncidentRetentionDays));
        set => _errorRetentionDays = value;
    }
    public int IncidentRetentionDays { get; set; } = 180;
    public int InboxMaximum { get; set; } = 100_000;
    public int MinimumFreeMegabytes { get; set; } = 512;
    public AuthSettings Auth { get; set; } = new();
    public List<AppScope> Scopes { get; set; } = [];
    public List<DetectionRule> Rules { get; set; } = [];
    public List<ProbeSettings> Probes { get; set; } = [];
    public List<WebhookSettings> Webhooks { get; set; } = [];
    public string PortalOrigin { get; set; } = "";
    /// <summary>Hourly rollups (UTC, by event time) are kept this many days.</summary>
    public int RollupHourlyDays { get; set; } = 32;
    /// <summary>Daily rollups are kept this many days. Raw telemetry follows RetentionDays.</summary>
    public int RollupRetentionDays { get; set; } = 180;
    public int MonitorIntervalSeconds { get; set; } = 60;
    public List<MonitorPolicy> Monitors { get; set; } = [];
    public AdministrationSettings Administration { get; set; } = new();
    public SmtpSettings Smtp { get; set; } = new();
    public List<EmailRoute> EmailRoutes { get; set; } = [];

    public void Validate(bool development, string contentRoot, bool databaseOnly)
    {
        if (!development && !Path.IsPathFullyQualified(DatabasePath))
            throw new InvalidOperationException("PulseOps:DatabasePath must be absolute.");
        if (DatabasePath.StartsWith(@"\\", StringComparison.Ordinal) || DatabasePath.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("SQLite requires a local volume.");
        DatabasePath = Path.GetFullPath(DatabasePath, contentRoot);
        if (Demo && !development)
            throw new InvalidOperationException("Demo is available only in Development.");
        if (RetentionDays is < 1 or > 90 || IncidentRetentionDays < RetentionDays ||
            IncidentRetentionDays > 3650 || InboxMaximum is < 100 or > 1_000_000 ||
            MinimumFreeMegabytes is < 1 or > 10_240)
            throw new InvalidOperationException("Invalid storage retention, disk reserve or inbox limit.");
        if (ErrorRetentionDays < RetentionDays || ErrorRetentionDays > 90 || IncidentRetentionDays < ErrorRetentionDays)
            throw new InvalidOperationException("ErrorRetentionDays must be between RetentionDays and 90 and not exceed IncidentRetentionDays.");
        if (RollupHourlyDays is < 2 or > 90 || RollupRetentionDays < RollupHourlyDays || RollupRetentionDays > 3650 ||
            MonitorIntervalSeconds is < 15 or > 600)
            throw new InvalidOperationException("Invalid rollup retention or monitor evaluation interval.");
        if (Administration.AllowSelfApproval && !development)
            throw new InvalidOperationException("Administration self-approval is available only in Development.");
        if (Administration.ChangeExpiryHours is < 1 or > 720 || Administration.MaximumPendingChanges is < 1 or > 500)
            throw new InvalidOperationException("Invalid administration change expiry or pending change limit.");

        if (!databaseOnly && !development &&
            (!Validation.IsHttps(Auth.Authority) || string.IsNullOrWhiteSpace(Auth.ClientId) ||
             string.IsNullOrWhiteSpace(Auth.ClientSecret) || !Path.IsPathFullyQualified(DataProtectionPath)))
            throw new InvalidOperationException("Production web hosting requires HTTPS OIDC, client credentials and an absolute data protection path.");

        if (Demo && !databaseOnly)
        {
            Scopes.AddRange(DemoData.Scopes());
            Rules.Add(new() { Application = "finance-demo", Environment = "demo", Kind = "api", MinimumSamples = 1, MinimumFailures = 1 });
            Monitors.AddRange(DemoData.Monitors());
            // A single local demo operator must be able to complete the maker-checker workflow. Development only.
            Administration.AllowSelfApproval = true;
        }
        if (Scopes.Count > 200 || Scopes.GroupBy(x => (x.Application, x.Environment)).Any(x => x.Count() > 1) ||
            Scopes.Where(x => x.IngestToken.Length > 0).GroupBy(x => x.IngestToken).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Scopes and their credentials must be unique and limited to 200.");
        foreach (var scope in Scopes)
        {
            if (!Validation.IsSlug(scope.Application) || !Validation.IsSlug(scope.Environment) ||
                string.IsNullOrWhiteSpace(scope.Label) || scope.Label.Length > 80 ||
                (scope.IngestToken.Length > 0 && ((!development && scope.IngestToken.Length < 32) ||
                    scope.IngestToken.Length > 256 || scope.IngestToken.Any(c => c < 33 || c > 126))) ||
                scope.AllowedNames.Length > 500 || scope.AllowedNames.Any(x => !Validation.IsAlias(x)) ||
                scope.AllowedCategories.Length > 100 || scope.AllowedCategories.Any(x => !Validation.IsAlias(x)) ||
                scope.StartupOrigins.Length > 10 || scope.StartupOrigins.Any(x => !Validation.IsOrigin(x, development)))
                throw new InvalidOperationException("Invalid application scope configuration.");
        }

        if (Rules.Count > 300 || Rules.Any(rule => (!string.IsNullOrEmpty(rule.Application) && !Validation.IsSlug(rule.Application))
            || (!string.IsNullOrEmpty(rule.Environment) && !Validation.IsSlug(rule.Environment))
            || !Validation.Kinds.Contains(rule.Kind) || (rule.Name.Length > 0 && !Validation.IsAlias(rule.Name))
            || rule.WindowMinutes is < 1 or > 60 || rule.MinimumSamples is < 1 or > 10000
            || rule.MinimumFailures is < 1 or > 10000 || !double.IsFinite(rule.FailurePercent)
            || rule.FailurePercent is < 0 or > 100 || (rule.RecoveryPercent is double recovery &&
                (!double.IsFinite(recovery) || recovery < 0 || recovery >= rule.FailurePercent))
            || rule.RecoveryWindows is < 2 or > 6 || rule.Severity is not ("critical" or "high" or "medium" or "low")))
            throw new InvalidOperationException("Invalid incident policy.");
        if (Probes.Count > 50 || Probes.Any(p => !Validation.IsHttps(p.Url) || !Validation.IsAlias(p.Name)
            || p.ExpectedStatus is < 200 or > 499 || p.IntervalSeconds is < 30 or > 86400 || p.CertificateWarningDays is < 0 or > 365
            || !Scopes.Any(s => s.Application == p.Application && s.Environment == p.Environment && s.AllowedNames.Contains(p.Name)))
            || Probes.GroupBy(p => (p.Application, p.Environment, p.Name)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Invalid scheduled HTTPS probe. Register its name on the application scope.");
        if (Webhooks.Count > 100 || (Webhooks.Count > 0 && !Validation.IsOrigin(PortalOrigin, false)) ||
            Webhooks.Select(w => w.Name).Distinct().Count() != Webhooks.Count ||
            Webhooks.Any(w => !Validation.IsAlias(w.Name) || !Validation.IsHttps(w.Url) ||
                (w.Application.Length > 0 && !Validation.IsSlug(w.Application)) ||
                (w.Environment.Length > 0 && !Validation.IsSlug(w.Environment)) ||
                w.BearerToken.Length > 1024 || w.BearerToken.Any(c => c < 33 || c > 126) ||
                w.Severities.Length == 0 || w.Severities.Any(x => x is not ("critical" or "high" or "medium" or "low"))))
            throw new InvalidOperationException("Invalid webhook configuration or PortalOrigin.");
        if (EmailRoutes.Count > 50 || (EmailRoutes.Count > 0 && (!Validation.IsOrigin(PortalOrigin, false) || !IsValidSmtp(Smtp, development))) ||
            EmailRoutes.Select(r => r.Name).Concat(Webhooks.Select(w => w.Name)).GroupBy(x => x, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            EmailRoutes.Any(r => !Validation.IsAlias(r.Name) || r.Recipients.Length is < 1 or > 20 ||
                r.Recipients.Any(x => !Validation.IsEmail(x)) || r.Recipients.Distinct(StringComparer.OrdinalIgnoreCase).Count() != r.Recipients.Length ||
                (r.Application.Length > 0 && !Validation.IsSlug(r.Application)) ||
                (r.Environment.Length > 0 && !Validation.IsSlug(r.Environment)) ||
                r.Severities.Length == 0 || r.Severities.Any(x => x is not ("critical" or "high" or "medium" or "low"))))
            throw new InvalidOperationException("Invalid email notification route, SMTP relay or PortalOrigin. Route names must be unique across webhooks and email.");

        if (Monitors.Count > 300 || Monitors.Any(m => m is null) ||
            Monitors.GroupBy(m => (m.Application, m.Environment, m.Name)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Monitors must be unique per application environment and limited to 300.");
        foreach (var monitor in Monitors)
        {
            if (monitor.Validate(Scopes) is { } problem)
                throw new InvalidOperationException($"Invalid monitor policy '{(Validation.IsAlias(monitor.Name) ? monitor.Name : "?")}': {problem}");
        }
    }

    private static bool IsValidSmtp(SmtpSettings smtp, bool development) =>
        smtp.Host.Length is > 0 and <= 253 && Uri.CheckHostName(smtp.Host) != UriHostNameType.Unknown &&
        smtp.Port is > 0 and < 65536 && Validation.IsEmail(smtp.From) && smtp.TimeoutSeconds is >= 1 and <= 120 &&
        smtp.UserName.Length <= 256 && smtp.Password.Length <= 1024 && !smtp.UserName.Any(char.IsControl) &&
        (smtp.UserName.Length == 0) == (smtp.Password.Length == 0) &&
        (smtp.UseTls || (smtp.UserName.Length == 0 && (development || IsLoopbackHost(smtp.Host))));

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));
}

public sealed class AuthSettings
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RoleClaim { get; set; } = "role";
    public string GroupsClaim { get; set; } = "groups";
}

public sealed class AppScope
{
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Demo { get; set; }
    public string[] ViewerGroups { get; set; } = [];
    public string[] AllowedNames { get; set; } = [];
    public string[] AllowedCategories { get; set; } = [];
    public string[] StartupOrigins { get; set; } = [];
    public string IngestToken { get; set; } = "";
    /// <summary>Emergency kill switch: intake answers 410 Gone and the SDK stops for the session.</summary>
    public bool IntakeSuspended { get; set; }
    public bool VerifyToken(string token) => IngestToken.Length > 0 && token.Length <= 256 &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(IngestToken)));
}

public sealed class DetectionRule
{
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Kind { get; set; } = "api";
    public string Name { get; set; } = "";
    public int WindowMinutes { get; set; } = 5;
    public int MinimumSamples { get; set; } = 10;
    public int MinimumFailures { get; set; } = 3;
    public double FailurePercent { get; set; } = 20;
    public double? RecoveryPercent { get; set; }
    public int RecoveryWindows { get; set; } = 2;
    public string Severity { get; set; } = "high";
}

public sealed class ProbeSettings
{
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public int IntervalSeconds { get; set; } = 60;
    public int ExpectedStatus { get; set; } = 200;
    /// <summary>A passing check is reported as degraded (category "tls") when the certificate expires sooner. 0 disables.</summary>
    public int CertificateWarningDays { get; set; } = 14;
}

public sealed class WebhookSettings
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string[] Severities { get; set; } = ["high", "critical"];
    public string BearerToken { get; set; } = "";
}

internal static class Access
{
    public static readonly string[] Roles =
        ["PlatformAdministrator", "ApplicationOwner", "ProductionSupport", "Developer", "ReadOnlyAuditor"];

    public static bool CanManage(ClaimsPrincipal user) =>
        user.IsInRole("PlatformAdministrator") || user.IsInRole("ApplicationOwner") || user.IsInRole("ProductionSupport");

    /// <summary>Approves or rejects configuration changes and sees platform-level audit entries.</summary>
    public static bool CanAdminister(ClaimsPrincipal user) => user.IsInRole("PlatformAdministrator");

    /// <summary>Proposes configuration changes for visible scopes.</summary>
    public static bool CanPropose(ClaimsPrincipal user) =>
        user.IsInRole("PlatformAdministrator") || user.IsInRole("ApplicationOwner");

    /// <summary>Reads and exports the audit log for visible scopes.</summary>
    public static bool CanAudit(ClaimsPrincipal user) =>
        user.IsInRole("PlatformAdministrator") || user.IsInRole("ReadOnlyAuditor");

    /// <summary>The opaque OIDC subject recorded for operator actions. Display names and e-mail are never stored.</summary>
    public static string Actor(ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "operator";
        return subject.Length <= 200 && !subject.Any(char.IsControl) ? subject : "subject-sha256:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
    }

    public static AppScope[] Visible(ClaimsPrincipal user, PlatformOptions options) =>
        user.IsInRole("PlatformAdministrator") ? [.. options.Scopes] :
        options.Scopes.Where(s => s.ViewerGroups.Any(g => user.HasClaim(options.Auth.GroupsClaim, g))).ToArray();
}
