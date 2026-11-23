using Microsoft.Data.Sqlite;

namespace PulseOps.Server;

public sealed partial class Storage(PlatformOptions options)
{
    public const int SchemaVersion = 6;
    private const int ApplicationId = 0x504f5053; // POPS
    private string FilePath => options.DatabasePath;

    private const string Schema = """
        CREATE TABLE telemetry (
            row_id INTEGER PRIMARY KEY,
            application TEXT NOT NULL, environment TEXT NOT NULL, id TEXT NOT NULL,
            occurred INTEGER NOT NULL, received INTEGER NOT NULL,
            version TEXT NOT NULL, session_id TEXT NOT NULL,
            kind TEXT NOT NULL, name TEXT NOT NULL, outcome TEXT NOT NULL,
            duration_ms REAL, status_code INTEGER, trace_id TEXT, span_id TEXT, parent_span_id TEXT,
            category TEXT, value REAL, anonymous INTEGER NOT NULL, done INTEGER,
            UNIQUE(application, environment, id)
        );
        CREATE INDEX pending ON telemetry(row_id) WHERE done IS NULL;
        CREATE INDEX telemetry_scope_time ON telemetry(application, environment, occurred DESC) WHERE done IS NOT NULL;
        CREATE INDEX telemetry_scope_kind_time ON telemetry(application, environment, kind, occurred DESC) WHERE done IS NOT NULL;
        CREATE INDEX telemetry_policy ON telemetry(application, environment, kind, name, received) WHERE done IS NOT NULL AND anonymous = 0;
        CREATE INDEX telemetry_scoped_session ON telemetry(application, environment, session_id, occurred DESC) WHERE done IS NOT NULL;
        CREATE INDEX telemetry_scoped_trace ON telemetry(application, environment, trace_id, occurred DESC) WHERE done IS NOT NULL;
        CREATE INDEX telemetry_retention ON telemetry(received) WHERE done IS NOT NULL;
        CREATE TABLE worker_state (id INTEGER PRIMARY KEY CHECK(id = 1), heartbeat INTEGER NOT NULL, processed INTEGER NOT NULL);
        CREATE TABLE incidents (
            id TEXT PRIMARY KEY, application TEXT NOT NULL, environment TEXT NOT NULL, kind TEXT NOT NULL,
            name TEXT NOT NULL, title TEXT NOT NULL, severity TEXT NOT NULL, status TEXT NOT NULL,
            first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, occurrences INTEGER NOT NULL, evidence TEXT NOT NULL,
            assignee TEXT NOT NULL DEFAULT '', UNIQUE(application, environment, kind, name)
        );
        CREATE INDEX incident_scope ON incidents(application, environment, last_seen DESC);
        CREATE TABLE incident_history (
            row_id INTEGER PRIMARY KEY, incident_id TEXT NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
            at INTEGER NOT NULL, status TEXT NOT NULL, actor TEXT NOT NULL, note TEXT NOT NULL, assignee TEXT NOT NULL
        );
        CREATE INDEX incident_history_order ON incident_history(incident_id, row_id DESC);
        CREATE TABLE releases (
            application TEXT NOT NULL, environment TEXT NOT NULL, version TEXT NOT NULL, build_id TEXT NOT NULL,
            commit_id TEXT NOT NULL, deployed INTEGER NOT NULL, PRIMARY KEY(application, environment, build_id)
        );
        CREATE INDEX release_scope ON releases(application, environment, deployed DESC);
        PRAGMA user_version = 1;
        """;

    private const string NotificationSchema = """
        CREATE TABLE alert_deliveries (
            id TEXT PRIMARY KEY, incident_id TEXT NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
            application TEXT NOT NULL, environment TEXT NOT NULL, destination TEXT NOT NULL,
            severity TEXT NOT NULL, status TEXT NOT NULL, created INTEGER NOT NULL,
            next_attempt INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
            lease_until INTEGER, lease_id TEXT, delivery_state TEXT NOT NULL DEFAULT 'pending'
        );
        CREATE INDEX alert_due ON alert_deliveries(next_attempt) WHERE delivery_state = 'pending';
        PRAGMA user_version = 2;
        """;

    // Version 4: long-range rollups, scheduled monitors and the audited administration workflow.
    // rollups: one row per (scope, resolution, bucket, kind, name, version, anonymous). Buckets are UTC by event time.
    // histogram is a versioned sparse log-scale latency histogram (see Histogram.cs).
    // rollup_state tracks the one-time backfill of rows processed before this migration (row_id <= backfill_until).
    // audit_log is append-only. Each hash covers the canonical entry and the previous hash.
    private const string OperationsSchema = """
        CREATE TABLE rollups (
            application TEXT NOT NULL, environment TEXT NOT NULL, resolution INTEGER NOT NULL, bucket INTEGER NOT NULL,
            kind TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL, anonymous INTEGER NOT NULL,
            total INTEGER NOT NULL, successes INTEGER NOT NULL, failures INTEGER NOT NULL,
            degraded INTEGER NOT NULL, cancelled INTEGER NOT NULL,
            timed INTEGER NOT NULL, duration_sum REAL NOT NULL, duration_max REAL, histogram BLOB NOT NULL,
            PRIMARY KEY(application, environment, resolution, bucket, kind, name, version, anonymous)
        ) WITHOUT ROWID;
        CREATE INDEX rollup_retention ON rollups(resolution, bucket);
        CREATE TABLE signal_activity (
            application TEXT NOT NULL, environment TEXT NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL,
            first_received INTEGER NOT NULL, last_received INTEGER NOT NULL, last_occurred INTEGER NOT NULL,
            PRIMARY KEY(application, environment, kind, name)
        ) WITHOUT ROWID;
        CREATE TABLE rollup_state (
            id INTEGER PRIMARY KEY CHECK(id = 1), backfill_cursor INTEGER NOT NULL, backfill_until INTEGER NOT NULL
        );
        INSERT INTO rollup_state(id, backfill_cursor, backfill_until)
            VALUES (1, 0, COALESCE((SELECT MAX(row_id) FROM telemetry WHERE done IS NOT NULL), 0));
        CREATE TABLE monitor_policies (
            application TEXT NOT NULL, environment TEXT NOT NULL, name TEXT NOT NULL,
            revision INTEGER NOT NULL, definition TEXT NOT NULL, updated INTEGER NOT NULL,
            updated_by TEXT NOT NULL, change_id TEXT NOT NULL,
            PRIMARY KEY(application, environment, name)
        );
        CREATE TABLE monitor_state (
            application TEXT NOT NULL, environment TEXT NOT NULL, name TEXT NOT NULL,
            state TEXT NOT NULL, since INTEGER NOT NULL, evaluated INTEGER NOT NULL, healthy_since INTEGER,
            observed REAL, baseline REAL, samples INTEGER, detail TEXT NOT NULL DEFAULT '',
            definition_hash TEXT NOT NULL DEFAULT '', incident_id TEXT,
            PRIMARY KEY(application, environment, name)
        );
        CREATE TABLE maintenance_windows (
            id TEXT PRIMARY KEY, application TEXT NOT NULL, environment TEXT NOT NULL,
            starts INTEGER NOT NULL, ends INTEGER NOT NULL, reason TEXT NOT NULL,
            created INTEGER NOT NULL, created_by TEXT NOT NULL, change_id TEXT NOT NULL, cancelled INTEGER
        );
        CREATE INDEX maintenance_scope ON maintenance_windows(application, environment, ends);
        CREATE TABLE config_changes (
            id TEXT PRIMARY KEY, created INTEGER NOT NULL, proposer TEXT NOT NULL,
            entity TEXT NOT NULL, operation TEXT NOT NULL, application TEXT NOT NULL, environment TEXT NOT NULL,
            target TEXT NOT NULL, payload TEXT NOT NULL, reason TEXT NOT NULL, base_revision INTEGER,
            status TEXT NOT NULL, decided INTEGER, decider TEXT, decision_note TEXT
        );
        CREATE INDEX config_change_status ON config_changes(status, created DESC);
        CREATE TABLE audit_log (
            seq INTEGER PRIMARY KEY, at INTEGER NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL,
            application TEXT NOT NULL, environment TEXT NOT NULL, target TEXT NOT NULL, detail TEXT NOT NULL,
            previous_hash TEXT NOT NULL, hash TEXT NOT NULL UNIQUE
        );
        CREATE INDEX audit_scope ON audit_log(application, environment, seq DESC);
        CREATE TRIGGER audit_log_append_only_update BEFORE UPDATE ON audit_log
        BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
        CREATE TRIGGER audit_log_append_only_delete BEFORE DELETE ON audit_log
        BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
        CREATE INDEX pending_scope ON telemetry(application, environment, row_id) WHERE done IS NULL;
        DROP INDEX telemetry_retention;
        CREATE INDEX telemetry_retention ON telemetry(kind, received) WHERE done IS NOT NULL;
        CREATE TABLE dead_letters (
            row_id INTEGER PRIMARY KEY, application TEXT NOT NULL, environment TEXT NOT NULL, id TEXT NOT NULL,
            occurred INTEGER NOT NULL, received INTEGER NOT NULL, version TEXT NOT NULL, session_id TEXT NOT NULL,
            kind TEXT NOT NULL, name TEXT NOT NULL, outcome TEXT NOT NULL, duration_ms REAL, status_code INTEGER,
            trace_id TEXT, span_id TEXT, parent_span_id TEXT, category TEXT, value REAL, anonymous INTEGER NOT NULL,
            reason TEXT NOT NULL, quarantined INTEGER NOT NULL
        );
        CREATE INDEX dead_letter_age ON dead_letters(quarantined);
        PRAGMA user_version = 4;
        """;

    public SqliteConnection Open() => OpenFile(FilePath, SqliteOpenMode.ReadWrite);

    private static SqliteConnection OpenFile(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, ForeignKeys = true,
            DefaultTimeout = 2, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            // SQLite's own busy handler retries a locked write in short steps. Without it Microsoft.Data.Sqlite sleeps
            // 150 ms per retry, so any brief collision between intake and processing added ~150 ms to a request.
            using (var busy = connection.Command(null, "PRAGMA busy_timeout = 2000"))
                busy.ExecuteNonQuery();
            if (mode != SqliteOpenMode.ReadOnly)
            {
                using var synchronous = connection.Command(null, "PRAGMA synchronous = FULL");
                synchronous.ExecuteNonQuery();
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Migrate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var connection = OpenFile(FilePath, SqliteOpenMode.ReadWriteCreate);
        ValidateEngine(connection);
        using var journal = connection.Command(null, "PRAGMA journal_mode = WAL");
        if (!string.Equals((string?)journal.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite could not enable WAL on this volume.");
        using var transaction = connection.BeginTransaction();
        using var check = connection.Command(transaction, "PRAGMA user_version");
        var version = (long)check.ExecuteScalar()!;
        if (version == 0)
        {
            using var existing = connection.Command(transaction, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table'");
            if ((long)existing.ExecuteScalar()! != 0)
                throw new InvalidOperationException("Refusing to initialize a nonempty SQLite database.");
            using var migration = connection.Command(transaction, Schema);
            migration.ExecuteNonQuery();
        }
        else
            ValidateIdentity(connection, transaction, version);
        if (version < 2)
        {
            using var migration = connection.Command(transaction, NotificationSchema);
            migration.ExecuteNonQuery();
        }
        if (version < 4)
        {
            using var migration = connection.Command(transaction, OperationsSchema);
            migration.ExecuteNonQuery();
        }
        if (version < 5)
        {
            using var migration = connection.Command(transaction, """
                CREATE TABLE processing_cursor (
                    id INTEGER PRIMARY KEY CHECK(id = 1), application TEXT NOT NULL, environment TEXT NOT NULL
                );
                INSERT INTO processing_cursor VALUES (1, '', '');
                -- An early schema-4 build marked receipts done without generating any rollups.
                -- Recover the retained raw history when there are no existing aggregates to double count.
                UPDATE rollup_state SET backfill_cursor = 0,
                    backfill_until = COALESCE((SELECT MAX(row_id) FROM telemetry WHERE done IS NOT NULL), 0)
                WHERE id = 1 AND NOT EXISTS (SELECT 1 FROM rollups);
                """);
            migration.ExecuteNonQuery();
        }
        if (version < 6)
        {
            // Per-incident, per-destination delivery order for the outbox (ClaimAlert) and maintenance deferral.
            using var migration = connection.Command(transaction, """
                CREATE INDEX IF NOT EXISTS alert_order ON alert_deliveries(incident_id, destination) WHERE delivery_state IN ('pending', 'deferred');
                """);
            migration.ExecuteNonQuery();
        }
        if (version < SchemaVersion)
        {
            using var identify = connection.Command(transaction, $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {SchemaVersion}");
            identify.ExecuteNonQuery();
        }
        else if (version != SchemaVersion)
            throw new InvalidOperationException("Unsupported SQLite schema. Use matching PulseOps binaries.");
        transaction.Commit();
    }

    public void AssertReady()
    {
        using var connection = Open();
        ValidateEngine(connection);
        ValidateSchema(connection);
        using var journal = connection.Command(null, "PRAGMA journal_mode");
        if (!string.Equals((string?)journal.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run --migrate to enable WAL.");
    }

    private static void ValidateEngine(SqliteConnection connection)
    {
        using var command = connection.Command(null, "SELECT sqlite_version()");
        var version = Version.Parse((string)command.ExecuteScalar()!);
        if (version < new Version(3, 51, 3))
            throw new InvalidOperationException("The bundled SQLite must be at least 3.51.3.");
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        using var command = connection.Command(null, "PRAGMA user_version");
        if ((long)command.ExecuteScalar()! != SchemaVersion)
            throw new InvalidOperationException("Run --migrate with the matching PulseOps version.");
        ValidateIdentity(connection, null, SchemaVersion);
    }

    private static void ValidateIdentity(SqliteConnection connection, SqliteTransaction? transaction, long version)
    {
        using var command = connection.Command(transaction, "PRAGMA application_id");
        long identity = (long)command.ExecuteScalar()!;
        if (identity == ApplicationId)
            return;
        if (identity == 0 && version is 1 or 2)
        {
            using var legacy = connection.Command(transaction, """
                SELECT COUNT(*) FROM sqlite_schema WHERE type='table'
                    AND name IN ('telemetry','incidents','worker_state','releases')
                """);
            if ((long)legacy.ExecuteScalar()! == 4)
                return;
        }
        throw new InvalidOperationException("The SQLite file is not a recognized PulseOps database.");
    }

    public static void VerifyBackup(string path, bool requireCurrentSchema = false)
    {
        using var connection = OpenFile(Path.GetFullPath(path), SqliteOpenMode.ReadOnly);
        ValidateEngine(connection);
        if (requireCurrentSchema)
            ValidateSchema(connection);
        else
        {
            using var version = connection.Command(null, "PRAGMA user_version");
            ValidateIdentity(connection, null, (long)version.ExecuteScalar()!);
        }
        using var check = connection.Command(null, "PRAGMA integrity_check(1)");
        if ((string?)check.ExecuteScalar() != "ok")
            throw new InvalidOperationException("SQLite backup failed integrity verification.");
    }

    public void Backup(string path)
    {
        var target = Path.GetFullPath(path);
        if (File.Exists(target) || File.Exists(target + "-wal") || File.Exists(target + "-shm"))
            throw new IOException("Backup destination already exists.");
        CopyDatabase(FilePath, target);
        VerifyBackup(target);
    }

    public void Restore(string path)
    {
        var snapshot = Path.GetFullPath(path);
        if (Path.GetFullPath(FilePath).Equals(snapshot, OperatingSystem.IsWindows() ?
                StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Restore requires a separate snapshot.");
        VerifyBackup(snapshot);
        using var source = OpenFile(snapshot, SqliteOpenMode.ReadOnly);
        ValidateSchema(source);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var db = OpenFile(FilePath, SqliteOpenMode.ReadWriteCreate);
        using (var exclusive = db.Command(null, "PRAGMA locking_mode = EXCLUSIVE"))
            exclusive.ExecuteScalar();
        using (var lockCheck = db.Command(null, "BEGIN EXCLUSIVE; COMMIT;"))
            lockCheck.ExecuteNonQuery();
        source.BackupDatabase(db);
    }

    private static void CopyDatabase(string source, string target)
    {
        using (File.Open(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
        using var input = OpenFile(source, SqliteOpenMode.ReadOnly);
        using var output = OpenFile(target, SqliteOpenMode.ReadWrite);
        input.BackupDatabase(output);
    }

    public bool HasDiskReserve()
    {
        var volume = DriveInfo.GetDrives().Where(d => FilePath.StartsWith(d.RootDirectory.FullName,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        return volume is not null && volume.AvailableFreeSpace >= options.MinimumFreeMegabytes * 1048576L;
    }

    public int? Ingest(AppScope scope, IEnumerable<TelemetryEvent> events, bool anonymous, DateTimeOffset now)
    {
        if (!HasDiskReserve())
            return null;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        int total = PendingCount(connection, transaction);
        int scoped = PendingCount(connection, transaction, (scope.Application, scope.Environment));
        int scopeMaximum = ScopeInboxMaximum(options.InboxMaximum);
        int accepted = 0;
        foreach (var item in events)
        {
            using var insert = connection.Command(transaction, """
                INSERT INTO telemetry(application, environment, id, occurred, received, version, session_id,
                    kind, name, outcome, duration_ms, status_code, trace_id, span_id, parent_span_id, category, value, anonymous)
                VALUES ($app, $env, $id, $at, $received, $version, $session, $kind, $name, $outcome,
                    $duration, $status, $trace, $span, $parent, $category, $value, $anonymous)
                ON CONFLICT(application, environment, id) DO NOTHING
                """, ("$app", scope.Application), ("$env", scope.Environment), ("$id", item.Id.ToString("D")),
                ("$at", item.OccurredAt.ToUnixTimeMilliseconds()), ("$received", now.ToUnixTimeMilliseconds()),
                ("$version", item.Version), ("$session", item.SessionId.ToString("D")), ("$kind", item.Kind),
                ("$name", item.Name), ("$outcome", item.Outcome), ("$duration", item.DurationMs),
                ("$status", item.StatusCode), ("$trace", item.TraceId), ("$span", item.SpanId),
                ("$parent", item.ParentSpanId), ("$category", item.Category), ("$value", item.Value),
                ("$anonymous", anonymous));
            accepted += insert.ExecuteNonQuery();
            if (accepted > 0 && (total + accepted > options.InboxMaximum ||
                scoped + accepted > scopeMaximum))
                return null;
        }
        transaction.Commit();
        return accepted;
    }

    private static int PendingCount(SqliteConnection connection, SqliteTransaction transaction,
        (string Application, string Environment)? scope = null)
    {
        using var count = scope is { } key
            ? connection.Command(transaction, "SELECT COUNT(*) FROM telemetry WHERE done IS NULL AND application=$app AND environment=$env",
                ("$app", key.Application), ("$env", key.Environment))
            : connection.Command(transaction, "SELECT COUNT(*) FROM telemetry WHERE done IS NULL");
        return checked((int)(long)count.ExecuteScalar()!);
    }

    private static int ScopeInboxMaximum(int inboxMaximum) =>
        Math.Min(inboxMaximum, Math.Max(100, inboxMaximum / 4));

    private static int ReplayInboxMaximum(int inboxMaximum) =>
        Math.Max(0, inboxMaximum - Math.Min(100, Math.Max(1, inboxMaximum / 10)));

    public void AddRelease(AppScope scope, ReleaseMarker marker, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.Command(null, """
            INSERT INTO releases VALUES ($app, $env, $version, $build, $commit, $at)
            ON CONFLICT(application, environment, build_id) DO NOTHING
            """, ("$app", scope.Application), ("$env", scope.Environment), ("$version", marker.Version),
            ("$build", marker.BuildId), ("$commit", marker.CommitId), ("$at", now.ToUnixTimeMilliseconds()));
        command.ExecuteNonQuery();
    }

    public object PlatformHealth(DateTimeOffset? now = null)
    {
        var evaluatedAt = now ?? DateTimeOffset.UtcNow;
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var (expiredRawRows, oldestExpired, retentionLag) = RawRetentionHealth(connection, transaction, evaluatedAt);
        using var statement = connection.Command(transaction, """
            SELECT COUNT(*), MIN(received),
                COALESCE((SELECT processed FROM worker_state WHERE id = 1), 0),
                (SELECT heartbeat FROM worker_state WHERE id = 1),
                (SELECT COUNT(*) FROM alert_deliveries WHERE delivery_state='failed'),
                (SELECT COUNT(*) FROM dead_letters),
                (SELECT COUNT(*) FROM alert_deliveries WHERE delivery_state='pending'),
                (SELECT MAX(backfill_until - backfill_cursor, 0) FROM rollup_state WHERE id = 1),
                (SELECT COUNT(*) FROM monitor_state WHERE state = 'Alert'),
                (SELECT COUNT(*) FROM config_changes WHERE status = 'Pending'),
                (SELECT COUNT(*) FROM audit_log),
                (SELECT COUNT(*) FROM alert_deliveries WHERE delivery_state='deferred')
            FROM telemetry WHERE done IS NULL
            """);
        using var rows = statement.ExecuteReader();
        rows.Read();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new
        {
            pending = rows.GetInt64(0), oldestPendingAt = rows.NullableTime(1), processed = rows.GetInt64(2),
            deadLetters = rows.GetInt64(5), notificationFailures = rows.GetInt64(4), notificationsPending = rows.GetInt64(6),
            workerHeartbeat = rows.NullableTime(3), rollupBackfillRemaining = rows.IsDBNull(7) ? 0 : rows.GetInt64(7),
            monitorsAlerting = rows.GetInt64(8), pendingChanges = rows.GetInt64(9), auditEntries = rows.GetInt64(10),
            notificationsDeferred = rows.GetInt64(11),
            expiredRawRows, oldestExpiredRawAt = oldestExpired is long at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : (DateTimeOffset?)null,
            rawRetentionLagSeconds = retentionLag / 1_000d,
            databaseBytes = new[] { "", "-wal", "-shm" }.Sum(suffix =>
                File.Exists(FilePath + suffix) ? new FileInfo(FilePath + suffix).Length : 0),
            freeDiskBytes = FreeDiskBytes(), processWorkingSetBytes = process.WorkingSet64,
            processCpuSeconds = process.TotalProcessorTime.TotalSeconds,
            processStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)
        };
    }

    private long? FreeDiskBytes() => DriveInfo.GetDrives().Where(d => FilePath.StartsWith(d.RootDirectory.FullName,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault()?.AvailableFreeSpace;

    public bool IsReady(DateTimeOffset now)
    {
        if (!HasDiskReserve())
            return false;
        using var connection = Open();
        ValidateSchema(connection);
        using var statement = connection.Command(null, "SELECT heartbeat FROM worker_state WHERE id = 1");
        return statement.ExecuteScalar() is long heartbeat && heartbeat >= now.AddSeconds(-90).ToUnixTimeMilliseconds();
    }
}

internal static class Sql
{
    public static SqliteCommand Command(this SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static DateTimeOffset Time(this SqliteDataReader row, int ordinal) =>
        DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(ordinal));
    public static DateTimeOffset? NullableTime(this SqliteDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.Time(ordinal);
    public static double? NullableDouble(this SqliteDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.GetDouble(ordinal);
    public static string? NullableString(this SqliteDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.GetString(ordinal);
}
