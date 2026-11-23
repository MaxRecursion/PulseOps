using System.Buffers;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

namespace PulseOps.Server;

// Audited administration: a hash-chained append-only audit log, maker-checker change control for monitor policies and
// maintenance windows, maintenance suppression lookups and verified audit export.
//
// Every state change commits with its audit entry in one IMMEDIATE transaction. Entry n stores
// hash = lowercase hex SHA-256 of the UTF-8 compact System.Text.Json array (default encoder)
// [seq, at, actor, action, application, environment, target, detail, previousHash], where previousHash is the hash of
// entry n - 1 (64 zeros for entry 1) and seq is contiguous from 1. Triggers reject UPDATE and DELETE. A chain proves
// internal consistency only: keep exported head hashes outside the server to detect truncation or a rewritten suffix.

/// <summary>The operator behind an administration request, resolved from server-side claims only.</summary>
internal sealed record AdminCaller(string Actor, AppScope[] Visible, bool CanAdminister, bool CanPropose, bool CanAudit)
{
    public static AdminCaller From(ClaimsPrincipal user, PlatformOptions options) => new(Access.Actor(user),
        Access.Visible(user, options), Access.CanAdminister(user), Access.CanPropose(user), Access.CanAudit(user));

    /// <summary>Scopes whose rows the caller reads. Null means every row, including platform-level entries and retired scopes.</summary>
    public AppScope[]? Readable => CanAdminister ? null : Visible;

    public bool CanReadChanges => CanAdminister || CanPropose || CanAudit;

    public bool Sees(string application, string environment) => CanAdminister || CanTarget(application, environment);

    /// <summary>Proposals require a registered scope that the caller can see.</summary>
    public bool CanTarget(string application, string environment) =>
        Visible.Any(s => s.Application == application && s.Environment == environment);
}

/// <summary>The result of a change-control request. Handlers map it to HTTP without further decisions.</summary>
internal sealed record ChangeOutcome(int Status, string? Error = null, string? Detail = null, string? Id = null)
{
    public static readonly ChangeOutcome Done = new(204);
    public static readonly ChangeOutcome Forbidden = new(403);
    public static readonly ChangeOutcome Missing = new(404);
    public static ChangeOutcome Invalid(string detail) => new(400, "invalid_change", detail);
    public static ChangeOutcome Conflict(string error) => new(409, error);
}

internal sealed record ChangeRecord(string Id, DateTimeOffset CreatedAt, string Proposer, string Entity, string Operation,
    string Application, string Environment, string Target, JsonElement Payload, string Reason, long? BaseRevision,
    string Status, DateTimeOffset? DecidedAt, string? Decider, string? DecisionNote, bool CanApprove, bool CanWithdraw);

internal sealed record MaintenanceRecord(string Id, string Application, string Environment, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string Reason, DateTimeOffset CreatedAt, string CreatedBy, string ChangeId,
    DateTimeOffset? CancelledAt, bool Active);

internal sealed record AuditEntry(long Seq, DateTimeOffset At, string Actor, string Action, string Application,
    string Environment, string Target, string Detail, string PreviousHash, string Hash);

/// <summary>The full chain recomputed. BrokenAtSeq is the first entry whose sequence, link or hash does not verify.</summary>
internal sealed record AuditChain(bool Verified, long Entries, string HeadHash, long? BrokenAtSeq);

internal sealed record AuditPage(AuditChain Chain, List<AuditEntry> Entries);

/// <summary>A recorded export: entries before <see cref="BeforeSeq"/>, at or after <see cref="Since"/>, within Scopes (null = all).</summary>
internal sealed record AuditExport(long BeforeSeq, long Since, AppScope[]? Scopes, long Entries);

public sealed partial class Storage
{
    internal static readonly string AuditGenesis = new('0', 64);
    private const string AuditSystemActor = "PulseOps";
    private const int ChangePayloadLimit = 8192;
    private const int ChangeExpiryBatch = 100;
    private const int MaximumManagedMonitors = 300;
    private const int AuditExportPage = 500;
    private const string ChangeColumns = """
        id, created, proposer, entity, operation, application, environment, target, payload, reason, base_revision,
        status, decided, decider, decision_note
        """;
    private const string AuditColumns = "seq, at, actor, action, application, environment, target, detail, previous_hash, hash";
    private const string MaintenanceColumns =
        "id, application, environment, starts, ends, reason, created, created_by, change_id, cancelled";
    private const string DecisionNoteRule = "Decision notes are limited to 500 characters without control characters.";

    private sealed record ConfigChange(string Id, long Created, string Proposer, string Entity, string Operation,
        string Application, string Environment, string Target, string Payload, string Reason, long? BaseRevision,
        string Status, long? Decided, string? Decider, string? DecisionNote);

    private sealed record ChangeDraft(string Entity, string Operation, string Application, string Environment, string Target,
        string Payload);

    // ---- Maintenance ----

    /// <summary>The active, uncancelled maintenance window covering the scope at <paramref name="at"/>, if any.</summary>
    private (string Id, string Reason, long Ends)? ActiveMaintenance(SqliteConnection connection, SqliteTransaction transaction,
        string application, string environment, long at)
    {
        using var command = connection.Command(transaction, """
            SELECT id, reason, ends FROM maintenance_windows
            WHERE application = $app AND environment = $env AND ends > $at AND starts <= $at AND cancelled IS NULL
            ORDER BY ends DESC, id LIMIT 1
            """, ("$app", application), ("$env", environment), ("$at", at));
        using var row = command.ExecuteReader();
        return row.Read() ? (row.GetString(0), row.GetString(1), row.GetInt64(2)) : null;
    }

    /// <summary>Read-only form of the suppression lookup: the window covering the scope at the instant, if any.</summary>
    internal (string Id, string Reason, long Ends)? ActiveMaintenance(string application, string environment, DateTimeOffset at)
    {
        using var connection = Open();
        using var snapshot = connection.BeginTransaction(deferred: true);
        return ActiveMaintenance(connection, snapshot, application, environment, at.ToUnixTimeMilliseconds());
    }

    /// <summary>Upcoming and active windows (soonest first) or, including ended and cancelled ones, the latest 200.</summary>
    internal List<MaintenanceRecord> MaintenanceWindows(AdminCaller caller, bool includeEnded, DateTimeOffset now)
    {
        long at = now.ToUnixTimeMilliseconds();
        var scopes = caller.Readable;
        using var connection = Open();
        using var command = connection.Command(null, $"""
            SELECT {MaintenanceColumns} FROM maintenance_windows
            WHERE {AdminScopeClause(scopes)} AND {(includeEnded ? "1" : "ends > $now AND cancelled IS NULL")}
            ORDER BY starts {(includeEnded ? "DESC" : "ASC")}, id LIMIT 200
            """, ("$now", at));
        BindAdminScopes(command, scopes);
        return Read(command, row => new MaintenanceRecord(row.GetString(0), row.GetString(1), row.GetString(2),
            row.Time(3), row.Time(4), row.GetString(5), row.Time(6), row.GetString(7), row.GetString(8),
            row.NullableTime(9), row.IsDBNull(9) && row.GetInt64(3) <= at && at < row.GetInt64(4)));
    }

    // ---- Change control ----

    /// <summary>Lists changes in the caller's readable scopes, newest first. Null when the caller may not read changes.</summary>
    internal List<ChangeRecord>? Changes(AdminCaller caller, bool pendingOnly, int limit, int offset)
    {
        if (!caller.CanReadChanges)
            return null;
        var scopes = caller.Readable;
        using var connection = Open();
        using var command = connection.Command(null, $"""
            SELECT {ChangeColumns} FROM config_changes
            WHERE {(pendingOnly ? "status = 'Pending'" : "1")} AND {AdminScopeClause(scopes)}
            ORDER BY created DESC, id DESC LIMIT $limit OFFSET $offset
            """, ("$limit", limit), ("$offset", offset));
        BindAdminScopes(command, scopes);
        return Read(command, ChangeRow).Select(change => new ChangeRecord(change.Id,
            DateTimeOffset.FromUnixTimeMilliseconds(change.Created), change.Proposer, change.Entity, change.Operation,
            change.Application, change.Environment, change.Target, JsonSerializer.Deserialize<JsonElement>(change.Payload),
            change.Reason, change.BaseRevision, change.Status,
            change.Decided is long decided ? DateTimeOffset.FromUnixTimeMilliseconds(decided) : null, change.Decider,
            change.DecisionNote,
            CanApprove: caller.CanAdminister && change.Status == "Pending" &&
                (change.Proposer != caller.Actor || options.Administration.AllowSelfApproval),
            CanWithdraw: caller.CanPropose && change.Status == "Pending" && change.Proposer == caller.Actor)).ToList();
    }

    /// <summary>Validates and records a pending change with its config.proposed audit entry.</summary>
    internal ChangeOutcome ProposeChange(AdminCaller caller, ChangeProposal proposal, DateTimeOffset now)
    {
        if (!caller.CanPropose)
            return ChangeOutcome.Forbidden;
        var (draft, rejection) = PrepareChange(caller, proposal, now.ToUnixTimeMilliseconds());
        if (draft is null)
            return rejection!;
        if (!IsChangeText(proposal.Reason, 3, 500, out var reason))
            return ChangeOutcome.Invalid("A change reason of 3-500 characters without control characters is required.");
        if (Encoding.UTF8.GetByteCount(draft.Payload) > ChangePayloadLimit)
            return ChangeOutcome.Invalid("The change payload exceeds 8 KB.");
        if (draft.Operation == "upsert" && IsConfiguredMonitor(draft.Application, draft.Environment, draft.Target))
            return ChangeOutcome.Conflict("configuration_managed");

        return DecideChange(now, null, (connection, transaction, at) =>
        {
            long? baseRevision = null;
            if (draft.Entity == "monitor")
            {
                baseRevision = PolicyRevision(connection, transaction, draft.Application, draft.Environment, draft.Target);
                if (draft.Operation == "delete" && baseRevision is null)
                    return ChangeOutcome.Conflict("not_found");
            }
            else if (draft.Operation == "cancel" &&
                !IsOpenMaintenance(connection, transaction, draft.Application, draft.Environment, draft.Target, at))
                return ChangeOutcome.Conflict("not_found");

            using var pending = connection.Command(transaction, "SELECT COUNT(*) FROM config_changes WHERE status = 'Pending'");
            if ((long)pending.ExecuteScalar()! >= options.Administration.MaximumPendingChanges)
                return ChangeOutcome.Conflict("too_many_pending");
            using var duplicate = connection.Command(transaction, """
                SELECT 1 FROM config_changes WHERE status = 'Pending' AND entity = $entity
                    AND application = $app AND environment = $env AND target = $target LIMIT 1
                """, ("$entity", draft.Entity), ("$app", draft.Application), ("$env", draft.Environment),
                ("$target", draft.Target));
            if (duplicate.ExecuteScalar() is not null)
                return ChangeOutcome.Conflict("conflict");

            var id = Guid.NewGuid().ToString("D");
            ExecuteAdmin(connection, transaction, """
                INSERT INTO config_changes(id, created, proposer, entity, operation, application, environment, target,
                    payload, reason, base_revision, status)
                VALUES ($id, $at, $proposer, $entity, $operation, $app, $env, $target, $payload, $reason, $base, 'Pending')
                """, ("$id", id), ("$at", at), ("$proposer", caller.Actor), ("$entity", draft.Entity),
                ("$operation", draft.Operation), ("$app", draft.Application), ("$env", draft.Environment),
                ("$target", draft.Target), ("$payload", draft.Payload), ("$reason", reason), ("$base", baseRevision));
            WriteAudit(connection, transaction, at, caller.Actor, "config.proposed", draft.Application, draft.Environment, id,
                AuditDetail(new
                {
                    entity = draft.Entity, operation = draft.Operation, target = draft.Target,
                    // The free-text reason stays in config_changes only. The chain is immutable, so text that may later need
                    // redaction must never enter it; the change id links the entry to the reason.
                    payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Payload)))
                }));
            return new ChangeOutcome(201, Id: id);
        });
    }

    /// <summary>
    /// Applies a pending change after a second administrator approves it. A change that no longer validates against the
    /// current configuration, or whose base revision moved, is marked Superseded and audited instead.
    /// </summary>
    internal ChangeOutcome ApproveChange(AdminCaller caller, string id, string? note, DateTimeOffset now)
    {
        if (!caller.CanAdminister)
            return ChangeOutcome.Forbidden;
        if (!IsChangeText(note, 0, 500, out var decision))
            return ChangeOutcome.Invalid(DecisionNoteRule);
        return DecideChange(now, id, (connection, transaction, at) =>
        {
            var change = FindChange(connection, transaction, id);
            if (change is null)
                return ChangeOutcome.Missing;
            if (change.Status != "Pending")
                return ChangeOutcome.Conflict("not_pending");
            if (change.Proposer == caller.Actor && !options.Administration.AllowSelfApproval)
                return new ChangeOutcome(403, "self_approval");
            if (RevalidateChange(connection, transaction, change, at) is { } problem)
            {
                CloseChange(connection, transaction, change, "Superseded", "config.superseded", caller.Actor, problem, at,
                    AuditDetail(new { entity = change.Entity, operation = change.Operation, target = change.Target,
                        cause = "invalid_now", explanation = problem }));
                return ChangeOutcome.Conflict("invalid_now");
            }
            if (change.Entity == "monitor")
            {
                var current = PolicyRevision(connection, transaction, change.Application, change.Environment, change.Target);
                if (current != change.BaseRevision)
                {
                    CloseChange(connection, transaction, change, "Superseded", "config.superseded", caller.Actor,
                        "The monitor changed after this proposal.", at,
                        AuditDetail(new { entity = change.Entity, operation = change.Operation, target = change.Target,
                            cause = "stale", baseRevision = change.BaseRevision, currentRevision = current }));
                    return ChangeOutcome.Conflict("stale");
                }
            }

            string target = change.Target;
            string detail;
            switch (change.Operation)
            {
                case "upsert":
                {
                    if (change.BaseRevision is null)
                    {
                        // Configuration and approved policies share one evaluation budget of 300 monitors.
                        using var count = connection.Command(transaction, "SELECT COUNT(*) FROM monitor_policies");
                        if ((long)count.ExecuteScalar()! + options.Monitors.Count >= MaximumManagedMonitors)
                            return ChangeOutcome.Conflict("managed_monitor_limit");
                    }
                    long revision = (change.BaseRevision ?? 0) + 1;
                    ExecuteAdmin(connection, transaction, """
                        INSERT INTO monitor_policies(application, environment, name, revision, definition, updated,
                            updated_by, change_id)
                        VALUES ($app, $env, $name, $revision, $definition, $at, $by, $change)
                        ON CONFLICT(application, environment, name) DO UPDATE SET revision = excluded.revision,
                            definition = excluded.definition, updated = excluded.updated,
                            updated_by = excluded.updated_by, change_id = excluded.change_id
                        """, ("$app", change.Application), ("$env", change.Environment), ("$name", change.Target),
                        ("$revision", revision), ("$definition", change.Payload), ("$at", at), ("$by", change.Proposer),
                        ("$change", change.Id));
                    detail = AuditDetail(new { entity = change.Entity, operation = change.Operation, target, revision });
                    break;
                }
                case "delete":
                    // Incidents opened by the monitor stay untouched; only its definition and evaluation state go.
                    ExecuteAdmin(connection, transaction, """
                        DELETE FROM monitor_policies WHERE application = $app AND environment = $env AND name = $name;
                        DELETE FROM monitor_state WHERE application = $app AND environment = $env AND name = $name;
                        """, ("$app", change.Application), ("$env", change.Environment), ("$name", change.Target));
                    detail = AuditDetail(new { entity = change.Entity, operation = change.Operation, target,
                        revision = change.BaseRevision });
                    break;
                case "create":
                {
                    var window = ParseChangePayload<MaintenanceRequest>(change.Payload)!;
                    target = Guid.NewGuid().ToString("D");
                    ExecuteAdmin(connection, transaction, """
                        INSERT INTO maintenance_windows(id, application, environment, starts, ends, reason, created,
                            created_by, change_id)
                        VALUES ($id, $app, $env, $starts, $ends, $reason, $at, $by, $change)
                        """, ("$id", target), ("$app", change.Application), ("$env", change.Environment),
                        ("$starts", window.StartsAt.ToUnixTimeMilliseconds()), ("$ends", window.EndsAt.ToUnixTimeMilliseconds()),
                        ("$reason", window.Reason), ("$at", at), ("$by", change.Proposer), ("$change", change.Id));
                    detail = AuditDetail(new { entity = change.Entity, operation = change.Operation, target, windowId = target });
                    break;
                }
                default:
                    ExecuteAdmin(connection, transaction, """
                        UPDATE maintenance_windows SET cancelled = $at
                        WHERE id = $id AND application = $app AND environment = $env AND cancelled IS NULL
                        """, ("$at", at), ("$id", change.Target), ("$app", change.Application), ("$env", change.Environment));
                    detail = AuditDetail(new { entity = change.Entity, operation = change.Operation, target, windowId = target });
                    break;
            }
            ExecuteAdmin(connection, transaction, """
                UPDATE config_changes SET status = 'Applied', decided = $at, decider = $actor, decision_note = $note,
                    target = $target
                WHERE id = $id AND status = 'Pending'
                """, ("$at", at), ("$actor", caller.Actor), ("$note", decision.Length == 0 ? null : decision),
                ("$target", target), ("$id", change.Id));
            WriteAudit(connection, transaction, at, caller.Actor, "config.approved", change.Application, change.Environment,
                change.Id, detail);
            return ChangeOutcome.Done;
        });
    }

    internal ChangeOutcome RejectChange(AdminCaller caller, string id, string? note, DateTimeOffset now)
    {
        if (!caller.CanAdminister)
            return ChangeOutcome.Forbidden;
        if (!IsChangeText(note, 0, 500, out var decision))
            return ChangeOutcome.Invalid(DecisionNoteRule);
        return DecideChange(now, id, (connection, transaction, at) =>
        {
            var change = FindChange(connection, transaction, id);
            if (change is null)
                return ChangeOutcome.Missing;
            if (change.Status != "Pending")
                return ChangeOutcome.Conflict("not_pending");
            CloseChange(connection, transaction, change, "Rejected", "config.rejected", caller.Actor, decision, at);
            return ChangeOutcome.Done;
        });
    }

    /// <summary>The proposer withdraws their own pending change. Other operators receive 403, unseen scopes 404.</summary>
    internal ChangeOutcome WithdrawChange(AdminCaller caller, string id, DateTimeOffset now)
    {
        if (!caller.CanPropose)
            return ChangeOutcome.Forbidden;
        return DecideChange(now, id, (connection, transaction, at) =>
        {
            var change = FindChange(connection, transaction, id);
            if (change is null || !caller.Sees(change.Application, change.Environment))
                return ChangeOutcome.Missing;
            if (change.Proposer != caller.Actor)
                return ChangeOutcome.Forbidden;
            if (change.Status != "Pending")
                return ChangeOutcome.Conflict("not_pending");
            CloseChange(connection, transaction, change, "Withdrawn", "config.withdrawn", caller.Actor, "", at);
            return ChangeOutcome.Done;
        });
    }

    /// <summary>Expires the change being decided, or the next maintenance batch when no id is specified.</summary>
    private void ExpireChanges(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, string? id = null)
    {
        using var select = connection.Command(transaction, $"""
            SELECT {ChangeColumns} FROM config_changes WHERE status = 'Pending' AND created <= $cutoff
                AND {(id is null ? "1" : "id = $id")}
            ORDER BY created, id LIMIT $limit
            """, ("$cutoff", now.AddHours(-options.Administration.ChangeExpiryHours).ToUnixTimeMilliseconds()),
            ("$limit", ChangeExpiryBatch), ("$id", id));
        long at = now.ToUnixTimeMilliseconds();
        foreach (var change in Read(select, ChangeRow))
            CloseChange(connection, transaction, change, "Expired", "config.expired", AuditSystemActor, "", at);
    }

    private ChangeOutcome DecideChange(DateTimeOffset now, string? id, Func<SqliteConnection, SqliteTransaction, long, ChangeOutcome> decide)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ExpireChanges(connection, transaction, now, id);
        var outcome = decide(connection, transaction, now.ToUnixTimeMilliseconds());
        transaction.Commit();
        return outcome;
    }

    private void CloseChange(SqliteConnection connection, SqliteTransaction transaction, ConfigChange change, string status,
        string action, string actor, string note, long at, string? detail = null)
    {
        ExecuteAdmin(connection, transaction, """
            UPDATE config_changes SET status = $status, decided = $at, decider = $actor, decision_note = $note
            WHERE id = $id AND status = 'Pending'
            """, ("$status", status), ("$at", at), ("$actor", actor), ("$note", note.Length == 0 ? null : note),
            ("$id", change.Id));
        WriteAudit(connection, transaction, at, actor, action, change.Application, change.Environment, change.Id,
            detail ?? AuditDetail(new { entity = change.Entity, operation = change.Operation, target = change.Target }));
    }

    // Structural and scope validation of a proposal. Scopes outside the caller's visibility, including unregistered
    // ones, answer 403 so that a proposer cannot probe which application environments exist.
    private (ChangeDraft? Draft, ChangeOutcome? Rejection) PrepareChange(AdminCaller caller, ChangeProposal proposal, long at)
    {
        string application = proposal.Application ?? "", environment = proposal.Environment ?? "", target = proposal.Target ?? "";
        switch (proposal.Entity, proposal.Operation)
        {
            case ("monitor", "upsert") when proposal is { Monitor: { } monitor, Maintenance: null } &&
                Same(proposal.Application, monitor.Application) && Same(proposal.Environment, monitor.Environment) &&
                Same(proposal.Target, monitor.Name):
            {
                if (!Validation.IsSlug(monitor.Application) || !Validation.IsSlug(monitor.Environment))
                    return Reject("Application and environment must be registered slugs.");
                if (!caller.CanTarget(monitor.Application, monitor.Environment))
                    return (null, ChangeOutcome.Forbidden);
                if (monitor.Validate(options.Scopes) is { } problem)
                    return Reject(problem);
                return (new("monitor", "upsert", monitor.Application, monitor.Environment, monitor.Name,
                    JsonSerializer.Serialize(monitor, JsonSerializerOptions.Web)), null);
            }
            case ("monitor", "delete") when proposal is { Monitor: null, Maintenance: null }:
            {
                if (!Validation.IsSlug(application) || !Validation.IsSlug(environment) || !Validation.IsAlias(target))
                    return Reject("Deleting a monitor requires its application, environment and name.");
                if (!caller.CanTarget(application, environment))
                    return (null, ChangeOutcome.Forbidden);
                return (new("monitor", "delete", application, environment, target,
                    Identity(application, environment, target)), null);
            }
            case ("maintenance", "create") when proposal is { Maintenance: { } window, Monitor: null } &&
                target.Length == 0 && Same(proposal.Application, window.Application) &&
                Same(proposal.Environment, window.Environment):
            {
                string windowApplication = window.Application ?? "", windowEnvironment = window.Environment ?? "";
                if (!Validation.IsSlug(windowApplication) || !Validation.IsSlug(windowEnvironment))
                    return Reject("Application and environment must be registered slugs.");
                if (!caller.CanTarget(windowApplication, windowEnvironment))
                    return (null, ChangeOutcome.Forbidden);
                long starts = window.StartsAt.ToUnixTimeMilliseconds(), ends = window.EndsAt.ToUnixTimeMilliseconds();
                if (starts >= ends)
                    return Reject("Maintenance must end after it starts.");
                if (ends <= at)
                    return Reject("Maintenance must end in the future.");
                if (ends - starts > 7 * DayMs)
                    return Reject("Maintenance windows are limited to 7 days.");
                if (starts > at + 90 * DayMs)
                    return Reject("Maintenance must start within 90 days.");
                if (!IsChangeText(window.Reason, 3, 200, out var why))
                    return Reject("A maintenance reason of 3-200 characters without control characters is required.");
                return (new("maintenance", "create", windowApplication, windowEnvironment, "", JsonSerializer.Serialize(
                    new MaintenanceRequest(windowApplication, windowEnvironment, DateTimeOffset.FromUnixTimeMilliseconds(starts),
                        DateTimeOffset.FromUnixTimeMilliseconds(ends), why), JsonSerializerOptions.Web)), null);
            }
            case ("maintenance", "cancel") when proposal is { Monitor: null, Maintenance: null }:
            {
                if (!Validation.IsSlug(application) || !Validation.IsSlug(environment) ||
                    !Guid.TryParseExact(target, "D", out var windowId))
                    return Reject("Cancelling maintenance requires its application, environment and window id.");
                if (!caller.CanTarget(application, environment))
                    return (null, ChangeOutcome.Forbidden);
                var window = windowId.ToString("D");
                return (new("maintenance", "cancel", application, environment, window,
                    Identity(application, environment, window)), null);
            }
            default:
                return Reject("Use monitor upsert or delete, or maintenance create or cancel, with only the matching payload.");
        }

        static (ChangeDraft?, ChangeOutcome?) Reject(string detail) => (null, ChangeOutcome.Invalid(detail));
        static bool Same(string? given, string? actual) => string.IsNullOrEmpty(given) || given == actual;
        static string Identity(string application, string environment, string target) =>
            JsonSerializer.Serialize(new { application, environment, target }, JsonSerializerOptions.Web);
    }

    // Approval-time validation against the current configuration. Returns a short operator-facing reason, or null.
    private string? RevalidateChange(SqliteConnection connection, SqliteTransaction transaction, ConfigChange change, long at)
    {
        if (!options.Scopes.Any(s => s.Application == change.Application && s.Environment == change.Environment))
            return "The application environment is no longer registered.";
        switch (change.Operation)
        {
            case "upsert":
                var policy = ParseChangePayload<MonitorPolicy>(change.Payload);
                if (policy is null || policy.Application != change.Application || policy.Environment != change.Environment ||
                    policy.Name != change.Target)
                    return "The stored monitor definition does not match the change.";
                return policy.Validate(options.Scopes) ??
                    (IsConfiguredMonitor(change.Application, change.Environment, change.Target)
                        ? "The monitor is now managed by configuration." : null);
            case "create":
                var window = ParseChangePayload<MaintenanceRequest>(change.Payload);
                if (window is null || window.Application != change.Application || window.Environment != change.Environment)
                    return "The stored maintenance window does not match the change.";
                return window.EndsAt.ToUnixTimeMilliseconds() > at ? null : "The maintenance window has already ended.";
            case "cancel":
                return IsOpenMaintenance(connection, transaction, change.Application, change.Environment, change.Target, at)
                    ? null : "The maintenance window is no longer active or scheduled.";
            default:
                return null;
        }
    }

    private bool IsConfiguredMonitor(string application, string environment, string name) =>
        options.Monitors.Any(m => m.Application == application && m.Environment == environment && m.Name == name);

    private static long? PolicyRevision(SqliteConnection connection, SqliteTransaction transaction, string application,
        string environment, string name)
    {
        using var command = connection.Command(transaction, """
            SELECT revision FROM monitor_policies WHERE application = $app AND environment = $env AND name = $name
            """, ("$app", application), ("$env", environment), ("$name", name));
        return command.ExecuteScalar() as long?;
    }

    private static bool IsOpenMaintenance(SqliteConnection connection, SqliteTransaction transaction, string application,
        string environment, string id, long at)
    {
        using var command = connection.Command(transaction, """
            SELECT 1 FROM maintenance_windows
            WHERE id = $id AND application = $app AND environment = $env AND cancelled IS NULL AND ends > $at
            """, ("$id", id), ("$app", application), ("$env", environment), ("$at", at));
        return command.ExecuteScalar() is not null;
    }

    private static ConfigChange? FindChange(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.Command(transaction, $"SELECT {ChangeColumns} FROM config_changes WHERE id = $id",
            ("$id", id));
        return Read(command, ChangeRow).SingleOrDefault();
    }

    private static ConfigChange ChangeRow(SqliteDataReader row) => new(row.GetString(0), row.GetInt64(1), row.GetString(2),
        row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8),
        row.GetString(9), row.IsDBNull(10) ? null : row.GetInt64(10), row.GetString(11),
        row.IsDBNull(12) ? null : row.GetInt64(12), row.NullableString(13), row.NullableString(14));

    private static T? ParseChangePayload<T>(string payload) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ExecuteAdmin(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static bool IsChangeText(string? value, int minimum, int maximum, out string text)
    {
        text = value?.Trim() ?? "";
        return text.Length >= minimum && text.Length <= maximum && IsPlainText(text);
    }

    // Well-formed UTF-16 without control characters, so stored text and its canonical hash input always agree.
    private static bool IsPlainText(string value)
    {
        for (int index = 0; index < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out int consumed) != OperationStatus.Done ||
                Rune.IsControl(rune))
                return false;
            index += consumed;
        }
        return true;
    }

    private static string AdminScopeClause(AppScope[]? scopes) => scopes is null ? "1" : scopes.Length == 0 ? "0" :
        "(application, environment) IN (VALUES " + string.Join(',', scopes.Select((_, i) => $"($app{i},$env{i})")) + ")";

    private static void BindAdminScopes(SqliteCommand command, AppScope[]? scopes)
    {
        for (int i = 0; scopes is not null && i < scopes.Length; i++)
        {
            command.Parameters.AddWithValue($"$app{i}", scopes[i].Application);
            command.Parameters.AddWithValue($"$env{i}", scopes[i].Environment);
        }
    }

    // ---- Audit log ----

    /// <summary>A compact JSON object built by code for an audit entry. Never pass request bodies or free-text notes.</summary>
    private static string AuditDetail<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>Appends a hash-chained audit entry inside the caller's immediate transaction.</summary>
    internal void WriteAudit(SqliteConnection connection, SqliteTransaction transaction, long at, string actor, string action,
        string application, string environment, string target, string detail)
    {
        if (!IsAuditText(actor, 1, 256) || !IsAuditText(action, 1, 64) || !IsAuditText(target, 0, 128) ||
            !IsAuditText(detail, 2, 8192) || !IsJsonObject(detail) ||
            (application.Length == 0 ? environment.Length != 0 : !Validation.IsSlug(application) || !Validation.IsSlug(environment)))
            throw new ArgumentException("Audit entries require a bounded actor, action, target, JSON object detail and scope.");
        long seq = 1;
        string previous = AuditGenesis;
        using (var last = connection.Command(transaction, "SELECT seq, hash FROM audit_log ORDER BY seq DESC LIMIT 1"))
        using (var row = last.ExecuteReader())
        {
            if (row.Read())
                (seq, previous) = (row.GetInt64(0) + 1, row.GetString(1));
        }
        ExecuteAdmin(connection, transaction, """
            INSERT INTO audit_log(seq, at, actor, action, application, environment, target, detail, previous_hash, hash)
            VALUES ($seq, $at, $actor, $action, $app, $env, $target, $detail, $previous, $hash)
            """, ("$seq", seq), ("$at", at), ("$actor", actor), ("$action", action), ("$app", application),
            ("$env", environment), ("$target", target), ("$detail", detail), ("$previous", previous),
            ("$hash", AuditHash(seq, at, actor, action, application, environment, target, detail, previous)));

        static bool IsAuditText(string value, int minimum, int maximum) =>
            value.Length >= minimum && value.Length <= maximum && IsPlainText(value);
    }

    /// <summary>Lowercase hex SHA-256 of the canonical entry: the compact default-encoded JSON array of its fields.</summary>
    internal static string AuditHash(long seq, long at, string actor, string action, string application, string environment,
        string target, string detail, string previousHash)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            json.WriteNumberValue(seq);
            json.WriteNumberValue(at);
            foreach (var value in (ReadOnlySpan<string>)[actor, action, application, environment, target, detail, previousHash])
                json.WriteStringValue(value);
            json.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>
    /// Recomputes entries in ascending sequence, streaming, and reports the first entry that does not verify. With a
    /// verified prefix (<paramref name="afterEntries"/> entries ending in <paramref name="afterHead"/>) only the entries
    /// appended since are recomputed.
    /// </summary>
    internal static AuditChain VerifyAuditChain(SqliteConnection connection, SqliteTransaction? transaction = null,
        long afterEntries = 0, string? afterHead = null)
    {
        using var command = connection.Command(transaction, $"SELECT {AuditColumns} FROM audit_log WHERE seq > $after ORDER BY seq",
            ("$after", afterEntries));
        using var rows = command.ExecuteReader();
        long entries = afterEntries;
        long? broken = null;
        string head = afterHead ?? AuditGenesis;
        while (rows.Read())
        {
            entries++;
            long seq = rows.GetInt64(0);
            string previous = rows.GetString(8), hash = rows.GetString(9);
            if (broken is null && (seq != entries || previous != head || hash != AuditHash(seq, rows.GetInt64(1),
                    rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetString(6),
                    rows.GetString(7), previous)))
                broken = seq;
            head = hash;
        }
        return new(broken is null, entries, head, broken);
    }

    // Page views verify only entries appended after the last verified head: rows are append-only (UPDATE and DELETE
    // triggers), and the last verified row's hash is re-read on every view. The whole chain is recomputed at least every
    // ten minutes, so an out-of-band edit, which needs the triggers dropped, is reported within that interval.
    // Exports always verify the full chain.
    private const long AuditFullVerificationMs = 10 * 60_000;
    private readonly Lock _auditVerification = new();
    private (long Entries, string Head, long VerifiedAt)? _auditVerified;

    private AuditChain VerifiedAuditChain(SqliteConnection connection, SqliteTransaction snapshot)
    {
        long now = System.Environment.TickCount64;
        (long Entries, string Head, long VerifiedAt)? known;
        lock (_auditVerification)
            known = _auditVerified;
        bool incremental = false;
        if (known is { } prefix && now - prefix.VerifiedAt < AuditFullVerificationMs && prefix.Entries > 0)
        {
            // The anchor is recomputed from its content, so an edit to the last verified entry forces a full verification.
            using var anchor = connection.Command(snapshot, $"SELECT {AuditColumns} FROM audit_log WHERE seq = $seq",
                ("$seq", prefix.Entries));
            using var row = anchor.ExecuteReader();
            incremental = row.Read() && row.GetString(9) == prefix.Head && AuditHash(row.GetInt64(0), row.GetInt64(1),
                row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7),
                row.GetString(8)) == prefix.Head;
        }
        var chain = incremental
            ? VerifyAuditChain(connection, snapshot, known!.Value.Entries, known.Value.Head)
            : VerifyAuditChain(connection, snapshot);
        lock (_auditVerification)
            _auditVerified = chain.Verified ? (chain.Entries, chain.HeadHash, incremental ? known!.Value.VerifiedAt : now) : null;
        return chain;
    }

    /// <summary>Newest entries first, with the chain summary for the full log in the same snapshot.</summary>
    internal AuditPage? AuditLog(AdminCaller caller, string? action, int limit, int offset)
    {
        if (!caller.CanAudit)
            return null;
        var scopes = caller.Readable;
        using var connection = Open();
        using var snapshot = connection.BeginTransaction(deferred: true);
        var chain = VerifiedAuditChain(connection, snapshot);
        using var command = connection.Command(snapshot, $"""
            SELECT {AuditColumns} FROM audit_log
            WHERE {AdminScopeClause(scopes)} AND {(action is null ? "1" : "action = $action")}
            ORDER BY seq DESC LIMIT $limit OFFSET $offset
            """, ("$action", action), ("$limit", limit), ("$offset", offset));
        BindAdminScopes(command, scopes);
        return new(chain, Read(command, AuditRow));
    }

    /// <summary>
    /// Appends the audit.exported entry in its own transaction and returns the export it describes: the caller's readable
    /// entries of the last <paramref name="days"/> days committed before that entry. Null when the caller may not audit.
    /// </summary>
    internal AuditExport? RecordAuditExport(AdminCaller caller, int days, DateTimeOffset now)
    {
        if (!caller.CanAudit)
            return null;
        var scopes = caller.Readable;
        long since = now.AddDays(-days).ToUnixTimeMilliseconds();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var count = connection.Command(transaction,
            $"SELECT COUNT(*) FROM audit_log WHERE at >= $since AND {AdminScopeClause(scopes)}", ("$since", since));
        BindAdminScopes(count, scopes);
        long entries = (long)count.ExecuteScalar()!;
        WriteAudit(connection, transaction, now.ToUnixTimeMilliseconds(), caller.Actor, "audit.exported", "", "", "audit",
            AuditDetail(new { days, entries }));
        using var head = connection.Command(transaction, "SELECT MAX(seq) FROM audit_log");
        long seq = (long)head.ExecuteScalar()!;
        transaction.Commit();
        return new(seq, since, scopes, entries);
    }

    /// <summary>Streams a recorded export as NDJSON, oldest first, in short paged reads (entries are append-only).</summary>
    internal async Task StreamAudit(Stream output, AuditExport export, CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>(65_536);
        using var json = new Utf8JsonWriter(buffer);
        using var connection = Open();
        using var command = connection.Command(null, $"""
            SELECT {AuditColumns} FROM audit_log
            WHERE seq > $after AND seq < $before AND at >= $since AND {AdminScopeClause(export.Scopes)}
            ORDER BY seq LIMIT $limit
            """, ("$after", 0L), ("$before", export.BeforeSeq), ("$since", export.Since), ("$limit", AuditExportPage));
        BindAdminScopes(command, export.Scopes);
        while (true)
        {
            var page = Read(command, AuditRow);
            foreach (var entry in page)
                WriteAuditLine(json, buffer, entry);
            await output.WriteAsync(buffer.WrittenMemory, token);
            buffer.ResetWrittenCount();
            if (page.Count < AuditExportPage)
                break;
            command.Parameters["$after"].Value = page[^1].Seq;
        }
    }

    /// <summary>Verifies the full chain and writes it as NDJSON to a new file. Returns entry count and head hash.</summary>
    public (long Count, string HeadHash) ExportAudit(string path)
    {
        var target = Path.GetFullPath(path);
        using var connection = Open();
        // One read snapshot covers verification and export, so the file holds exactly the verified chain.
        using var snapshot = connection.BeginTransaction(deferred: true);
        var chain = VerifyAuditChain(connection, snapshot);
        if (!chain.Verified)
            throw new InvalidOperationException(
                $"The audit chain does not verify at entry {chain.BrokenAtSeq}. Nothing was exported; preserve the database for investigation.");
        long count = 0;
        var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (file)
            {
                var buffer = new ArrayBufferWriter<byte>(65_536);
                using var json = new Utf8JsonWriter(buffer);
                using var command = connection.Command(snapshot, $"SELECT {AuditColumns} FROM audit_log ORDER BY seq");
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    WriteAuditLine(json, buffer, AuditRow(rows));
                    count++;
                    if (buffer.WrittenCount >= 60_000)
                    {
                        file.Write(buffer.WrittenSpan);
                        buffer.ResetWrittenCount();
                    }
                }
                file.Write(buffer.WrittenSpan);
                file.Flush(flushToDisk: true);
                if (count != chain.Entries)
                    throw new InvalidOperationException("The audit log changed during export.");
            }
        }
        catch
        {
            // The file was created by this call (CreateNew), so a partial export never remains.
            File.Delete(target);
            throw;
        }
        return (count, chain.HeadHash);
    }

    // One NDJSON line. atUnixMs is the hashed value; at is the same instant in ISO-8601 for readers.
    private static void WriteAuditLine(Utf8JsonWriter json, ArrayBufferWriter<byte> buffer, AuditEntry entry)
    {
        json.WriteStartObject();
        json.WriteNumber("seq", entry.Seq);
        json.WriteString("at", entry.At);
        json.WriteNumber("atUnixMs", entry.At.ToUnixTimeMilliseconds());
        json.WriteString("actor", entry.Actor);
        json.WriteString("action", entry.Action);
        json.WriteString("application", entry.Application);
        json.WriteString("environment", entry.Environment);
        json.WriteString("target", entry.Target);
        json.WriteString("detail", entry.Detail);
        json.WriteString("previousHash", entry.PreviousHash);
        json.WriteString("hash", entry.Hash);
        json.WriteEndObject();
        json.Flush();
        buffer.Write("\n"u8);
        json.Reset();
    }

    private static AuditEntry AuditRow(SqliteDataReader row) => new(row.GetInt64(0), row.Time(1), row.GetString(2),
        row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8),
        row.GetString(9));

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal static class AdministrationEndpoints
{
    public static void Map(RouteGroupBuilder api, PlatformOptions options)
    {
        var admin = api.MapGroup("/admin");
        admin.MapGet("/changes", (HttpContext ctx, Storage storage) =>
        {
            var status = ctx.Request.Query["status"].ToString();
            if (status is not ("" or "pending" or "all"))
                return Results.BadRequest(new { error = "invalid_query" });
            return storage.Changes(AdminCaller.From(ctx.User, options), status != "all", Number(ctx, "limit", 50, 1, 200),
                Number(ctx, "offset", 0, 0, 100_000)) is { } changes ? Results.Ok(changes) : Results.Forbid();
        });
        admin.MapPost("/changes", async (ChangeProposal proposal, HttpContext ctx, IAntiforgery csrf, Storage storage,
            TimeProvider clock) => await csrf.IsRequestValidAsync(ctx)
                ? Respond(storage.ProposeChange(AdminCaller.From(ctx.User, options), proposal, clock.GetUtcNow()))
                : Results.BadRequest());
        admin.MapPost("/changes/{id:guid}/approve", async (Guid id, ChangeDecision? decision, HttpContext ctx,
            IAntiforgery csrf, Storage storage, TimeProvider clock, IHubContext<LiveHub> live) =>
        {
            if (!await csrf.IsRequestValidAsync(ctx))
                return Results.BadRequest();
            var outcome = storage.ApproveChange(AdminCaller.From(ctx.User, options), id.ToString("D"), decision?.Note,
                clock.GetUtcNow());
            if (outcome.Status == StatusCodes.Status204NoContent)
                await Refresh(live, ctx.RequestAborted);
            return Respond(outcome);
        });
        admin.MapPost("/changes/{id:guid}/reject", async (Guid id, ChangeDecision? decision, HttpContext ctx,
            IAntiforgery csrf, Storage storage, TimeProvider clock) => await csrf.IsRequestValidAsync(ctx)
                ? Respond(storage.RejectChange(AdminCaller.From(ctx.User, options), id.ToString("D"), decision?.Note,
                    clock.GetUtcNow()))
                : Results.BadRequest());
        admin.MapPost("/changes/{id:guid}/withdraw", async (Guid id, HttpContext ctx, IAntiforgery csrf, Storage storage,
            TimeProvider clock) => await csrf.IsRequestValidAsync(ctx)
                ? Respond(storage.WithdrawChange(AdminCaller.From(ctx.User, options), id.ToString("D"), clock.GetUtcNow()))
                : Results.BadRequest());
        admin.MapGet("/maintenance", (HttpContext ctx, Storage storage, TimeProvider clock) =>
            storage.MaintenanceWindows(AdminCaller.From(ctx.User, options),
                bool.TryParse(ctx.Request.Query["includeEnded"].ToString(), out var includeEnded) && includeEnded,
                clock.GetUtcNow()));
        admin.MapGet("/audit", (HttpContext ctx, Storage storage) =>
        {
            var action = ctx.Request.Query["action"].ToString();
            return storage.AuditLog(AdminCaller.From(ctx.User, options),
                action.Length == 0 ? null : action.Length > 64 ? "(invalid)" : action,
                Number(ctx, "limit", 100, 1, 200), Number(ctx, "offset", 0, 0, 1_000_000)) is { } page
                ? Results.Ok(page) : Results.Forbid();
        });
        admin.MapGet("/audit/export", (HttpContext ctx, Storage storage, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            if (storage.RecordAuditExport(AdminCaller.From(ctx.User, options), Number(ctx, "days", 30, 1, 3650), now)
                is not { } export)
                return Results.Forbid();
            return Results.Stream((Stream output) => storage.StreamAudit(output, export, ctx.RequestAborted),
                "application/x-ndjson",
                $"pulseops-audit-{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.ndjson");
        });
    }

    private static IResult Respond(ChangeOutcome outcome) => outcome.Status switch
    {
        StatusCodes.Status201Created => Results.Json(new { id = outcome.Id }, statusCode: StatusCodes.Status201Created),
        StatusCodes.Status204NoContent => Results.NoContent(),
        StatusCodes.Status400BadRequest => Results.BadRequest(new { error = outcome.Error, detail = outcome.Detail }),
        StatusCodes.Status403Forbidden when outcome.Error is not null =>
            Results.Json(new { error = outcome.Error }, statusCode: StatusCodes.Status403Forbidden),
        StatusCodes.Status403Forbidden => Results.Forbid(),
        StatusCodes.Status404NotFound => Results.NotFound(),
        _ => Results.Conflict(new { error = outcome.Error })
    };

    // A refresh hint only: the change is already committed, so a failed broadcast must not turn success into an error.
    private static async Task Refresh(IHubContext<LiveHub> live, CancellationToken token)
    {
        try
        {
            await live.Clients.Group("platform").SendAsync("Changed", token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
    }

    private static int Number(HttpContext context, string key, int fallback, int minimum, int maximum) =>
        int.TryParse(context.Request.Query[key].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, minimum, maximum) : fallback;
}
