using Microsoft.Data.Sqlite;
using System.Globalization;

namespace PulseOps.Server;

internal sealed record QueryFilter(AppScope[] Scopes, long Since, long Until, string? Version, int Limit, int Offset,
    string? Kind, string? Name, string? SessionId, string? TraceId)
{
    public static QueryFilter From(HttpRequest request, AppScope[] visible, DateTimeOffset now)
    {
        var query = request.Query;
        var app = query["application"].ToString();
        var env = query["environment"].ToString();
        var hours = Number("hours", 24, 1, 336);
        return new(visible.Where(s => (app.Length == 0 || s.Application == app) &&
            (env.Length == 0 || s.Environment == env)).ToArray(),
            now.AddHours(-hours).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(),
            Text("version", 32), Number("limit", 100, 1, 200), Number("offset", 0, 0, 100_000),
            Text("kind", 16), Text("name", 64), Text("sessionId", 36), Text("traceId", 32));

        int Number(string key, int fallback, int min, int max) =>
            int.TryParse(query[key], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                ? Math.Clamp(value, min, max) : fallback;
        string? Text(string key, int max) => query[key].ToString() switch
        {
            "" => null,
            var value when value.Length > max => "(invalid)",
            var value => value
        };
    }

    public string ScopeClause => Scopes.Length == 0 ? "0" :
        "(application, environment) IN (VALUES " +
        string.Join(',', Scopes.Select((_, i) => $"($app{i},$env{i})")) + ")";

    public void Bind(SqliteCommand command)
    {
        for (var i = 0; i < Scopes.Length; i++)
        {
            command.Parameters.AddWithValue($"$app{i}", Scopes[i].Application);
            command.Parameters.AddWithValue($"$env{i}", Scopes[i].Environment);
        }
        foreach (var (key, value) in new (string, object?)[] {
            ("$since", Since), ("$until", Until), ("$version", Version), ("$limit", Limit), ("$offset", Offset),
            ("$kind", Kind), ("$name", Name), ("$session", SessionId), ("$trace", TraceId)
        })
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
    }

    public string TimeClause => "occurred BETWEEN $since AND $until AND " +
        (Version is null ? "1" : "version = $version");
}

internal sealed record IncidentRecord(string Id, string Application, string Environment, string Title, string Kind,
    string Name, string Severity, string Status, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    long Count, string Evidence, string Assignee);

public sealed partial class Storage
{
    private const string IncidentColumns = """
        id,application,environment,title,kind,name,severity,status,first_seen,last_seen,occurrences,evidence,assignee
        """;
    private static IncidentRecord Incident(SqliteDataReader row) => new(row.GetString(0), row.GetString(1),
        row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7),
        row.Time(8), row.Time(9), row.GetInt64(10), row.GetString(11), row.GetString(12));

    private static List<T> Read<T>(SqliteCommand command, Func<SqliteDataReader, T> map)
    {
        using var rows = command.ExecuteReader();
        var results = new List<T>();
        while (rows.Read())
            results.Add(map(rows));
        return results;
    }

    private static SqliteCommand Query(SqliteConnection connection, QueryFilter filter, string sql)
    {
        var command = connection.Command(null, sql);
        filter.Bind(command);
        return command;
    }

    internal object Overview(QueryFilter filter)
    {
        using var connection = Open();
        var eventScope = $"{filter.ScopeClause} AND {filter.TimeClause} AND done IS NOT NULL";
        using var total = Query(connection, filter, $"""
            SELECT COUNT(*) FILTER(WHERE kind='api'), COUNT(*) FILTER(WHERE kind='api' AND outcome='failure'),
                COUNT(DISTINCT (application || ':' || environment || ':' || session_id)) FILTER(WHERE kind != 'synthetic'),
                COUNT(*) FILTER(WHERE kind='error'), COUNT(*) FILTER(WHERE kind='startup' AND outcome='failure'),
                COUNT(*) FILTER(WHERE kind='api' AND outcome='success')
            FROM telemetry WHERE {eventScope}
            """);
        var counts = Read(total, row => new
        {
            requests = row.GetInt64(0), failedRequests = row.GetInt64(1), sessions = row.GetInt64(2),
            exceptions = row.GetInt64(3), startupFailures = row.GetInt64(4), successfulRequests = row.GetInt64(5)
        }).Single();

        using var global = Query(connection, filter, $"""
            WITH ranked AS (
                SELECT duration_ms, ROW_NUMBER() OVER(ORDER BY duration_ms) AS rn, COUNT(*) OVER() AS n
                FROM telemetry WHERE {eventScope} AND kind='api' AND duration_ms IS NOT NULL
            )
            SELECT MAX(duration_ms) FILTER(WHERE rn = (n*50+99)/100),
                MAX(duration_ms) FILTER(WHERE rn = (n*95+99)/100),
                MAX(duration_ms) FILTER(WHERE rn = (n*99+99)/100) FROM ranked
            """);
        var latency = Read(global, row => new { p50 = row.NullableDouble(0), p95 = row.NullableDouble(1), p99 = row.NullableDouble(2) }).Single();

        long step = filter.Until - filter.Since <= 3_600_000 ? 300_000 :
            filter.Until - filter.Since <= 86_400_000 ? 1_800_000 : 14_400_000;
        long start = filter.Since / step * step;
        using var timeline = Query(connection, filter, $"""
            WITH api AS (
                SELECT occurred/$step*$step AS bucket, duration_ms, outcome
                FROM telemetry WHERE {eventScope} AND kind='api'
            ), ranked AS (
                SELECT bucket, duration_ms, ROW_NUMBER() OVER(PARTITION BY bucket ORDER BY duration_ms) AS rn,
                    COUNT(*) OVER(PARTITION BY bucket) AS n FROM api WHERE duration_ms IS NOT NULL
            ), counts AS (
                SELECT bucket, COUNT(*) AS requests, SUM(outcome='failure') AS failures FROM api GROUP BY bucket
            )
            SELECT counts.bucket, counts.requests, counts.failures, ranked.duration_ms
            FROM counts LEFT JOIN ranked ON counts.bucket=ranked.bucket AND ranked.rn=(ranked.n*95+99)/100
            ORDER BY counts.bucket
            """);
        timeline.Parameters.AddWithValue("$step", step);
        var byBucket = Read(timeline, row => new
        {
            at = row.Time(0), requests = row.GetInt64(1), failures = row.GetInt64(2), p95 = row.NullableDouble(3)
        }).ToDictionary(point => point.at.ToUnixTimeMilliseconds());
        var points = new List<object>();
        for (var tick = start; tick <= filter.Until; tick += step)
            points.Add(byBucket.TryGetValue(tick, out var point) ? point :
                new { at = DateTimeOffset.FromUnixTimeMilliseconds(tick), requests = 0L, failures = 0L, p95 = (double?)null });

        using var service = Query(connection, filter, $"""
            WITH scoped AS (SELECT * FROM telemetry WHERE {eventScope}),
            totals AS (
                SELECT application, environment, COUNT(*) FILTER(WHERE kind='api') AS requests,
                    COUNT(*) FILTER(WHERE kind='api' AND outcome='failure') AS failures,
                    COUNT(DISTINCT session_id) FILTER(WHERE kind!='synthetic') AS sessions
                FROM scoped GROUP BY application,environment
            ), ranked AS (
                SELECT application,environment,duration_ms,
                    ROW_NUMBER() OVER(PARTITION BY application,environment ORDER BY duration_ms) AS rn,
                    COUNT(*) OVER(PARTITION BY application,environment) AS n
                FROM scoped WHERE kind='api' AND duration_ms IS NOT NULL
            )
            SELECT t.application,t.environment,t.requests,t.failures,t.sessions,r.duration_ms FROM totals t
            LEFT JOIN ranked r ON t.application=r.application AND t.environment=r.environment AND r.rn=(r.n*95+99)/100
            """);
        var serviceData = Read(service, row => new
        {
            application = row.GetString(0), environment = row.GetString(1), requests = row.GetInt64(2),
            failures = row.GetInt64(3), sessions = row.GetInt64(4), p95 = row.NullableDouble(5)
        }).ToDictionary(x => (x.application, x.environment));
        var services = filter.Scopes.Select(scope =>
        {
            var data = serviceData.GetValueOrDefault((scope.Application, scope.Environment));
            return new
            {
                scope.Application, scope.Environment, scope.Label, requests = data?.requests ?? 0,
                failures = data?.failures ?? 0, sessions = data?.sessions ?? 0, p95 = data?.p95
            };
        }).OrderByDescending(x => x.failures).ThenByDescending(x => x.requests).ToArray();

        using var incidentCount = Query(connection, filter, $"SELECT COUNT(*) FROM incidents WHERE {filter.ScopeClause} AND status != 'Resolved'");
        return new
        {
            counts.requests, counts.failedRequests, counts.successfulRequests, counts.sessions, counts.exceptions, counts.startupFailures,
            latency.p50, latency.p95, latency.p99, openIncidents = (long)incidentCount.ExecuteScalar()!,
            points, services, incidents = Incidents(filter with { Limit = 8, Offset = 0 }, active: true)
        };
    }

    internal List<IncidentRecord> Incidents(QueryFilter filter, bool active = false)
    {
        using var connection = Open();
        using var command = Query(connection, filter, $"""
            SELECT {IncidentColumns} FROM incidents WHERE {filter.ScopeClause}
                AND (status != 'Resolved' OR (last_seen BETWEEN $since AND $until AND $active=0))
            ORDER BY CASE WHEN status='Resolved' THEN 1 ELSE 0 END, last_seen DESC, id
            LIMIT $limit OFFSET $offset
            """);
        command.Parameters.AddWithValue("$active", active);
        return Read(command, Incident);
    }

    internal object? IncidentDetails(QueryFilter filter, string id)
    {
        using var connection = Open();
        using var select = Query(connection, filter, $"SELECT {IncidentColumns} FROM incidents WHERE id=$id AND {filter.ScopeClause}");
        select.Parameters.AddWithValue("$id", id);
        var incident = Read(select, Incident).SingleOrDefault();
        if (incident is null)
            return null;
        using var history = connection.Command(null, """
            SELECT at,status,actor,note,assignee FROM incident_history WHERE incident_id=$id
            ORDER BY row_id DESC LIMIT 200
            """, ("$id", id));
        return new
        {
            incident, history = Read(history, row => new
            {
                at = row.Time(0), status = row.GetString(1), actor = row.GetString(2),
                note = row.GetString(3), assignee = row.GetString(4)
            })
        };
    }

    internal int ChangeIncident(QueryFilter filter, string id, IncidentChange change, string actor, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var select = Query(connection, filter, $"SELECT {IncidentColumns} FROM incidents WHERE id=$id AND {filter.ScopeClause}");
        select.Transaction = transaction;
        select.Parameters.AddWithValue("$id", id);
        var incident = Read(select, Incident).SingleOrDefault();
        if (incident is null)
            return 404;
        var status = string.IsNullOrWhiteSpace(change.Status) ? incident.Status : change.Status;
        bool allowed = status == incident.Status || (incident.Status, status) switch
        {
            ("Resolved", "Reopened") => true,
            ("Resolved", _) => false,
            (_, "Acknowledged" or "Investigating" or "Mitigated" or "Resolved") => true,
            _ => false
        };
        if (!allowed)
            return 409;
        var assignee = change.Assignee?.Trim() ?? incident.Assignee;
        using var update = connection.Command(transaction, "UPDATE incidents SET status=$status, assignee=$assignee WHERE id=$id",
            ("$status", status), ("$assignee", assignee), ("$id", id));
        update.ExecuteNonQuery();
        WriteHistory(connection, transaction, id, now.ToUnixTimeMilliseconds(), status, actor, change.Note?.Trim() ?? "", assignee);
        // The audit entry records the transition and whether a note or assignee changed, never the note or assignee text.
        WriteAudit(connection, transaction, now.ToUnixTimeMilliseconds(), actor, "incident.updated", incident.Application,
            incident.Environment, id, AuditDetail(new
            {
                from = incident.Status, to = status, assigneeChanged = assignee != incident.Assignee,
                noteProvided = !string.IsNullOrWhiteSpace(change.Note)
            }));
        if (status != incident.Status && status is "Resolved" or "Reopened")
            EnqueueAlerts(connection, transaction, id, incident.Application, incident.Environment, incident.Severity,
                status, now.ToUnixTimeMilliseconds());
        transaction.Commit();
        return 204;
    }

    internal object Events(QueryFilter filter, EventSearch search)
    {
        using var connection = Open();
        using var command = Query(connection, filter, $"""
            SELECT id, occurred, application, environment, version, session_id, kind, name, outcome, duration_ms,
                status_code, trace_id, span_id, parent_span_id, category, value, anonymous
            FROM telemetry WHERE {filter.ScopeClause} AND {filter.TimeClause} AND done IS NOT NULL
              AND {(filter.SessionId is null ? "1" : "session_id=$session")}
              AND {(filter.TraceId is null ? "1" : "trace_id=$trace")}
              AND {(filter.Kind is null ? "1" : "kind=$kind")}
              AND {(filter.Name is null ? "1" : "name=$name")} AND ({search.Sql})
            ORDER BY occurred DESC, row_id DESC LIMIT $limit OFFSET $offset
            """);
        foreach (var (name, value) in search.Parameters)
            command.Parameters.AddWithValue(name, value);
        return Read(command, row => new
        {
            id = row.GetString(0), occurredAt = row.Time(1), application = row.GetString(2), environment = row.GetString(3),
            version = row.GetString(4), sessionId = row.GetString(5), kind = row.GetString(6), name = row.GetString(7),
            outcome = row.GetString(8), durationMs = row.NullableDouble(9),
            statusCode = row.IsDBNull(10) ? (int?)null : row.GetInt32(10), traceId = row.NullableString(11),
            spanId = row.NullableString(12), parentSpanId = row.NullableString(13),
            category = row.NullableString(14), value = row.NullableDouble(15), anonymous = row.GetBoolean(16)
        });
    }

    internal object Sessions(QueryFilter filter)
    {
        using var connection = Open();
        using var command = Query(connection, filter, $"""
            SELECT session_id, application, environment, MAX(version), MIN(occurred), MAX(occurred), COUNT(*),
                SUM(outcome='failure'), SUM(outcome='failure' AND kind IN ('startup','error')),
                MAX(occurred) FILTER (WHERE kind='session' AND name='offline'),
                MAX(occurred) FILTER (WHERE kind='session' AND name='online'),
                SUM(outcome='degraded')
            FROM telemetry WHERE {filter.ScopeClause} AND {filter.TimeClause} AND done IS NOT NULL AND kind != 'synthetic'
                AND {(filter.SessionId is null ? "1" : "session_id=$session")}
            GROUP BY application, environment, session_id ORDER BY MAX(occurred) DESC, session_id LIMIT $limit OFFSET $offset
            """);
        return Read(command, row => new
        {
            sessionId = row.GetString(0), application = row.GetString(1), environment = row.GetString(2),
            version = row.GetString(3), firstSeen = row.Time(4), lastSeen = row.Time(5), eventCount = row.GetInt64(6),
            failures = row.GetInt64(7), health = row.GetInt64(8) > 0 ? "Failed" :
                row.NullableTime(9) is { } offline && (row.NullableTime(10) ?? DateTimeOffset.MinValue) < offline ? "Disconnected" :
                row.GetInt64(7) + row.GetInt64(11) > 0 ? "Degraded" : "Healthy"
        });
    }

    internal object Dependencies(QueryFilter filter)
    {
        using var connection = Open();
        using var command = Query(connection, filter, $"""
            WITH api AS (
                SELECT name, duration_ms, outcome FROM telemetry WHERE done IS NOT NULL AND kind='api'
                    AND {filter.ScopeClause} AND {filter.TimeClause} AND {(filter.Name is null ? "1" : "name=$name")}
            ), ranked AS (
                SELECT name, duration_ms, ROW_NUMBER() OVER(PARTITION BY name ORDER BY duration_ms) AS rn,
                    COUNT(*) OVER(PARTITION BY name) AS n FROM api WHERE duration_ms IS NOT NULL
            ), latency AS (
                SELECT name, MAX(duration_ms) FILTER(WHERE rn=(n*50+99)/100) AS p50,
                    MAX(duration_ms) FILTER(WHERE rn=(n*95+99)/100) AS p95,
                    MAX(duration_ms) FILTER(WHERE rn=(n*99+99)/100) AS p99 FROM ranked GROUP BY name
            ), counts AS (
                SELECT name,COUNT(*) AS requests,SUM(outcome='failure') AS failures FROM api GROUP BY name
            )
            SELECT counts.name,requests,failures,p50,p95,p99 FROM counts
            LEFT JOIN latency ON latency.name=counts.name ORDER BY failures DESC, requests DESC, counts.name
            LIMIT $limit OFFSET $offset
            """);
        return Read(command, row => new
        {
            name = row.GetString(0), requests = row.GetInt64(1), failures = row.GetInt64(2),
            p50 = row.NullableDouble(3), p95 = row.NullableDouble(4), p99 = row.NullableDouble(5)
        });
    }

    internal object Releases(QueryFilter filter)
    {
        using var connection = Open();
        using var command = Query(connection, filter, $"""
            SELECT application,environment,version,build_id,commit_id,deployed FROM releases
            WHERE {filter.ScopeClause} AND deployed BETWEEN $since AND $until AND {(filter.Version is null ? "1" : "version=$version")}
            ORDER BY deployed DESC, application, environment, build_id LIMIT $limit OFFSET $offset
            """);
        return Read(command, row => new
        {
            application = row.GetString(0), environment = row.GetString(1), version = row.GetString(2),
            buildId = row.GetString(3), commitId = row.GetString(4), deployedAt = row.Time(5)
        });
    }
}
