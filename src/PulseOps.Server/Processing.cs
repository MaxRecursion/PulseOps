using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.SignalR;
using System.Diagnostics;

namespace PulseOps.Server;

public sealed record DeadLetterReplayResult(int Returned, int Duplicates, int Deferred, long Remaining, bool PausedForDisk);

public sealed partial class Storage
{
    public const int BatchSize = 128;
    public const int RawRetentionBatch = 5_000;
    private const string PendingColumns =
        "row_id, application, environment, kind, name, outcome, received, anonymous, occurred, version, duration_ms, id";
    private const int QuarantineLimit = 20;
    private static readonly TimeSpan QuarantineWindow = TimeSpan.FromMinutes(10);
    private readonly Queue<long> _quarantines = new();
    private long _quarantined;

    /// <summary>Records moved to dead letters by this instance since it started.</summary>
    public long Quarantined => Interlocked.Read(ref _quarantined);

    /// <summary>Test seam invoked for each record before processing, used to prove fault isolation.</summary>
    internal Action<long>? RecordHook { get; set; }

    /// <summary>
    /// Processes one bounded batch atomically. When a record-specific fault aborts the batch, its records are retried
    /// one at a time and each record that still faults is moved to dead_letters (replayable), so a single defective
    /// record can neither stall the inbox nor stop the worker. A circuit breaker stops quarantining when faults look
    /// systemic, leaving the backlog intact and the readiness heartbeat stale for operators.
    /// </summary>
    public int Process(DateTimeOffset now)
    {
        List<PendingItem> batch = [];
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            batch = SelectPending(connection, transaction);
            ProcessItems(connection, transaction, batch, now);
            transaction.Commit();
            return batch.Count;
        }
        catch (Exception exception) when (batch.Count > 0 && IsRecordFault(exception))
        {
            return ProcessIndividually(batch, now);
        }
    }

    internal static bool IsRecordFault(Exception exception) => exception is not (SqliteException or IOException or
        OperationCanceledException or OutOfMemoryException or ObjectDisposedException or UnauthorizedAccessException);

    private int ProcessIndividually(List<PendingItem> batch, DateTimeOffset now)
    {
        int handled = 0;
        foreach (var candidate in batch)
        {
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                // Another worker may have completed the record after the failed batch rolled back.
                var single = SelectPending(connection, transaction, candidate);
                if (single.Count == 0)
                    continue;
                ProcessItems(connection, transaction, single, now);
                transaction.Commit();
                handled++;
            }
            catch (Exception exception) when (IsRecordFault(exception))
            {
                if (Quarantine(candidate, exception.GetType().Name, now))
                    handled++;
            }
        }
        return handled;
    }

    private bool Quarantine(PendingItem candidate, string reason, DateTimeOffset now)
    {
        long timestamp = now.ToUnixTimeMilliseconds();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        // A second worker can finish this receipt between the isolated retry and this transaction.
        // Match its scoped event identity as SQLite may have already reused the integer key.
        using var move = connection.Command(transaction, """
            INSERT INTO dead_letters(application, environment, id, occurred, received, version, session_id, kind, name,
                outcome, duration_ms, status_code, trace_id, span_id, parent_span_id, category, value, anonymous, reason, quarantined)
            SELECT application, environment, id, occurred, received, version, session_id, kind, name,
                outcome, duration_ms, status_code, trace_id, span_id, parent_span_id, category, value, anonymous, $reason, $at
            FROM telemetry WHERE row_id = $row AND application = $app AND environment = $env AND id = $id AND done IS NULL
            """, ("$row", candidate.RowId), ("$app", candidate.Application), ("$env", candidate.Environment),
            ("$id", candidate.EventId), ("$reason", reason.Length <= 120 ? reason : reason[..120]), ("$at", timestamp));
        if (move.ExecuteNonQuery() == 0)
            return false;

        using var remove = connection.Command(transaction, "DELETE FROM telemetry WHERE row_id = $row", ("$row", candidate.RowId));
        remove.ExecuteNonQuery();
        lock (_quarantines)
        {
            while (_quarantines.Count > 0 && _quarantines.Peek() < timestamp - (long)QuarantineWindow.TotalMilliseconds)
                _quarantines.Dequeue();
            if (_quarantines.Count >= QuarantineLimit)
                throw new InvalidOperationException("Dead-letter circuit open: processing faults look systemic.");
            transaction.Commit();
            _quarantines.Enqueue(timestamp);
        }
        Interlocked.Increment(ref _quarantined);
        return true;
    }

    /// <summary>
    /// Returns quarantined records to the inbox in bounded slices. Capacity- or disk-deferred records remain quarantined.
    /// </summary>
    public DeadLetterReplayResult ReplayDeadLetters(int maximum = 10_000)
    {
        const int sliceSize = 128;
        int limit = Math.Clamp(maximum, 1, 100_000);
        int returned = 0;
        int duplicates = 0;
        int deferred = 0;
        int examined = 0;
        long cursor = 0;
        bool pausedForDisk = false;
        int inboxMaximum = ReplayInboxMaximum(options.InboxMaximum);
        int scopeMaximum = ReplayInboxMaximum(ScopeInboxMaximum(options.InboxMaximum));

        while (examined < limit)
        {
            if (!HasDiskReserve())
            {
                pausedForDisk = true;
                break;
            }

            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var select = connection.Command(transaction, """
                SELECT row_id,application,environment,id FROM dead_letters
                WHERE row_id > $after ORDER BY row_id LIMIT $limit
                """, ("$after", cursor), ("$limit", Math.Min(sliceSize, limit - examined)));
            var rows = Read(select, row => (RowId: row.GetInt64(0), Application: row.GetString(1),
                Environment: row.GetString(2), EventId: row.GetString(3)));
            if (rows.Count == 0)
            {
                transaction.Commit();
                break;
            }

            var (total, _) = PendingCounts(connection, transaction, rows[0].Application, rows[0].Environment);
            var scopedCounts = new Dictionary<(string Application, string Environment), int>();
            foreach (var row in rows)
            {
                cursor = row.RowId;
                examined++;
                var scope = (row.Application, row.Environment);
                if (!scopedCounts.TryGetValue(scope, out var scoped))
                {
                    (_, scoped) = PendingCounts(connection, transaction, row.Application, row.Environment);
                    scopedCounts.Add(scope, scoped);
                }

                using var exists = connection.Command(transaction, """
                    SELECT EXISTS(SELECT 1 FROM telemetry WHERE application=$app AND environment=$env AND id=$id)
                    """, ("$app", row.Application), ("$env", row.Environment), ("$id", row.EventId));
                if ((long)exists.ExecuteScalar()! != 0)
                {
                    DeleteDeadLetter(connection, transaction, row.RowId);
                    duplicates++;
                    continue;
                }

                if (total >= inboxMaximum || scoped >= scopeMaximum)
                {
                    deferred++;
                    continue;
                }

                using var insert = connection.Command(transaction, """
                    INSERT INTO telemetry(application, environment, id, occurred, received, version, session_id, kind, name,
                        outcome, duration_ms, status_code, trace_id, span_id, parent_span_id, category, value, anonymous)
                    SELECT application, environment, id, occurred, received, version, session_id, kind, name,
                        outcome, duration_ms, status_code, trace_id, span_id, parent_span_id, category, value, anonymous
                    FROM dead_letters WHERE row_id = $id
                    ON CONFLICT(application, environment, id) DO NOTHING
                    """, ("$id", row.RowId));
                if (insert.ExecuteNonQuery() == 0)
                {
                    DeleteDeadLetter(connection, transaction, row.RowId);
                    duplicates++;
                    continue;
                }

                DeleteDeadLetter(connection, transaction, row.RowId);
                total++;
                scopedCounts[scope] = scoped + 1;
                returned++;
            }
            transaction.Commit();
            if (rows.Count < sliceSize)
                break;
        }

        using var countConnection = Open();
        using var remainingQuery = countConnection.Command(null, "SELECT COUNT(*) FROM dead_letters");
        long remaining = (long)remainingQuery.ExecuteScalar()!;
        return new(returned, duplicates, deferred, remaining, pausedForDisk);
    }

    private static void DeleteDeadLetter(SqliteConnection connection, SqliteTransaction transaction, long rowId)
    {
        using var remove = connection.Command(transaction, "DELETE FROM dead_letters WHERE row_id = $id", ("$id", rowId));
        remove.ExecuteNonQuery();
    }

    private static PendingItem ReadPending(SqliteDataReader row) => new(row.GetInt64(0), row.GetString(1), row.GetString(2),
        row.GetString(3), row.GetString(4), row.GetString(5), row.GetInt64(6), row.GetBoolean(7), row.GetInt64(8),
        row.GetString(9), row.NullableDouble(10), row.GetString(11));

    private static List<PendingItem> SelectPending(SqliteConnection connection, SqliteTransaction transaction, PendingItem? candidate = null)
    {
        if (candidate is not null)
        {
            using var single = connection.Command(transaction, $"""
                SELECT {PendingColumns} FROM telemetry
                WHERE row_id = $row AND application = $app AND environment = $env AND id = $id AND done IS NULL
                """, ("$row", candidate.RowId), ("$app", candidate.Application), ("$env", candidate.Environment), ("$id", candidate.EventId));
            return Read(single, ReadPending);
        }

        // Walk the pending_scope index from the last committed batch. A tuple seek skips the
        // entire preceding scope, even when it has a large backlog. Wrap at most once.
        using var cursor = connection.Command(transaction, "SELECT application, environment FROM processing_cursor WHERE id = 1");
        var start = Read(cursor, row => (Application: row.GetString(0), Environment: row.GetString(1))).Single();
        var scopes = new List<(string Application, string Environment)>();
        using var next = connection.Command(transaction, """
            SELECT application, environment FROM telemetry
            WHERE done IS NULL AND (application, environment) > ($app, $env)
            ORDER BY application, environment LIMIT 1
            """, ("$app", ""), ("$env", ""));
        var position = start;
        bool wrapped = false;
        while (scopes.Count < BatchSize)
        {
            next.Parameters["$app"].Value = position.Application;
            next.Parameters["$env"].Value = position.Environment;
            using var row = next.ExecuteReader();
            if (!row.Read())
            {
                if (wrapped || start.Application.Length == 0)
                    break;
                wrapped = true;
                position = ("", "");
                continue;
            }
            position = (row.GetString(0), row.GetString(1));
            if (wrapped && (string.CompareOrdinal(position.Application, start.Application) > 0 ||
                (position.Application == start.Application && string.CompareOrdinal(position.Environment, start.Environment) > 0)))
                break;
            scopes.Add(position);
        }
        if (scopes.Count == 0)
            return [];
        if (scopes[^1] != start)
        {
            using var advance = connection.Command(transaction,
                "UPDATE processing_cursor SET application = $app, environment = $env WHERE id = 1",
                ("$app", scopes[^1].Application), ("$env", scopes[^1].Environment));
            advance.ExecuteNonQuery();
        }
        if (scopes.Count == 1)
        {
            using var fifo = connection.Command(transaction,
                $"SELECT {PendingColumns} FROM telemetry WHERE done IS NULL ORDER BY row_id LIMIT $limit", ("$limit", BatchSize));
            return Read(fifo, ReadPending);
        }

        var items = new List<PendingItem>(BatchSize);
        var after = new Dictionary<(string, string), long>();
        using var take = connection.Command(transaction, $"""
            SELECT {PendingColumns} FROM telemetry
            WHERE application = $app AND environment = $env AND done IS NULL AND row_id > $after
            ORDER BY row_id LIMIT $limit
            """, ("$app", ""), ("$env", ""), ("$after", 0L), ("$limit", 0));
        var active = scopes;
        for (int round = 0; round < 4 && items.Count < BatchSize && active.Count > 0; round++)
        {
            int share = Math.Max(1, (BatchSize - items.Count + active.Count - 1) / active.Count);
            var remaining = new List<(string, string)>();
            foreach (var scope in active)
            {
                int quota = Math.Min(share, BatchSize - items.Count);
                if (quota <= 0)
                    break;
                take.Parameters["$app"].Value = scope.Item1;
                take.Parameters["$env"].Value = scope.Item2;
                take.Parameters["$after"].Value = after.GetValueOrDefault(scope);
                take.Parameters["$limit"].Value = quota;
                var rows = Read(take, ReadPending);
                items.AddRange(rows);
                if (rows.Count > 0)
                    after[scope] = rows[^1].RowId;
                if (rows.Count == quota)
                    remaining.Add(scope);
            }
            active = remaining;
        }
        items.Sort((left, right) => left.RowId.CompareTo(right.RowId));
        return items;
    }

    private void ProcessItems(SqliteConnection connection, SqliteTransaction transaction, List<PendingItem> items, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeMilliseconds();
        var recoverableKinds = options.Rules.Where(rule => rule.RecoveryPercent is not null)
            .Select(rule => rule.Kind).ToHashSet();
        foreach (var item in items)
        {
            RecordHook?.Invoke(item.RowId);
            using var complete = connection.Command(transaction, "UPDATE telemetry SET done = $at WHERE row_id = $id",
                ("$at", timestamp), ("$id", item.RowId));
            complete.ExecuteNonQuery();
            if (!item.Anonymous && (item.Outcome == "failure" ||
                (item.Outcome == "success" && recoverableKinds.Contains(item.Kind))))
                Detect(connection, transaction, item, now);
        }
        // Rollups and signal activity commit with the receipt state, so each event is aggregated exactly once.
        if (items.Count > 0)
            ApplyRollups(connection, transaction, items);
        using var heartbeat = connection.Command(transaction, """
            INSERT INTO worker_state VALUES (1,$at,$count)
            ON CONFLICT(id) DO UPDATE SET heartbeat = excluded.heartbeat, processed = processed + excluded.processed
            """, ("$at", timestamp), ("$count", items.Count));
        heartbeat.ExecuteNonQuery();
    }

    private void Detect(SqliteConnection connection, SqliteTransaction transaction, PendingItem item, DateTimeOffset now)
    {
        var rule = options.Rules.Where(r => r.Kind == item.Kind &&
            (r.Application == "" || r.Application == item.Application) &&
            (r.Environment == "" || r.Environment == item.Environment) &&
            (r.Name == "" || r.Name == item.Name))
            .OrderByDescending(r => (r.Application == "" ? 0 : 4) + (r.Environment == "" ? 0 : 2) + (r.Name == "" ? 0 : 1))
            .FirstOrDefault() ?? DefaultRule(item.Kind);
        if (rule is null || (item.Outcome == "success" && rule.RecoveryPercent is null))
            return;

        using var lookup = connection.Command(transaction, """
            SELECT id,status,assignee FROM incidents WHERE application=$app AND environment=$env AND kind=$kind AND name=$name
            """, ("$app", item.Application), ("$env", item.Environment), ("$kind", item.Kind), ("$name", item.Name));
        var existing = Read(lookup, r => (Id: r.GetString(0), Status: r.GetString(1), Assignee: r.GetString(2))).SingleOrDefault();

        if (item.Outcome == "success")
        {
            if (existing.Id is not null && existing.Status != "Resolved" && rule.RecoveryPercent is double threshold)
                TryRecover(connection, transaction, item, rule, existing.Id, existing.Assignee, threshold, now);
            return;
        }

        long timestamp = now.ToUnixTimeMilliseconds();
        long window = TimeSpan.FromMinutes(rule.WindowMinutes).Ticks / TimeSpan.TicksPerMillisecond;
        long since = item.Received - window;
        using var count = connection.Command(transaction, """
            SELECT COUNT(*), COALESCE(SUM(outcome = 'failure'),0)
            FROM telemetry
            WHERE application = $app AND environment = $env AND kind = $kind AND name = $name
                AND done IS NOT NULL AND anonymous = 0 AND received >= $since AND received <= $until
                AND outcome IN ('success','failure','degraded')
            """, ("$app", item.Application), ("$env", item.Environment), ("$kind", item.Kind),
            ("$name", item.Name), ("$since", since), ("$until", item.Received));
        var (samples, failures) = Read(count, row => (row.GetInt64(0), row.GetInt64(1))).Single();
        var reached = samples >= rule.MinimumSamples && failures >= rule.MinimumFailures &&
            failures * 100.0 / Math.Max(1, samples) >= rule.FailurePercent;
        long evaluationDelay = Math.Max(0, timestamp - item.Received);
        bool historical = evaluationDelay > window;
        string evidence = FormattableString.Invariant(
            $"{failures:N0} failures in {samples:N0} observations in the {rule.WindowMinutes}-minute window ending {DateTimeOffset.FromUnixTimeMilliseconds(item.Received):O}.") +
            (historical
                ? FormattableString.Invariant($" Historical evaluation completed {TimeSpan.FromMilliseconds(evaluationDelay)} after receipt; notification was suppressed.")
                : "") +
            (item.Kind == "api" ? " The caller observed the failures. Inspect the dependency and its correlation records." :
             " Inspect correlated events in the affected application scope.");
        if (existing.Id is not null && existing.Status != "Resolved")
        {
            using var update = connection.Command(transaction, """
                UPDATE incidents SET first_seen = MIN(first_seen,$at), last_seen = MAX(last_seen,$at),
                    occurrences = occurrences+1, evidence = CASE WHEN $reached THEN $evidence ELSE evidence END
                WHERE id=$id
                """, ("$at", item.Occurred), ("$reached", reached), ("$evidence", evidence), ("$id", existing.Id));
            update.ExecuteNonQuery();
            return;
        }
        if (!reached)
            return;

        var id = existing.Id ?? Guid.NewGuid().ToString("D");
        var state = existing.Id is null ? "Open" : "Reopened";
        if (existing.Id is not null)
        {
            using var resolved = connection.Command(transaction, """
                SELECT MAX(at) FROM incident_history WHERE incident_id=$id AND status='Resolved'
                """, ("$id", id));
            if (resolved.ExecuteScalar() is long closedAt && (item.Received <= closedAt ||
                timestamp - closedAt < TimeSpan.FromMinutes(rule.WindowMinutes).TotalMilliseconds))
                return;
        }
        using var upsert = connection.Command(transaction, """
            INSERT INTO incidents(id,application,environment,kind,name,title,severity,status,first_seen,last_seen,occurrences,evidence)
            VALUES($id,$app,$env,$kind,$name,$title,$severity,$state,$at,$at,$count,$evidence)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status, severity=excluded.severity,
                last_seen=MAX(last_seen,excluded.last_seen), occurrences=occurrences+excluded.occurrences, evidence=excluded.evidence
            """, ("$id", id), ("$app", item.Application), ("$env", item.Environment), ("$kind", item.Kind),
            ("$name", item.Name), ("$title", IncidentTitle(item.Kind, item.Name)), ("$severity", rule.Severity),
            ("$state", state), ("$at", item.Occurred), ("$count", failures), ("$evidence", evidence));
        upsert.ExecuteNonQuery();
        WriteHistory(connection, transaction, id, timestamp, state, "PulseOps", evidence, "");
        if (!historical)
            EnqueueAlerts(connection, transaction, id, item.Application, item.Environment, rule.Severity, state, timestamp);
    }

    // Recovery thresholds adapt DataCat's stateFor in artifacts/api-server/src/jobs/monitors.ts
    // at c016efb70de792f5beeb7b2d1266830087910343 (Copyright 2026 The DataCat authors, Apache-2.0).
    // PulseOps requires consecutive, fully observed windows after the active incident was opened.
    private void TryRecover(SqliteConnection connection, SqliteTransaction transaction, PendingItem item,
        DetectionRule rule, string id, string assignee, double threshold, DateTimeOffset now)
    {
        long interval = rule.WindowMinutes * 60_000L;
        long since = item.Received - interval * rule.RecoveryWindows;
        if (item.Received < now.AddMinutes(-rule.WindowMinutes).ToUnixTimeMilliseconds())
            return;

        using var opened = connection.Command(transaction, """
            SELECT MAX(at) FROM incident_history WHERE incident_id=$id AND status IN ('Open','Reopened')
            """, ("$id", id));
        if (opened.ExecuteScalar() is not long started || since < started)
            return;

        using var windows = connection.Command(transaction, """
            SELECT ($until-received)/$interval AS bucket, COUNT(*), SUM(outcome='failure')
            FROM telemetry
            WHERE application=$app AND environment=$env AND kind=$kind AND name=$name AND anonymous=0
                AND done IS NOT NULL AND received > $since AND received <= $until
                AND outcome IN ('success','failure','degraded')
            GROUP BY bucket
            """, ("$until", item.Received), ("$interval", interval), ("$since", since), ("$app", item.Application),
            ("$env", item.Environment), ("$kind", item.Kind), ("$name", item.Name));
        var counts = Read(windows, r => (Samples: r.GetInt64(1), Failures: r.GetInt64(2)));
        if (counts.Count != rule.RecoveryWindows ||
            counts.Any(c => c.Samples < rule.MinimumSamples || c.Failures * 100.0 / c.Samples > threshold))
            return;

        string evidence = FormattableString.Invariant(
            $"Recovered at or below {threshold:G}% failure for {rule.RecoveryWindows} consecutive {rule.WindowMinutes}-minute windows.");
        using var resolve = connection.Command(transaction,
            "UPDATE incidents SET status='Resolved', evidence=$evidence WHERE id=$id",
            ("$evidence", evidence), ("$id", id));
        resolve.ExecuteNonQuery();
        long timestamp = now.ToUnixTimeMilliseconds();
        WriteHistory(connection, transaction, id, timestamp, "Resolved", "PulseOps", evidence, assignee);
        EnqueueAlerts(connection, transaction, id, item.Application, item.Environment, rule.Severity, "Resolved", timestamp);
    }

    private static string IncidentTitle(string kind, string name)
    {
        var subject = name.Replace('-', ' ');
        return kind switch
        {
            "api" => $"{subject} dependency failures",
            "startup" => $"{subject} startup failures",
            "error" => $"Repeated {subject}",
            "synthetic" => $"{subject} availability check failed",
            _ => $"{subject} failed"
        };
    }

    private static DetectionRule? DefaultRule(string kind) => kind switch
    {
        "api" => new(),
        "startup" or "error" => new() { Kind = kind, MinimumSamples = 2, MinimumFailures = 2, FailurePercent = 100 },
        "operation" => new() { Kind = kind, MinimumSamples = 5, MinimumFailures = 3, FailurePercent = 30 },
        "synthetic" => new() { Kind = kind, MinimumSamples = 2, MinimumFailures = 2, FailurePercent = 100 },
        _ => null
    };

    // Raw telemetry retention by data class: exception events (kind "error") use ErrorRetentionDays, everything else
    // RetentionDays. Oldest expired records across kinds are removed first, with a bounded amount per pass.
    public int Retain(DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var (backfillCursor, backfillUntil) = BackfillRange(connection, transaction);
        using (var expire = connection.Command(transaction, """
                   DELETE FROM telemetry WHERE row_id IN (
                       SELECT row_id FROM telemetry WHERE done IS NOT NULL
                         AND ((kind = 'error' AND received < $errorCutoff) OR
                              (kind <> 'error' AND received < $rawCutoff))
                         AND (row_id <= $cursor OR row_id > $until)
                       ORDER BY received,row_id LIMIT $limit)
                   """, ("$rawCutoff", now.AddDays(-options.RetentionDays).ToUnixTimeMilliseconds()),
            ("$errorCutoff", now.AddDays(-options.ErrorRetentionDays).ToUnixTimeMilliseconds()),
            ("$limit", RawRetentionBatch), ("$cursor", backfillCursor), ("$until", backfillUntil)))
        {
            var removed = expire.ExecuteNonQuery();
            using var incidents = connection.Command(transaction, """
                DELETE FROM incidents WHERE id IN (
                    SELECT id FROM incidents WHERE last_seen < $cutoff AND status='Resolved'
                      AND NOT EXISTS (SELECT 1 FROM incident_history WHERE incident_id=incidents.id AND at >= $cutoff)
                    LIMIT 100)
                """, ("$cutoff", now.AddDays(-options.IncidentRetentionDays).ToUnixTimeMilliseconds()));
            incidents.ExecuteNonQuery();
            using var releases = connection.Command(transaction, """
                DELETE FROM releases WHERE rowid IN (SELECT rowid FROM releases WHERE deployed < $cutoff LIMIT 1000)
                """, ("$cutoff", now.AddDays(-options.IncidentRetentionDays).ToUnixTimeMilliseconds()));
            releases.ExecuteNonQuery();
            using var deadLetters = connection.Command(transaction, """
                DELETE FROM dead_letters WHERE row_id IN (
                    SELECT row_id FROM dead_letters WHERE quarantined < $cutoff ORDER BY quarantined LIMIT 1000)
                """, ("$cutoff", now.AddDays(-options.RetentionDays).ToUnixTimeMilliseconds()));
            deadLetters.ExecuteNonQuery();
            RetainRollups(connection, transaction, now);
            ExpireChanges(connection, transaction, now);
            transaction.Commit();
            if (removed > 0)
            {
                using var checkpoint = connection.Command(null, "PRAGMA wal_checkpoint(PASSIVE)");
                checkpoint.ExecuteNonQuery();
            }
            return removed;
        }
    }

    private static void WriteHistory(SqliteConnection connection, SqliteTransaction transaction, string id,
        long timestamp, string status, string actor, string note, string assignee)
    {
        using var command = connection.Command(transaction, """
            INSERT INTO incident_history(incident_id,at,status,actor,note,assignee) VALUES ($id,$at,$status,$actor,$note,$assignee)
            """, ("$id", id), ("$at", timestamp), ("$status", status), ("$actor", actor),
            ("$note", note), ("$assignee", assignee));
        command.ExecuteNonQuery();
    }

    private sealed record PendingItem(long RowId, string Application, string Environment, string Kind, string Name,
        string Outcome, long Received, bool Anonymous, long Occurred, string Version, double? DurationMs, string EventId);
}

public sealed class Processor(Storage storage, TimeProvider clock, ILogger<Processor> logger,
    IHubContext<LiveHub>? live = null) : BackgroundService
{
    // Each step is isolated: a fault in backfill or retention never stops intake processing, and
    // no fault ends the host (which would take ingestion down with the embedded worker). Logs carry exception types
    // and SQLite codes only, never messages that could echo data.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = DateTimeOffset.MinValue;
        var broadcast = DateTimeOffset.MinValue;
        var backfill = DateTimeOffset.MinValue;
        long quarantined = storage.Quarantined;
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            int processed = 0;
            bool healthy = Step("processing", () => processed = storage.Process(now));
            // Reserve a small backfill slice during sustained intake so old history keeps advancing.
            if (now >= backfill)
            {
                int limit = processed == Storage.BatchSize ? Storage.BatchSize : 2000;
                int consumed = 0;
                Step("rollup backfill", () => consumed = storage.BackfillRollups(limit));
                backfill = now.AddSeconds(consumed < limit ? 30 : processed == Storage.BatchSize ? 1 : 0);
            }
            if (now >= retention)
            {
                int removed = 0;
                bool retained = Step("retention", () => removed = storage.Retain(now));
                retention = now.AddSeconds(retained && removed == Storage.RawRetentionBatch ? 1 : 30);
            }
            if (storage.Quarantined != quarantined)
            {
                logger.LogWarning("{Count} telemetry records moved to dead letters. Review Platform health and replay after a fix.",
                    storage.Quarantined - quarantined);
                quarantined = storage.Quarantined;
            }
            if (processed > 0 && live is not null && now >= broadcast)
            {
                try
                {
                    await live.Clients.Group("platform").SendAsync("Changed", stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogDebug("Live refresh hint failed: {Type}.", exception.GetType().Name);
                }
                broadcast = now.AddSeconds(10);
            }
            failures = healthy ? 0 : Math.Min(failures + 1, 6);
            var delay = !healthy ? TimeSpan.FromSeconds(Math.Pow(2, failures - 1))
                : processed == Storage.BatchSize ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(1);
            await Task.Delay(delay, stoppingToken);
        }

        bool Step(string name, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (SqliteException exception)
            {
                logger.LogWarning("PulseOps {Step} will retry. SQLite code {Code}/{ExtendedCode}.",
                    name, exception.SqliteErrorCode, exception.SqliteExtendedErrorCode);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError("PulseOps {Step} failed with {Type} and will retry.", name, exception.GetType().Name);
            }
            return false;
        }
    }
}

public sealed class SyntheticChecks(PlatformOptions options, Storage storage, TimeProvider clock,
    ILogger<SyntheticChecks> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Parallel.ForEachAsync(options.Probes, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Probes.Count),
            CancellationToken = stoppingToken
        }, async (probe, token) =>
        {
            var scope = options.Scopes.Single(s => s.Application == probe.Application && s.Environment == probe.Environment);
            var session = Guid.NewGuid();
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 10)), token);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(probe.IntervalSeconds));
            do
            {
                var now = clock.GetUtcNow();
                var result = await ProbeClient.CheckAsync(probe, now, token);
                var item = new TelemetryEvent(Guid.NewGuid(), now, "probe", session, "synthetic", probe.Name,
                    result.Outcome, result.DurationMs, result.StatusCode, Category: result.Category,
                    Value: result.CertificateDays);
                try
                {
                    if (storage.Ingest(scope, [item], false, now) is null)
                        logger.LogWarning("Synthetic telemetry dropped because storage is at capacity.");
                }
                catch (SqliteException)
                {
                    logger.LogWarning("Synthetic telemetry dropped because storage is unavailable.");
                }
            } while (await timer.WaitForNextTickAsync(token));
        });
}

internal sealed record ProbeResult(string Outcome, double DurationMs, int? StatusCode, string Category, double? CertificateDays);

/// <summary>
/// Read-only HTTPS checks with TLS and DNS diagnostics. Each check uses a fresh connection, so it performs DNS
/// resolution, connection and full certificate validation, and records the server certificate's remaining validity.
/// </summary>
internal static class ProbeClient
{
    public static async Task<ProbeResult> CheckAsync(ProbeSettings probe, DateTimeOffset now, CancellationToken token)
    {
        DateTimeOffset? notAfter = null;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.Zero,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                // Validation policy is unchanged: any certificate error fails the check.
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is System.Security.Cryptography.X509Certificates.X509Certificate2 x509)
                        notAfter = new DateTimeOffset(x509.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                    return errors == System.Net.Security.SslPolicyErrors.None;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var watch = Stopwatch.StartNew();
        int? code = null;
        string? failure = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probe.Url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            code = (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { failure = "timeout"; }
        catch (HttpRequestException exception)
        {
            failure = exception.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "dns",
                HttpRequestError.SecureConnectionError => "tls",
                HttpRequestError.ConnectionError => "connection",
                _ => "network"
            };
        }
        return Evaluate(probe, code, failure, notAfter, now, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Classifies one check. A passing check whose certificate expires within the warning window is degraded.</summary>
    internal static ProbeResult Evaluate(ProbeSettings probe, int? code, string? failure, DateTimeOffset? notAfter,
        DateTimeOffset now, double durationMs)
    {
        double? days = notAfter is { } expiry ? Math.Floor((expiry - now).TotalDays) : null;
        if (failure is not null)
            return new("failure", durationMs, null, failure, days);
        if (code != probe.ExpectedStatus)
            return new("failure", durationMs, code, code >= 500 ? "server-error" : code >= 400 ? "client-error" : "http", days);
        if (probe.CertificateWarningDays > 0 && days < probe.CertificateWarningDays)
            return new("degraded", durationMs, code, "tls", days);
        return new("success", durationMs, code, "probe", days);
    }
}

public sealed class LiveHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Only administrators receive a global refresh hint. Scoped users poll scoped APIs.
        if (Context.User?.IsInRole("PlatformAdministrator") == true)
            await Groups.AddToGroupAsync(Context.ConnectionId, "platform");
        await base.OnConnectedAsync();
    }
}
