using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

namespace PulseOps.Server;

internal sealed record MonitorStatus(string Application, string Environment, string Name, string Type, string Kind,
    string Target, string Severity, bool Enabled, string Source, long Revision, string State, DateTimeOffset? Since,
    DateTimeOffset? EvaluatedAt, double? Observed, double? Baseline, long? Samples, string Detail, string? IncidentId,
    MonitorPolicy Definition);

public sealed partial class Storage
{
    internal const int MonitorSampleLimit = 100_000;
    private const long MinuteMs = 60_000;

    private sealed record ScheduledPolicy(MonitorPolicy Policy, string Source, long Revision, string? ChangeId)
    {
        public string Hash { get; } = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Policy, JsonSerializerOptions.Web))));
    }

    private sealed record MonitorState(string State, long Since, long Evaluated, long? HealthySince, double? Observed,
        double? Baseline, long? Samples, string Detail, string Hash, string? IncidentId);

    private enum Condition { Insufficient, Healthy, BetweenThresholds, Breach }
    private sealed record Measurement(Condition Condition, double? Observed, double? Baseline, long? Samples, string Detail);
    private sealed record MonitorIncident(string Id, string Status, string Assignee);

    private List<ScheduledPolicy> MonitorPolicies(SqliteConnection db, SqliteTransaction transaction, AppScope[] scopes)
    {
        if (scopes.Length == 0)
            return [];
        var allowed = scopes.Select(s => (s.Application, s.Environment)).ToHashSet();
        var entries = options.Monitors.Where(p => allowed.Contains((p.Application, p.Environment)))
            .ToDictionary(p => (p.Application, p.Environment, p.Name), p => new ScheduledPolicy(p, "configuration", 0, null));
        var filter = new QueryFilter(scopes, 0, 0, null, 0, 0, null, null, null, null);
        using var command = db.Command(transaction, $"""
            SELECT application, environment, name, revision, change_id, definition
            FROM monitor_policies WHERE {filter.ScopeClause}
            ORDER BY application, environment, name LIMIT $limit
            """);
        filter.Bind(command);
        // Approval keeps stored policies within the budget; the limit also bounds a restored or hand-edited database.
        command.Parameters["$limit"].Value = MaximumManagedMonitors;
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var key = (rows.GetString(0), rows.GetString(1), rows.GetString(2));
            if (entries.ContainsKey(key))
                continue;
            MonitorPolicy? policy;
            try { policy = JsonSerializer.Deserialize<MonitorPolicy>(rows.GetString(5), JsonSerializerOptions.Web); }
            catch (JsonException) { continue; }
            if (policy is null || key != (policy.Application, policy.Environment, policy.Name) ||
                policy.Target is null || policy.Validate(scopes) is not null)
                continue;
            entries.Add(key, new(policy, "administration", rows.GetInt64(3), rows.GetString(4)));
        }
        return [.. entries.Values.OrderBy(e => e.Policy.Application, StringComparer.Ordinal)
            .ThenBy(e => e.Policy.Environment, StringComparer.Ordinal).ThenBy(e => e.Policy.Name, StringComparer.Ordinal)];
    }

    internal List<MonitorStatus> MonitorStatuses(AppScope[] scopes)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var result = new List<MonitorStatus>();
        foreach (var entry in MonitorPolicies(db, transaction, scopes))
        {
            var p = entry.Policy;
            var state = ReadMonitorState(db, transaction, p);
            if (state?.Hash != entry.Hash)
                state = null;
            result.Add(new(p.Application, p.Environment, p.Name, p.Type, p.Kind, p.Target, p.Severity, p.Enabled,
                entry.Source, entry.Revision, p.Enabled ? state?.State ?? "InsufficientData" : "Disabled",
                Milliseconds(state?.Since), Milliseconds(state?.Evaluated), state?.Observed, state?.Baseline,
                state?.Samples, state?.Detail ?? (p.Enabled ? "Awaiting the first evaluation." : "Policy is disabled."),
                state?.IncidentId, p));
        }
        return result.OrderBy(m => m.State == "Alert" ? 0 : 1).ToList();
    }

    /// <summary>Evaluates due policies. Read observations in WAL snapshots, then commit each state and incident together.</summary>
    public int EvaluateMonitors(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        using var db = Open();
        List<ScheduledPolicy> entries;
        using (var snapshot = db.BeginTransaction(deferred: true))
            entries = MonitorPolicies(db, snapshot, [.. options.Scopes]);
        int changes = RemoveRetiredMonitorStates(db);
        long at = now.ToUnixTimeMilliseconds();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var policy = entry.Policy;
            MonitorState? previous;
            Measurement measure;
            using (var snapshot = db.BeginTransaction(deferred: true))
            {
                previous = ReadMonitorState(db, snapshot, policy);
                if (previous is not null && previous.Hash == entry.Hash &&
                    (!policy.Enabled || at - previous.Evaluated < options.MonitorIntervalSeconds * 1000L))
                    continue;
                measure = !policy.Enabled ? new(Condition.Insufficient, null, null, null, "Policy is disabled.") :
                    Measure(db, snapshot, policy, at, previous?.Hash == entry.Hash ? previous : null);
            }
            using var write = db.BeginTransaction();
            // A concurrent evaluator or an approved edit/delete can win while observations are read.
            if (previous != ReadMonitorState(db, write, policy) || !IsCurrentMonitor(db, write, entry))
                continue;
            var next = ApplyMeasurement(db, write, entry, previous, measure, at);
            StoreMonitorState(db, write, policy, next);
            write.Commit();
            if (previous?.State != next.State || previous.IncidentId != next.IncidentId)
                changes++;
        }
        return changes;
    }

    private int RemoveRetiredMonitorStates(SqliteConnection db)
    {
        // Configuration may remove a policy or a whole scope while the worker is stopped. A stale Alert here
        // would continue contributing to platform health even though the policy no longer appears in the portal.
        var configured = options.Monitors.Select(p => (p.Application, p.Environment, p.Name)).ToHashSet();
        var scopes = options.Scopes.Select(s => (s.Application, s.Environment)).ToHashSet();
        var retired = new List<(string App, string Env, string Name)>();
        using (var select = db.Command(null, """
                   SELECT s.application,s.environment,s.name,p.name FROM monitor_state s
                   LEFT JOIN monitor_policies p USING(application, environment, name)
                   """))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read() && retired.Count < 300)
            {
                var key = (App: reader.GetString(0), Env: reader.GetString(1), Name: reader.GetString(2));
                if (!configured.Contains(key) && (reader.IsDBNull(3) || !scopes.Contains((key.App, key.Env))))
                    retired.Add(key);
            }
        }
        if (retired.Count == 0)
            return 0;
        using var transaction = db.BeginTransaction();
        using var remove = db.Command(transaction, """
            DELETE FROM monitor_state WHERE application=$app AND environment=$env AND name=$name
                AND ($unregistered OR NOT EXISTS(SELECT 1 FROM monitor_policies
                    WHERE application=$app AND environment=$env AND name=$name))
            """, ("$app", ""), ("$env", ""), ("$name", ""), ("$unregistered", false));
        int removed = 0;
        foreach (var key in retired)
        {
            Set(remove, ("$app", key.App), ("$env", key.Env), ("$name", key.Name),
                ("$unregistered", !scopes.Contains((key.App, key.Env))));
            removed += remove.ExecuteNonQuery();
        }
        transaction.Commit();
        return removed;
    }

    private static bool IsCurrentMonitor(SqliteConnection db, SqliteTransaction transaction, ScheduledPolicy entry)
    {
        if (entry.ChangeId is null)
            return true;
        using var check = MonitorCommand(db, transaction, entry.Policy,
            "SELECT change_id FROM monitor_policies WHERE application=$app AND environment=$env AND name=$name");
        return check.ExecuteScalar() as string == entry.ChangeId;
    }

    private Measurement Measure(SqliteConnection db, SqliteTransaction transaction, MonitorPolicy p, long at, MonitorState? previous)
    {
        long until = at - p.EvaluationDelayMinutes * MinuteMs;
        long since = until - p.WindowMinutes * MinuteMs;
        if (p.Type == "no-data")
        {
            using var command = MonitorCommand(db, transaction, p, $"""
                SELECT MAX(last_received) FROM signal_activity WHERE application=$app AND environment=$env AND kind=$kind
                    {(p.Target.Length == 0 ? "" : "AND name=$target")}
                """);
            long? last = command.ExecuteScalar() as long?;
            // A never-seen signal gets one full grace period after the first evaluation. Once activity ages out
            // keep the persisted silence clock so an open outage can survive retention and a worker restart.
            var anchor = last ?? (previous?.Observed is double minutes && minutes > 0
                ? previous.Evaluated - p.EvaluationDelayMinutes * MinuteMs - (long)Math.Round(minutes * MinuteMs)
                : previous?.Since ?? at);
            double silence = Math.Max(0, (until - anchor) / (double)MinuteMs);
            bool silent = silence >= p.EffectiveNoDataMinutes;
            var condition = silent ? Condition.Breach : last.HasValue ? Condition.Healthy : Condition.Insufficient;
            string detail = last is null ? "No trusted signal has been observed." : "Time since the last trusted receipt.";
            return new(condition, silence, p.EffectiveNoDataMinutes, null, detail);
        }
        if (p.Type == "regression")
            return MeasureRegression(db, transaction, p, since, until);

        var current = ReadMonitorWindow(db, transaction, p, since, until, null);
        if (current is null)
            return ExceededWindow();
        double? value = current.Percentile(p.Percentile);
        if (value is null || current.Durations.Count < p.MinimumSamples)
            return new(Condition.Insufficient, value, null, current.Durations.Count, "Insufficient measured durations in the current window.");
        LatencySample baseline = default;
        if (p.BaselineDays > 0)
        {
            long end = since / HourMs * HourMs;
            baseline = HistoricalLatency(db, transaction, p.Application, p.Environment, p.Kind,
                p.Target, end - p.BaselineDays * DayMs, end, p.Percentile);
            if (baseline.ValueMs is null || baseline.Samples < p.MinimumSamples)
                return new(Condition.Insufficient, value, baseline.ValueMs, current.Durations.Count,
                    "Insufficient historical latency in the preceding complete UTC hours.");
        }
        bool high = value >= p.ThresholdMs && (baseline.ValueMs is null || value >= baseline.ValueMs * p.BaselineRatio);
        var level = high ? Condition.Breach : value <= p.EffectiveRecoveryMs ? Condition.Healthy : Condition.BetweenThresholds;
        return new(level, value, baseline.ValueMs, current.Durations.Count,
            FormattableString.Invariant($"p{p.Percentile} latency {value:F1} ms; alert threshold {p.ThresholdMs:G} ms."));
    }

    private static Measurement MeasureRegression(SqliteConnection db, SqliteTransaction transaction, MonitorPolicy p,
        long since, long until)
    {
        var releases = new List<(string Version, long At)>(2);
        using (var command = MonitorCommand(db, transaction, p, """
                   SELECT version,deployed FROM releases WHERE application=$app AND environment=$env AND deployed < $until
                   ORDER BY deployed DESC LIMIT 1025
                   """, ("$until", until)))
        using (var row = command.ExecuteReader())
        {
            int markers = 0;
            while (row.Read())
            {
                if (++markers > 1024)
                    return new(Condition.Insufficient, null, null, 0, "The release comparison exceeds 1024 deployment markers.");
                (string Version, long At) marker = (row.GetString(0), row.GetInt64(1));
                if (releases.Count == 0)
                    releases.Add(marker);
                else if (releases[^1].Version == marker.Version)
                    releases[^1] = marker; // redeploying the same version continues its existing cohort
                else if (releases[^1].At == marker.At)
                    return new(Condition.Insufficient, null, null, 0, "Different release versions share a deployment timestamp.");
                else if (releases.Count == 2)
                    break;
                else
                    releases.Add(marker);
            }
        }
        if (releases.Count < 2)
            return new(Condition.Insufficient, null, null, 0, "Two distinct registered release versions are required.");
        var latest = releases[0];
        var previous = releases[1];
        var current = ReadMonitorWindow(db, transaction, p, Math.Max(since, latest.At), until, latest.Version);
        var reference = ReadMonitorWindow(db, transaction, p, Math.Max(previous.At, latest.At - p.ComparisonHours * HourMs),
            latest.At, previous.Version);
        if (current is null || reference is null)
            return ExceededWindow();
        if (current.Samples < p.MinimumSamples || reference.Samples < p.MinimumSamples)
            return new(Condition.Insufficient, current.FailureRate, reference.FailureRate, current.Samples,
                "Insufficient observations in one or both release cohorts.");
        bool failure = current.FailureRate - reference.FailureRate >= p.FailureIncreasePercent;
        bool hasLatency = current.Durations.Count >= p.MinimumSamples && reference.Durations.Count >= p.MinimumSamples;
        bool slower = false;
        if (p.LatencyIncreaseRatio > 0 && hasLatency)
        {
            double latency = current.Percentile(p.Percentile)!.Value;
            double baseline = reference.Percentile(p.Percentile)!.Value;
            slower = latency > baseline && latency >= baseline * p.LatencyIncreaseRatio &&
                latency - baseline >= p.MinimumLatencyIncreaseMs;
        }
        var condition = failure || slower ? Condition.Breach :
            p.LatencyIncreaseRatio > 0 && !hasLatency ? Condition.Insufficient : Condition.Healthy;
        return new(condition, current.FailureRate, reference.FailureRate, current.Samples,
            FormattableString.Invariant($"{latest.Version} versus {previous.Version}: failure rate {current.FailureRate:F1}% versus {reference.FailureRate:F1}%. ") +
            (slower ? $"The p{p.Percentile} latency increase exceeded the release threshold." :
                hasLatency || p.LatencyIncreaseRatio == 0 ? "" : "Insufficient measured release durations."));
    }

    private sealed class MonitorWindow
    {
        public long Samples { get; set; }
        public long Failures { get; set; }
        public double? FailureRate => Samples == 0 ? null : 100.0 * Failures / Samples;
        public List<double> Durations { get; } = [];

        public double? Percentile(int percentile)
        {
            if (Durations.Count == 0)
                return null;
            Durations.Sort();
            return Durations[(Durations.Count * percentile + 99) / 100 - 1];
        }
    }

    private static MonitorWindow? ReadMonitorWindow(SqliteConnection db, SqliteTransaction transaction, MonitorPolicy p,
        long since, long until, string? version)
    {
        using var command = MonitorCommand(db, transaction, p, $"""
            SELECT outcome, duration_ms FROM telemetry
            WHERE application=$app AND environment=$env AND kind=$kind AND occurred >= $since AND occurred < $until
                AND done IS NOT NULL AND anonymous=0 {(p.Target.Length == 0 ? "" : "AND name=$target")}
                {(version is null ? "" : "AND version=$version")} LIMIT $limit
            """, ("$since", since), ("$until", until), ("$version", version), ("$limit", MonitorSampleLimit + 1));
        var window = new MonitorWindow();
        using var rows = command.ExecuteReader();
        int records = 0;
        while (rows.Read())
        {
            if (++records > MonitorSampleLimit)
                return null;
            string outcome = rows.GetString(0);
            if (outcome is "success" or "failure" or "degraded")
                window.Samples++;
            if (outcome == "failure")
                window.Failures++;
            if (rows.NullableDouble(1) is double duration && (version is null || outcome != "cancelled"))
                window.Durations.Add(duration);
        }
        return window;
    }

    private static Measurement ExceededWindow() => new(Condition.Insufficient, null, null, null,
        $"The evaluation window exceeds the {MonitorSampleLimit} observation limit. Shorten the window or select a target.");

    private MonitorState ApplyMeasurement(SqliteConnection db, SqliteTransaction write, ScheduledPolicy entry,
        MonitorState? prior, Measurement measurement, long at)
    {
        var p = entry.Policy;
        var incident = ReadMonitorIncident(db, write, p);
        bool open = incident is { Status: not "Resolved" };
        long? healthy = null;
        string state = "InsufficientData";
        string detail = measurement.Detail;
        if (!p.Enabled)
            state = "Disabled";
        else if (measurement.Condition == Condition.Breach)
        {
            state = "Alert";
            if (!open)
            {
                using var closed = db.Command(write,
                    "SELECT MAX(at) FROM incident_history WHERE incident_id=$id AND status='Resolved'", ("$id", incident?.Id));
                if (closed.ExecuteScalar() is long resolved && at < resolved + p.CooldownMinutes * MinuteMs)
                    detail += " Incident reopening is in cooldown.";
                else
                    incident = TransitionMonitorIncident(db, write, p, incident, at, detail, resolved: false);
            }
        }
        else if (open && p.Enabled)
        {
            state = "Alert";
            if (measurement.Condition == Condition.Healthy)
            {
                long maxGap = Math.Max(p.WindowMinutes * MinuteMs, 2L * options.MonitorIntervalSeconds * 1000);
                healthy = prior?.Hash == entry.Hash && at - prior.Evaluated <= maxGap ? prior.HealthySince ?? at : at;
                if (at - healthy >= (long)p.RecoveryWindows * p.WindowMinutes * MinuteMs)
                {
                    incident = TransitionMonitorIncident(db, write, p, incident, at, detail, resolved: true);
                    state = "OK";
                    healthy = null;
                }
                else
                    detail += " Healthy observations are accumulating toward recovery.";
            }
            else
                detail += " The incident remains open pending healthy observations.";
        }
        else if (measurement.Condition is Condition.Healthy or Condition.BetweenThresholds)
            state = "OK";

        long since = prior?.State == state && prior.Hash == entry.Hash ? prior.Since : at;
        return new(state, since, at, healthy, measurement.Observed, measurement.Baseline, measurement.Samples,
            detail.TrimEnd(), entry.Hash, incident?.Id);
    }

    private static MonitorIncident? ReadMonitorIncident(SqliteConnection db, SqliteTransaction transaction, MonitorPolicy p)
    {
        using var command = MonitorCommand(db, transaction, p, """
            SELECT id,status,assignee FROM incidents WHERE application=$app AND environment=$env AND kind='monitor' AND name=$name
            """);
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetString(1), row.GetString(2)) : null;
    }

    private MonitorIncident TransitionMonitorIncident(SqliteConnection db, SqliteTransaction transaction, MonitorPolicy p,
        MonitorIncident? previous, long at, string evidence, bool resolved)
    {
        var id = previous?.Id ?? Guid.NewGuid().ToString("D");
        var status = resolved ? "Resolved" : previous is null ? "Open" : "Reopened";
        using var command = MonitorCommand(db, transaction, p, """
            INSERT INTO incidents(id,application,environment,kind,name,title,severity,status,first_seen,last_seen,occurrences,evidence)
            VALUES ($id,$app,$env,'monitor',$name,$title,$severity,$status,$at,$at,1,$evidence)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,severity=excluded.severity,title=excluded.title,
                last_seen=excluded.last_seen,occurrences=incidents.occurrences+$increment,evidence=excluded.evidence
            """, ("$id", id), ("$title", $"{p.Name} {p.Type} monitor"), ("$severity", p.Severity),
            ("$status", status), ("$at", at), ("$increment", resolved ? 0 : 1), ("$evidence", evidence));
        command.ExecuteNonQuery();
        WriteHistory(db, transaction, id, at, status, "PulseOps", evidence, previous?.Assignee ?? "");
        EnqueueAlerts(db, transaction, id, p.Application, p.Environment, p.Severity, status, at);
        return new(id, status, previous?.Assignee ?? "");
    }

    private static MonitorState? ReadMonitorState(SqliteConnection db, SqliteTransaction transaction, MonitorPolicy p)
    {
        using var command = MonitorCommand(db, transaction, p, """
            SELECT state,since,evaluated,healthy_since,observed,baseline,samples,detail,definition_hash,incident_id
            FROM monitor_state WHERE application=$app AND environment=$env AND name=$name
            """);
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetInt64(2), row.IsDBNull(3) ? null : row.GetInt64(3),
            row.NullableDouble(4), row.NullableDouble(5), row.IsDBNull(6) ? null : row.GetInt64(6), row.GetString(7),
            row.GetString(8), row.IsDBNull(9) ? null : row.GetString(9)) : null;
    }

    private static void StoreMonitorState(SqliteConnection db, SqliteTransaction transaction, MonitorPolicy p, MonitorState state)
    {
        using var command = MonitorCommand(db, transaction, p, """
            INSERT INTO monitor_state(application,environment,name,state,since,evaluated,healthy_since,observed,baseline,samples,
                detail,definition_hash,incident_id) VALUES ($app,$env,$name,$state,$since,$at,$healthy,$observed,$baseline,
                $samples,$detail,$hash,$incident)
            ON CONFLICT(application,environment,name) DO UPDATE SET state=excluded.state,since=excluded.since,
                evaluated=excluded.evaluated,healthy_since=excluded.healthy_since,observed=excluded.observed,
                baseline=excluded.baseline,samples=excluded.samples,detail=excluded.detail,definition_hash=excluded.definition_hash,
                incident_id=excluded.incident_id
            """, ("$state", state.State), ("$since", state.Since), ("$at", state.Evaluated), ("$healthy", state.HealthySince),
            ("$observed", state.Observed), ("$baseline", state.Baseline), ("$samples", state.Samples), ("$detail", state.Detail),
            ("$hash", state.Hash), ("$incident", state.IncidentId));
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset? Milliseconds(long? value) => value is long time ? DateTimeOffset.FromUnixTimeMilliseconds(time) : null;

    private static SqliteCommand MonitorCommand(SqliteConnection db, SqliteTransaction transaction, MonitorPolicy p,
        string sql, params (string Name, object? Value)[] args) =>
        db.Command(transaction, sql, [("$app", p.Application), ("$env", p.Environment), ("$name", p.Name),
            ("$kind", p.Kind), ("$target", p.Target), .. args]);
}

internal static class MonitorEndpoints
{
    public static void Map(RouteGroupBuilder api, PlatformOptions options) =>
        api.MapGet("/monitors", (HttpContext context, Storage storage, TimeProvider clock) =>
        {
            var filter = QueryFilter.From(context.Request, Access.Visible(context.User, options), clock.GetUtcNow());
            return Results.Ok(storage.MonitorStatuses(filter.Scopes));
        });
}

public sealed class MonitorWorker(Storage storage, PlatformOptions options, TimeProvider clock, ILogger<MonitorWorker> logger,
    IHubContext<LiveHub>? live = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (storage.EvaluateMonitors(clock.GetUtcNow(), stoppingToken) > 0 && live is not null)
                    await live.Clients.Group("platform").SendAsync("Changed", stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Scheduled monitor evaluation failed ({Type}).", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.MonitorIntervalSeconds), clock, stoppingToken);
        }
    }
}
