using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PulseOps.Server;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TelemetryBatch(int SchemaVersion, string? Application, string? Environment, TelemetryEvent?[]? Events);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TelemetryEvent(Guid Id, DateTimeOffset OccurredAt, string? Version, Guid SessionId,
    string? Kind, string? Name, string? Outcome, double? DurationMs = null, int? StatusCode = null,
    string? TraceId = null, string? SpanId = null, string? ParentSpanId = null, string? Category = null, double? Value = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReleaseMarker(string? Application, string? Environment, string? Version, string? BuildId, string? CommitId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IncidentChange(string? Status, string? Note, string? Assignee);

internal static partial class Validation
{
    public static readonly HashSet<string> Kinds = ["api", "error", "operation", "startup", "navigation", "metric", "session", "synthetic", "log"];
    public static readonly HashSet<string> Outcomes = ["success", "failure", "degraded", "unknown", "cancelled"];
    public static readonly HashSet<string> StartupNames = ["PageLoaded", "BootstrapStarted", "RuntimeLoading", "RuntimeStarted",
        "BlazorInitialised", "ApplicationReady", "StartupTimeout", "AssetLoadFailure", "JavaScriptError", "PromiseRejection",
        "BlankScreen", "UnsupportedBrowser", "ServiceWorkerFailure"];
    public static readonly HashSet<string> Categories = ["timeout", "cancelled", "network", "http", "authentication",
        "exception", "runtime", "resource", "service-worker", "unsupported", "startup", "render", "javascript",
        "unobserved", "measured", "estimated", "unavailable", "invalid-data", "disconnected", "demo", "probe",
        "server-error", "client-error", "rate-limited", "dns", "connection", "tls", "network-or-browser-policy",
        "cancelled-or-timeout", "unauthorized", "json", "format", "argument", "null-reference", "invalid-operation", "application"];
    public static readonly HashSet<string> StartupCategories = ["timeout", "runtime", "resource", "service-worker",
        "unsupported", "javascript", "startup"];
    public static bool IsAlias(string? value) => value is not null && Alias().IsMatch(value);
    public static bool IsSlug(string? value) => value is not null && Slug().IsMatch(value);
    public static bool IsVersion(string? value) => value is not null && value.Length <= 32 && IsAlias(value);
    public static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    public static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    /// <summary>A bare addr-spec without display name or surrounding whitespace, at most 254 characters.</summary>
    public static bool IsEmail(string? value) =>
        value is { Length: > 2 and <= 254 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) &&
        System.Net.Mail.MailAddress.TryCreate(value, out var address) &&
        address.DisplayName.Length == 0 && string.Equals(address.Address, value, StringComparison.Ordinal);
    public static bool IsOrigin(string value, bool development) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || (development && uri.Scheme == "http" && uri.IsLoopback)) &&
        uri.GetLeftPart(UriPartial.Authority) == value && uri.UserInfo.Length == 0;

    public static bool IsEvent(TelemetryEvent? item, AppScope scope, DateTimeOffset now, bool anonymous)
    {
        if (item is null || item.Id == Guid.Empty || item.SessionId == Guid.Empty ||
            item.OccurredAt < now.AddDays(-1) || item.OccurredAt > now.AddMinutes(5) || !IsVersion(item.Version) ||
            item.Kind is null || !Kinds.Contains(item.Kind) || item.Outcome is null || !Outcomes.Contains(item.Outcome) ||
            !IsAlias(item.Name) || item.DurationMs is < 0 or > 86_400_000 ||
            item.StatusCode is < 100 or > 599 || item.Value is < -1e12 or > 1e12 ||
            (item.Value is double value && !double.IsFinite(value)) ||
            (item.DurationMs is double duration && !double.IsFinite(duration)) ||
            (item.TraceId is not null && (!IsHex(item.TraceId, 32) || item.TraceId.All(c => c == '0'))) ||
            (item.SpanId is not null && (item.TraceId is null || !IsHex(item.SpanId, 16) || item.SpanId.All(c => c == '0'))) ||
            (item.ParentSpanId is not null && (item.SpanId is null || !IsHex(item.ParentSpanId, 16) || item.ParentSpanId.All(c => c == '0'))))
            return false;

        if (anonymous)
            return item.Kind == "startup" && StartupNames.Contains(item.Name!) && item.TraceId is null &&
                item.StatusCode is null && item.Value is null &&
                (item.Category is null || StartupCategories.Contains(item.Category)) &&
                item.Outcome is "success" or "failure";

        return (item.Kind == "startup" ? StartupNames.Contains(item.Name!) : scope.AllowedNames.Contains(item.Name)) &&
            (item.Category is null || Categories.Contains(item.Category) || scope.AllowedCategories.Contains(item.Category));
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Alias();

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();
}
