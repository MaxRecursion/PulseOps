using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseOps.Portal.Services;

// Shapes of the /api/v1 monitors, history and administration contract. Request records mirror the server's
// MonitorPolicy, ChangeProposal, MaintenanceRequest and ChangeDecision property names exactly: the server
// rejects unmapped members.

public sealed record MonitorStatus(string Application, string Environment, string Name, string Type, string Kind,
    string Target, string Severity, bool Enabled, string Source, long Revision, string State, DateTimeOffset? Since,
    DateTimeOffset? EvaluatedAt, double? Observed, double? Baseline, long? Samples, string Detail, string? IncidentId,
    MonitorDefinition? Definition);

/// <summary>Portal copy of the server MonitorPolicy. Property names, defaults and bounds must stay identical.</summary>
public sealed record MonitorDefinition
{
    public string Name { get; set; } = "";
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Type { get; set; } = "latency";
    public string Kind { get; set; } = "api";
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Severity { get; set; } = "high";
    public int WindowMinutes { get; set; } = 10;
    public int EvaluationDelayMinutes { get; set; } = 1;
    public int MinimumSamples { get; set; } = 20;
    public int RecoveryWindows { get; set; } = 2;
    public int CooldownMinutes { get; set; } = 15;
    public int Percentile { get; set; } = 95;
    public double ThresholdMs { get; set; }
    public double? RecoveryMs { get; set; }
    public int BaselineDays { get; set; }
    public double BaselineRatio { get; set; } = 1.5;
    public int NoDataMinutes { get; set; }
    public double FailureIncreasePercent { get; set; } = 5;
    public double LatencyIncreaseRatio { get; set; } = 1.5;
    public double MinimumLatencyIncreaseMs { get; set; } = 100;
    public int ComparisonHours { get; set; } = 24;
}

public sealed record HistoryResult(string Resolution, long StepMs, DateTimeOffset From, DateTimeOffset Until,
    bool ApproximatePercentiles, HistoryPoint[] Points, HistorySeries[] Series);
public sealed record HistoryPoint(DateTimeOffset At, long Total, long Failures, long Degraded, long Cancelled,
    double? P50, double? P95, double? P99, double? AverageMs, double? MaxMs);
public sealed record HistorySeries(string Application, string Environment, string Kind, string Name, long Total,
    long Failures, double? P95);

public sealed record ConfigChange(string Id, DateTimeOffset CreatedAt, string Proposer, string Entity, string Operation,
    string Application, string Environment, string Target, JsonElement Payload, string Reason, long? BaseRevision,
    string Status, DateTimeOffset? DecidedAt, string? Decider, string? DecisionNote, bool CanApprove, bool CanWithdraw);

public sealed record MaintenanceWindow(string Id, string Application, string Environment, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string Reason, DateTimeOffset CreatedAt, string CreatedBy, string ChangeId,
    DateTimeOffset? CancelledAt, bool Active);

public sealed record AuditChain(bool Verified, long Entries, string HeadHash, long? BrokenAtSeq);
public sealed record AuditEntry(long Seq, DateTimeOffset At, string Actor, string Action, string Application,
    string Environment, string Target, string Detail, string PreviousHash, string Hash);
public sealed record AuditResult(AuditChain Chain, AuditEntry[] Entries);

public sealed record ChangeProposal(string Entity, string Operation, MonitorDefinition? Monitor = null,
    MaintenanceProposal? Maintenance = null, string? Application = null, string? Environment = null,
    string? Target = null, string? Reason = null);
public sealed record MaintenanceProposal(string Application, string Environment, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string Reason);
public sealed record ChangeDecision(string? Note);
public sealed record ChangeCreated(string Id);
public sealed record ApiError(string? Error, string? Detail);

// A second context: the System.Text.Json generator fails (CS8785, duplicate hint names) when [JsonSerializable]
// attributes are spread over several partial declarations of PortalJson. Nulls are omitted from requests.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MonitorStatus[]))]
[JsonSerializable(typeof(MonitorDefinition))]
[JsonSerializable(typeof(HistoryResult))]
[JsonSerializable(typeof(ConfigChange[]))]
[JsonSerializable(typeof(MaintenanceWindow[]))]
[JsonSerializable(typeof(MaintenanceProposal))]
[JsonSerializable(typeof(AuditResult))]
[JsonSerializable(typeof(ChangeProposal))]
[JsonSerializable(typeof(ChangeDecision))]
[JsonSerializable(typeof(ChangeCreated))]
[JsonSerializable(typeof(ApiError))]
internal partial class AdminJson : JsonSerializerContext;

/// <summary>Client-side mirror of MonitorPolicy.Validate so obvious mistakes never reach the server.</summary>
public static class MonitorRules
{
    public static readonly string[] Types = ["latency", "no-data", "regression"];
    public static readonly int[] Percentiles = [50, 75, 90, 95, 99];
    public static readonly string[] DurationKinds = ["api", "operation", "navigation", "synthetic", "startup"];
    public static readonly string[] Kinds = ["api", "operation", "navigation", "synthetic", "startup", "error", "metric", "session"];
    public static readonly string[] Severities = ["critical", "high", "medium", "low"];
    public static readonly string[] StartupNames = ["PageLoaded", "BootstrapStarted", "RuntimeLoading", "RuntimeStarted",
        "BlazorInitialised", "ApplicationReady", "StartupTimeout", "AssetLoadFailure", "JavaScriptError", "PromiseRejection",
        "BlankScreen", "UnsupportedBrowser", "ServiceWorkerFailure"];

    public static bool IsAlias(string? value) => value is { Length: >= 1 and <= 64 } && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Returns the server's message for the first violated bound, otherwise null. Approval of non-startup
    /// target aliases is only known to the server.</summary>
    public static string? Validate(MonitorDefinition monitor, IReadOnlyCollection<ScopeEntry> scopes)
    {
        if (!IsAlias(monitor.Name))
            return "Monitor name must be a 1-64 character alias.";
        if (!scopes.Any(s => s.Application == monitor.Application && s.Environment == monitor.Environment))
            return "The application environment is not registered.";
        if (!Types.Contains(monitor.Type))
            return "Monitor type must be latency, no-data or regression.";
        if (!Kinds.Contains(monitor.Kind))
            return "Kind is not a supported telemetry kind.";
        if (monitor.Target.Length > 0 && (monitor.Kind == "startup" ? !StartupNames.Contains(monitor.Target) : !IsAlias(monitor.Target)))
            return "Target is not an approved name for the application environment.";
        if (!Severities.Contains(monitor.Severity))
            return "Severity must be critical, high, medium or low.";
        if (monitor.WindowMinutes is < 1 or > 1440 || monitor.EvaluationDelayMinutes is < 0 or > 60 ||
            monitor.MinimumSamples is < 1 or > 100_000 || monitor.RecoveryWindows is < 1 or > 12 ||
            monitor.CooldownMinutes is < 0 or > 1440)
            return "Window, delay, sample, recovery or cooldown settings are out of range.";

        switch (monitor.Type)
        {
            case "latency":
                if (!DurationKinds.Contains(monitor.Kind))
                    return "Latency monitors require a kind that records durations.";
                if (!Percentiles.Contains(monitor.Percentile) || !double.IsFinite(monitor.ThresholdMs) || monitor.ThresholdMs is < 1 or > 86_400_000)
                    return "Latency monitors require a supported percentile and a threshold between 1 ms and 24 hours.";
                if (monitor.RecoveryMs is double recovery && (!double.IsFinite(recovery) || recovery < 0 || recovery >= monitor.ThresholdMs))
                    return "Recovery latency must be below the threshold.";
                if (monitor.BaselineDays is < 0 or > 30 ||
                    (monitor.BaselineDays > 0 && (!double.IsFinite(monitor.BaselineRatio) || monitor.BaselineRatio is < 1.05 or > 20)))
                    return "Baseline days must be 0-30 with a ratio between 1.05 and 20.";
                break;
            case "no-data":
                if (monitor.NoDataMinutes is < 0 or > 10_080)
                    return "No-data timeframe must be 0 (twice the window) to 10,080 minutes.";
                break;
            case "regression":
                if (!DurationKinds.Contains(monitor.Kind))
                    return "Regression monitors require a kind that records outcomes and durations.";
                if (!Percentiles.Contains(monitor.Percentile) || !double.IsFinite(monitor.FailureIncreasePercent) ||
                    monitor.FailureIncreasePercent is < 0.1 or > 100 || !double.IsFinite(monitor.LatencyIncreaseRatio) ||
                    (monitor.LatencyIncreaseRatio != 0 && monitor.LatencyIncreaseRatio is < 1.05 or > 20) ||
                    !double.IsFinite(monitor.MinimumLatencyIncreaseMs) || monitor.MinimumLatencyIncreaseMs is < 0 or > 3_600_000 ||
                    monitor.ComparisonHours is < 1 or > 168)
                    return "Regression thresholds or comparison hours are out of range.";
                break;
        }
        return null;
    }

    /// <summary>
    /// Reason text shared by every proposal form. Mirrors the server: trimmed, at least 3 characters, no control
    /// characters (line breaks or tabs) and at most <paramref name="maximum"/> characters.
    /// </summary>
    public static string? ReasonProblem(string? reason, int maximum = 500)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length == 0)
            return "Enter a reason for the reviewer.";
        if (text.Any(char.IsControl))
            return "Write the reason on one line, without line breaks or tabs.";
        if (text.Length < 3)
            return "Enter a reason of at least 3 characters.";
        return text.Length > maximum ? $"Keep the reason within {maximum} characters." : null;
    }

    /// <summary>Optional decision note: one line of at most 500 characters, as the server requires.</summary>
    public static string? NoteProblem(string? note)
    {
        var text = note?.Trim() ?? "";
        if (text.Any(char.IsControl))
            return "Write the decision note on one line, without line breaks or tabs.";
        return text.Length > 500 ? "Keep the decision note within 500 characters." : null;
    }
}

public static class MonitorText
{
    public static string TypeLabel(string type) => type switch
    {
        "latency" => "Latency",
        "no-data" => "No data",
        "regression" => "Release regression",
        _ => Presentation.Title(type)
    };

    public static string StateLabel(string state) => state == "InsufficientData" ? "Insufficient data" : state;

    public static string StateTone(string state) => state switch
    {
        "Alert" => "danger",
        "InsufficientData" => "warning",
        "OK" => "good",
        _ => "neutral"
    };

    public static string SourceLabel(string source) => source == "administration" ? "Change control" : "Configuration";

    public static string Target(string kind, string? target) => string.IsNullOrEmpty(target) ? "All " + KindPlural(kind) : target;

    public static string Threshold(MonitorDefinition? monitor) => monitor?.Type switch
    {
        "latency" => $"p{monitor.Percentile} >= {Presentation.Duration(monitor.ThresholdMs)}" + (monitor.BaselineDays > 0
            ? $" · x{Presentation.Number(monitor.BaselineRatio)} vs {monitor.BaselineDays}-day baseline" : ""),
        "no-data" => $"silent >= {(monitor.NoDataMinutes > 0 ? monitor.NoDataMinutes : 2 * monitor.WindowMinutes)} min",
        "regression" => $"+{Presentation.Number(monitor.FailureIncreasePercent)} pts" + (monitor.LatencyIncreaseRatio > 0
            ? $" or x{Presentation.Number(monitor.LatencyIncreaseRatio)} p{monitor.Percentile}" : ""),
        _ => "—"
    };

    /// <summary>Latency values are milliseconds, no-data values minutes since the last signal and
    /// regression values failure-rate percentages.</summary>
    public static string Value(string type, double? value) => value is not double number || !double.IsFinite(number) ? "—" : type switch
    {
        "latency" => Presentation.Duration(number),
        "no-data" => Presentation.Number(number) + " min",
        "regression" => Presentation.Number(number) + "%",
        _ => Presentation.Number(number)
    };

    private static string KindPlural(string kind) => kind switch
    {
        "api" => "API requests",
        "operation" => "business operations",
        "navigation" => "navigations",
        "synthetic" => "synthetic checks",
        "startup" => "startup events",
        "error" => "exceptions",
        "metric" => "browser metrics",
        "session" => "session events",
        _ => kind
    };
}

public static class ChangeText
{
    public static string Title(ConfigChange change) => (change.Entity, change.Operation) switch
    {
        ("monitor", "upsert") => (change.BaseRevision is null ? "Create monitor " : "Update monitor ") + change.Target,
        ("monitor", "delete") => "Delete monitor " + change.Target,
        ("maintenance", "create") => "Schedule maintenance",
        ("maintenance", "cancel") => "Cancel maintenance window " + Presentation.Id(change.Target),
        _ => $"{Presentation.Title(change.Operation)} {change.Entity} {change.Target}".TrimEnd()
    };

    public static string StatusTone(string status) => status switch
    {
        "Applied" => "good",
        "Rejected" => "danger",
        "Expired" => "warning",
        "Pending" => "accent",
        _ => "neutral"
    };

    /// <summary>A one-line description of an untrusted change payload. Unknown shapes fall back to scalar fields.</summary>
    public static string Summary(ConfigChange change)
    {
        var payload = change.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
            return Fallback(change);
        if (change.Entity == "monitor" && change.Operation == "upsert" && Monitor(payload) is { } monitor)
            return $"{MonitorText.TypeLabel(monitor.Type)} · {MonitorText.Target(monitor.Kind, monitor.Target)} · " +
                $"{MonitorText.Threshold(monitor)} · {monitor.WindowMinutes}-minute window · {Presentation.Title(monitor.Severity)} severity" +
                (monitor.Enabled ? "" : " · disabled");
        var maintenance = Property(payload, "maintenance") is { ValueKind: JsonValueKind.Object } nested ? nested : payload;
        if (change.Entity == "maintenance" && change.Operation == "create" &&
            Time(maintenance, "startsAt") is { } starts && Time(maintenance, "endsAt") is { } ends)
            return $"Suppress notifications from {Presentation.Date(starts)} to {Presentation.Date(ends)} ({Span(ends - starts)})";
        return Fallback(change);
    }

    public static string Span(TimeSpan span) => span <= TimeSpan.Zero ? "no duration"
        : span.TotalDays >= 1 ? Presentation.Number(Math.Round(span.TotalDays, 1)) + " days"
        : span.TotalHours >= 1 ? Presentation.Number(Math.Round(span.TotalHours, 1)) + " hours"
        : Presentation.Number(Math.Round(span.TotalMinutes)) + " minutes";

    private static MonitorDefinition? Monitor(JsonElement payload)
    {
        var candidate = Property(payload, "monitor") is { ValueKind: JsonValueKind.Object } nested ? nested : payload;
        if (Property(candidate, "type") is not { ValueKind: JsonValueKind.String })
            return null;
        try
        {
            return candidate.Deserialize(AdminJson.Default.MonitorDefinition);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Fallback(ConfigChange change)
    {
        if (change.Operation == "delete")
            return "Removes the monitor and its evaluation state when approved.";
        if (change.Operation == "cancel")
            return "Ends the maintenance window when approved.";
        if (change.Payload.ValueKind != JsonValueKind.Object)
            return "No additional details.";
        var text = string.Join(" · ", change.Payload.EnumerateObject()
            .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .Take(6).Select(p => p.Name + ": " + (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())));
        return text.Length == 0 ? "No additional details." : text.Length <= 240 ? text : text[..239] + "…";
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value && value.TryGetDateTimeOffset(out var time) ? time : null;
}

public static class MaintenanceRules
{
    private static readonly string[] InputFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    /// <summary>Reads a datetime-local value as UTC. The browser time zone is deliberately never applied.</summary>
    public static DateTimeOffset? ParseUtc(string? value) =>
        DateTime.TryParseExact(value?.Trim(), InputFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
            : null;

    public static string ToInput(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    public static string? Validate(DateTimeOffset? starts, DateTimeOffset? ends, DateTimeOffset now) =>
        starts is null || ends is null ? "Enter start and end times in UTC."
        : ends <= starts ? "The window must end after it starts."
        : ends <= now ? "The window must end in the future."
        : null;
}
