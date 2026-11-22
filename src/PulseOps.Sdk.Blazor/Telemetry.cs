using System.Text.Json.Serialization;

namespace PulseOps.Sdk.Blazor;

internal sealed record TelemetryBatch(
    int SchemaVersion, string Application, string Environment, IReadOnlyList<TelemetryEvent> Events);

internal sealed record TelemetryEvent(
    Guid Id, DateTimeOffset OccurredAt, string Version, Guid SessionId, string Kind, string Name, string Outcome,
    double? DurationMs = null, int? StatusCode = null, string? TraceId = null, string? SpanId = null,
    string? ParentSpanId = null, string? Category = null, double? Value = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TelemetryBatch))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext;

public sealed record PulseOpsDiagnostics(long Queued, long Accepted, long Dropped, long Retries, int Pending);
