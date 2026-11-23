using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseOps.Portal.Services;

public sealed record PortalContext(string Name, string Role, bool CanManage, bool Development,
    ScopeEntry[] Scopes, string CsrfToken, bool CanAdminister = false, bool CanPropose = false, bool CanAudit = false,
    bool SelfApproval = false);
public sealed record ScopeEntry(string Application, string Environment, string Label, bool Demo);

public sealed record Overview(long Requests, long FailedRequests, long SuccessfulRequests, long Sessions, long Exceptions,
    double? P50, double? P95, double? P99, long OpenIncidents, long StartupFailures,
    TrafficPoint[] Points, ServiceSummary[] Services, Incident[] Incidents);
public sealed record TrafficPoint(DateTimeOffset At, long Requests, long Failures, double? P95);
public sealed record ServiceSummary(string Application, string Environment, string Label, long Requests, long Failures,
    double? P95, long Sessions);

public sealed record Incident(string Id, string Application, string Environment, string Title, string Kind,
    string Name, string Severity, string Status, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long Count,
    string Evidence, string? Assignee);
public sealed record IncidentDetails(Incident Incident, IncidentHistory[] History);
public sealed record IncidentHistory(DateTimeOffset At, string Status, string Actor, string? Note, string? Assignee);
public sealed record IncidentUpdate(string Status, string? Note, string? Assignee);

public sealed record BrowserSession(string SessionId, string Application, string Environment, string Version,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long EventCount, long Failures, string Health);
public sealed record TelemetryEvent(string Id, DateTimeOffset OccurredAt, string Application, string Environment,
    string Version, string? SessionId, string Kind, string Name, string? Outcome, double? DurationMs, int? StatusCode,
    string? TraceId, string? SpanId, string? ParentSpanId, string? Category, double? Value, bool Anonymous);
public sealed record Dependency(string Name, long Requests, long Failures, double? P50, double? P95, double? P99);
public sealed record Release(string Application, string Environment, string Version, string? BuildId, string? CommitId,
    DateTimeOffset DeployedAt);
public sealed record PlatformHealth(long Pending, DateTimeOffset? OldestPendingAt, long Processed, long DeadLetters,
    DateTimeOffset? WorkerHeartbeat, long DatabaseBytes, long NotificationFailures = 0, long NotificationsPending = 0,
    long RollupBackfillRemaining = 0, long MonitorsAlerting = 0, long PendingChanges = 0, long AuditEntries = 0,
    long? FreeDiskBytes = null, long ProcessWorkingSetBytes = 0, double ProcessCpuSeconds = 0,
    DateTimeOffset? ProcessStartedAt = null, long ExpiredRawRows = 0, DateTimeOffset? OldestExpiredRawAt = null,
    double RawRetentionLagSeconds = 0, long NotificationsDeferred = 0);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PortalContext))]
[JsonSerializable(typeof(Overview))]
[JsonSerializable(typeof(Incident[]))]
[JsonSerializable(typeof(IncidentDetails))]
[JsonSerializable(typeof(IncidentUpdate))]
[JsonSerializable(typeof(BrowserSession[]))]
[JsonSerializable(typeof(TelemetryEvent[]))]
[JsonSerializable(typeof(Dependency[]))]
[JsonSerializable(typeof(Release[]))]
[JsonSerializable(typeof(PlatformHealth))]
internal partial class PortalJson : JsonSerializerContext;
