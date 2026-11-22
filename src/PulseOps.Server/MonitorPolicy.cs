using System.Text.Json.Serialization;

namespace PulseOps.Server;

/// <summary>
/// A scheduled monitor evaluated by the monitor engine. Policies come from <c>PulseOps:Monitors</c>
/// (read-only, source "configuration") or from approved administration changes (source "administration").
/// Every property is bounded by <see cref="Validate"/> before a policy is stored or evaluated.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MonitorPolicy
{
    public static readonly string[] Types = ["latency", "no-data", "regression"];
    public static readonly int[] Percentiles = [50, 75, 90, 95, 99];
    public static readonly string[] DurationKinds = ["api", "operation", "navigation", "synthetic", "startup"];

    public string Name { get; set; } = "";
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Type { get; set; } = "latency";
    public string Kind { get; set; } = "api";
    /// <summary>Approved telemetry name. Empty means every name of <see cref="Kind"/> in the scope.</summary>
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Severity { get; set; } = "high";
    public int WindowMinutes { get; set; } = 10;
    public int EvaluationDelayMinutes { get; set; } = 1;
    public int MinimumSamples { get; set; } = 20;
    /// <summary>Recovery requires healthy evaluations spanning RecoveryWindows × WindowMinutes.</summary>
    public int RecoveryWindows { get; set; } = 2;
    public int CooldownMinutes { get; set; } = 15;

    // Latency and regression
    public int Percentile { get; set; } = 95;
    public double ThresholdMs { get; set; }
    public double? RecoveryMs { get; set; }
    public int BaselineDays { get; set; }
    public double BaselineRatio { get; set; } = 1.5;

    // No data
    public int NoDataMinutes { get; set; }

    // Release regression
    public double FailureIncreasePercent { get; set; } = 5;
    public double LatencyIncreaseRatio { get; set; } = 1.5;
    public double MinimumLatencyIncreaseMs { get; set; } = 100;
    public int ComparisonHours { get; set; } = 24;

    [JsonIgnore] public int EffectiveNoDataMinutes => NoDataMinutes > 0 ? NoDataMinutes : 2 * WindowMinutes;
    [JsonIgnore] public double EffectiveRecoveryMs => RecoveryMs ?? ThresholdMs * 0.8;

    public MonitorPolicy Clone() => (MonitorPolicy)MemberwiseClone();

    /// <summary>Returns a short operator-facing reason when the policy is invalid, otherwise null.</summary>
    public string? Validate(IReadOnlyCollection<AppScope> scopes)
    {
        if (!Validation.IsAlias(Name))
            return "Monitor name must be a 1-64 character alias.";
        if (!Validation.IsSlug(Application) || !Validation.IsSlug(Environment))
            return "Application and environment must be registered slugs.";
        var scope = scopes.FirstOrDefault(s => s.Application == Application && s.Environment == Environment);
        if (scope is null)
            return "The application environment is not registered.";
        if (!Types.Contains(Type))
            return "Monitor type must be latency, no-data or regression.";
        if (!Validation.Kinds.Contains(Kind) || Kind == "log")
            return "Kind is not a supported telemetry kind.";
        if (Target is null || (Target.Length > 0 &&
            !(Kind == "startup" ? Validation.StartupNames.Contains(Target) : scope.AllowedNames.Contains(Target))))
            return "Target is not an approved name for the application environment.";
        if (Severity is not ("critical" or "high" or "medium" or "low"))
            return "Severity must be critical, high, medium or low.";
        if (WindowMinutes is < 1 or > 1440 || EvaluationDelayMinutes is < 0 or > 60 || MinimumSamples is < 1 or > 100_000 ||
            RecoveryWindows is < 1 or > 12 || CooldownMinutes is < 0 or > 1440)
            return "Window, delay, sample, recovery or cooldown settings are out of range.";

        switch (Type)
        {
            case "latency":
                if (!DurationKinds.Contains(Kind))
                    return "Latency monitors require a kind that records durations.";
                if (!Percentiles.Contains(Percentile) || !double.IsFinite(ThresholdMs) || ThresholdMs is < 1 or > 86_400_000)
                    return "Latency monitors require a supported percentile and a threshold between 1 ms and 24 hours.";
                if (RecoveryMs is double recovery && (!double.IsFinite(recovery) || recovery < 0 || recovery >= ThresholdMs))
                    return "Recovery latency must be below the threshold.";
                if (BaselineDays is < 0 or > 30 || (BaselineDays > 0 && (!double.IsFinite(BaselineRatio) || BaselineRatio is < 1.05 or > 20)))
                    return "Baseline days must be 0-30 with a ratio between 1.05 and 20.";
                break;
            case "no-data":
                if (NoDataMinutes is < 0 or > 10_080)
                    return "No-data timeframe must be 0 (twice the window) to 10,080 minutes.";
                break;
            case "regression":
                if (!DurationKinds.Contains(Kind))
                    return "Regression monitors require a kind that records outcomes and durations.";
                if (!Percentiles.Contains(Percentile) || !double.IsFinite(FailureIncreasePercent) || FailureIncreasePercent is < 0.1 or > 100 ||
                    !double.IsFinite(LatencyIncreaseRatio) || (LatencyIncreaseRatio != 0 && LatencyIncreaseRatio is < 1.05 or > 20) ||
                    !double.IsFinite(MinimumLatencyIncreaseMs) || MinimumLatencyIncreaseMs is < 0 or > 3_600_000 ||
                    ComparisonHours is < 1 or > 168)
                    return "Regression thresholds or comparison hours are out of range.";
                break;
        }
        return null;
    }
}

/// <summary>A proposed configuration change. Exactly one entity payload applies to each operation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeProposal(string? Entity, string? Operation, MonitorPolicy? Monitor,
    MaintenanceRequest? Maintenance, string? Application, string? Environment, string? Target, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MaintenanceRequest(string? Application, string? Environment, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeDecision(string? Note);

public sealed class AdministrationSettings
{
    /// <summary>Permits a proposer to approve their own change. Rejected outside Development.</summary>
    public bool AllowSelfApproval { get; set; }
    public int ChangeExpiryHours { get; set; } = 72;
    public int MaximumPendingChanges { get; set; } = 50;
}

public sealed class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>STARTTLS. Cleartext is accepted only for a loopback relay or in Development, never with credentials.</summary>
    public bool UseTls { get; set; } = true;
    public string From { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class EmailRoute
{
    public string Name { get; set; } = "";
    public string[] Recipients { get; set; } = [];
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string[] Severities { get; set; } = ["high", "critical"];
}
