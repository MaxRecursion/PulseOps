# PulseOps operations

Use a dedicated Windows Server with local NTFS storage. PulseOps runs as one ASP.NET Core service with the portal, ingestion, queries, and processing in the same process. To isolate processing in a second process, use the same published executable in worker mode on that server. Both modes use the same SQLite database. SQLite WAL coordinates processes on one host and has one writer at a time. This deployment has a single-host failure domain. [SQLite WAL documentation](https://www.sqlite.org/wal.html)

## Fresh Windows Service installation

1. Provision a Windows Server supported by .NET 10, native PowerShell 7.4 or newer, and the approved **.NET 10 ASP.NET Core Runtime** for the server architecture. The installer requires 10.0.15 or a later .NET 10 servicing release. Allow inbound HTTPS from approved users and relays and outbound HTTPS to the configured identity provider and approved synthetic targets and webhooks. If email alerts are enabled, allow the processing host to reach the internal SMTP relay on its configured port. Provision space for the database, its WAL, snapshots, and the published releases. [Microsoft runtime downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
2. Create a DNS record such as `pulseops.internal.example`. Install a trusted TLS server certificate for that host with a private key in **Local Computer → Personal**. Use the exact certificate subject in the Kestrel settings, and ensure the certificate covers the chosen DNS name.
3. Register a confidential OIDC web client. Register the exact sign-in redirect `https://pulseops.internal.example/signin-oidc`. Configure authorization-code flow with PKCE, emit the role and group claims described below, and provision a client secret through the approved secret process. Test with a platform administrator and a scoped support user. [ASP.NET Core OIDC guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)
4. Obtain the reviewed Windows ZIP and the manifest SHA-256 from the trusted release record. The ZIP includes these instructions and the Windows and IIS setup scripts. In elevated PowerShell 7, extract and verify it in an administrator-controlled folder before running a bundled script. The example uses release 0.1.0. Substitute your approved version in the ZIP name. Copy the ZIP to `C:\Staging` and obtain `manifest.sha256` separately from the trusted release record. [Build and artifact guide](../deployment/teamcity/README.md)

   ```powershell
   $releaseDirectory = 'C:\Staging\PulseOps-release'
   $approvedManifestHash = (Get-Content 'C:\Staging\manifest.sha256' -Raw).Trim()
   Expand-Archive -LiteralPath 'C:\Staging\PulseOps-0.1.0-win-x64.zip' -DestinationPath $releaseDirectory
   $manifestPath = Join-Path $releaseDirectory 'manifest.json'
   if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $approvedManifestHash) {
       throw 'Release manifest failed verification.'
   }
   $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
   foreach ($entry in $manifest.files) {
       $file = Join-Path $releaseDirectory $entry.path
       if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
           throw "Release file failed verification: $($entry.path)"
       }
   }
   Set-Location $releaseDirectory
   ./deployment/windows/PulseOps.ps1 Initialize
   ```

5. Write the private config at `C:\ProgramData\PulseOps\appsettings.Production.json`. Enter the approved values for your environment.

   ```json
   {
     "AllowedHosts": "pulseops.internal.example",
     "Kestrel": {
       "Endpoints": {
         "Https": {
           "Url": "https://*:443",
           "Certificate": {
             "Subject": "pulseops.internal.example",
             "Store": "My",
             "Location": "LocalMachine",
             "AllowInvalid": false
           }
         }
       }
     },
     "PulseOps": {
       "DatabasePath": "C:\\ProgramData\\PulseOps\\data\\pulseops.db",
       "DataProtectionPath": "C:\\ProgramData\\PulseOps\\keys",
       "EmbeddedWorker": true,
       "Demo": false,
       "RetentionDays": 14,
       "ErrorRetentionDays": 30,
       "IncidentRetentionDays": 180,
       "RollupHourlyDays": 32,
       "RollupRetentionDays": 180,
       "InboxMaximum": 100000,
       "MinimumFreeMegabytes": 1024,
       "Auth": {
         "Authority": "https://identity.internal.example/tenant",
         "ClientId": "pulseops-confidential-client",
         "ClientSecret": "<secret supplied by the approved process>",
         "RoleClaim": "role",
         "GroupsClaim": "groups"
       },
       "Scopes": [
         {
           "Application": "finance-dashboard",
           "Environment": "production",
           "Label": "Finance Dashboard",
           "ViewerGroups": ["<production support group claim value>"],
           "AllowedNames": [
               "reports", "sap-report", "generate-report", "csv-parse", "report-health",
               "managed-exception", "render-exception", "unobserved-task", "javascript-error", "promise-rejection",
               "lcp", "inp", "cls", "fcp", "ttfb", "navigation-duration", "long-task-count",
               "opened", "online", "offline", "visible", "hidden", "ended"
             ],
           "IngestToken": "<unique random machine secret>",
           "StartupOrigins": []
         }
       ],
       "Rules": [
         {
           "Application": "finance-dashboard",
           "Environment": "production",
           "Kind": "api",
           "Name": "sap-report",
           "WindowMinutes": 5,
           "MinimumSamples": 10,
           "MinimumFailures": 3,
           "FailurePercent": 20,
           "Severity": "high"
         }
       ]
     }
   }
   ```

6. Validate the runtime and managed installation paths, then register the release. The application validates policy settings during migration and startup. The digest must come from a trusted release record. The deploy script checks the manifest and every file, stages a versioned release, migrates the database with the release executable, and registers service recovery.

   ```powershell
   $pulseOpsOrigin = 'https://pulseops.internal.example'
   $pulseOpsInstall = './deployment/windows/PulseOps.ps1'
   & $pulseOpsInstall Validate
   & $pulseOpsInstall Deploy -ReleaseDirectory $releaseDirectory `
       -ManifestSha256 $approvedManifestHash -DeferStart
   ```

7. In Certificates MMC use **Manage Private Keys** to grant `NT SERVICE\PulseOps` read access. The optional detached worker needs no TLS key. Start the deployment and allow the installer to probe both health endpoints.

   ```powershell
   & $pulseOpsInstall Start -HealthUrl $pulseOpsOrigin
   ```

8. Open the HTTPS portal and confirm that both the administrator and the scoped user receive their intended access. Verify that the scoped user receives no events from a fixture application outside their OIDC groups. Configure independent HTTPS probes for `/health/live` and `/health/ready`. Follow the [Blazor application onboarding procedure](#onboard-a-monitored-blazor-application) for each application that will send telemetry.

`NT SERVICE\PulseOps` has read and execute permissions for binaries, read permissions for private configuration, and modify permissions for SQLite files and Data Protection keys. The managed data directory and key directory are inherited by their contents. If a private config file was replaced, run the managed `Start` operation to reapply its access rules. Existing processes load config on startup. Use `Stop` then `Start` for config changes. With a custom `-Root`, set the two config paths to that root's `data\pulseops.db` and `keys` entries. Supply `-Root` consistently to operations commands. [Windows virtual service identities](https://learn.microsoft.com/windows/security/identity-protection/access-control/service-accounts)

### Identity and application registration

Assign operator roles and group claims at the identity provider. Each non-administrator sees only scopes whose `ViewerGroups` contain one of their exact group claims. Use stable group IDs, set `RoleClaim` and `GroupsClaim` to the emitted claim names, and test group overage with the identity team. The audit records the operator’s OIDC subject.

| Operator role | Actions |
| --- | --- |
| PlatformAdministrator | Read every scope and platform health, manage incidents, propose and decide changes, verify and export the audit log. |
| ApplicationOwner | Manage incidents, read change history, propose changes and withdraw own pending proposals in visible scopes. |
| ProductionSupport | Investigate and manage incidents in visible scopes. |
| Developer | Investigate telemetry, monitors, incidents and maintenance windows in visible scopes. |
| ReadOnlyAuditor | Read scoped investigations, changes and audit entries and export the scoped log. |

Add an entry to `Scopes` for each application and environment combination. Use lowercase slug identifiers up to 40 characters and explicit telemetry aliases. Give each combination a distinct random ingest token, with at least 32 characters. Send ordinary browser telemetry to an authenticated same-origin relay in the monitored application. The relay must bind the application/environment on the server, check CSRF and body size, and attach the token on its outgoing HTTPS request to PulseOps. The demo relay is an integration sample for local use. Register the SDK’s session, error and metric names for enabled captures as shown in the example. A single unregistered alias causes the collector to reject the whole batch. Keep `StartupOrigins` empty unless you intend to collect restricted anonymous milestones. When enabled, register each exact HTTPS origin with no trailing slash.

Use ASP.NET Core environment keys such as `PulseOps__Auth__ClientSecret` and `PulseOps__Scopes__0__IngestToken` for an approved secret injection mechanism. The private JSON provider takes precedence over bundled defaults and environment values override the private file. When using the Windows wrapper, keep the machine environment neutral for the managed database/key paths, processing topology, and runtime environment selection. Restart the affected processes after config or secret changes. Keep secrets out of release archives, terminal transcripts, screenshots, logs, and browser assets.

### Onboard a monitored Blazor application

1. The PulseOps operator registers the application and environment, agrees on the telemetry aliases with the application owner, and grants the support group view access. Register staging and production as separate scopes. Provision each scope's machine credential directly to its relay owner through the approved secret process.
2. The application owner adds the SDK to the existing .NET 10 Blazor WebAssembly project. The relay owner configures an authenticated POST on the application origin, binds the scope server-side, and forwards to the collector over HTTPS. Start with one instrumented API client and one business operation in staging. Enable other signal types and approve their aliases as needed. [SDK integration and verification](SDK.md)
3. In a fresh staging tab, perform an approved synthetic action. Verify a 202 returned by the application relay and find the matching processed observation in **Event explorer** with the intended **Application**, **Environment**, and **Version**. Check that a scoped support account can read it and that an account outside the scope cannot. In staging, exercise an authorized failure and confirm that the business app remains usable when telemetry transport is unavailable.
4. Record the application and SDK versions, scope, test time in UTC, aliases, HTTP status, and scoped portal view in the release evidence. Keep raw request data and credentials out of the evidence. Repeat a known-good check after production approval using an approved dry-run action. Give support a filtered portal link and the relay owner's contact. [Application reporting and investigation](Support.md#create-a-custom-report-for-a-blazor-app)

### Worker topology and probes

Set `PulseOps.EmbeddedWorker` to `false` before deploying with a detached worker. The deployment scripts register `PulseOpsWorker` with `--worker`, grant its virtual identity access to the managed data, and start it before the web service. Re-deploy an approved release when changing topology. Use embedded mode for the initial single-process setup.

To enable automatic incident recovery for a rule, set `RecoveryPercent` strictly below its opening `FailurePercent` and choose `RecoveryWindows` from two to six. Every consecutive post-opening window must satisfy the recovery percentage and minimum sample count. A scope with too little recovery data remains open for operator investigation.

A synthetic check uses an approved HTTPS URL and a registered telemetry alias. The worker performs a GET with redirects disabled, validates TLS and records status, duration and coarse failure categories such as DNS, connection or TLS. A successful check nearing certificate expiry reports a degraded TLS result. `CertificateWarningDays` defaults to 14 and accepts 0 to disable the expiry warning. Restrict outbound destinations at the host or firewall.

### Configure monitors and probes

Merge these properties into the existing `PulseOps` object in the private configuration. Here `report-health` and `sap-report` are registered aliases in the example scope. Restart the configured processes after changing the file.

```json
{
  "MonitorIntervalSeconds": 60,
  "Probes": [
    {
      "Application": "finance-dashboard", "Environment": "production", "Name": "report-health",
      "Url": "https://reports.internal.example/health/ready",
      "ExpectedStatus": 200, "IntervalSeconds": 60, "CertificateWarningDays": 14
    }
  ],
  "Monitors": [
    {
      "Application": "finance-dashboard", "Environment": "production", "Name": "sap-report-p95",
      "Type": "latency", "Kind": "api", "Target": "sap-report", "Percentile": 95,
      "ThresholdMs": 1200, "RecoveryMs": 900, "WindowMinutes": 10,
      "MinimumSamples": 20, "EvaluationDelayMinutes": 1, "RecoveryWindows": 2, "Severity": "high"
    },
    {
      "Application": "finance-dashboard", "Environment": "production", "Name": "report-health-missing",
      "Type": "no-data", "Kind": "synthetic", "Target": "report-health",
      "WindowMinutes": 5, "NoDataMinutes": 10, "Severity": "high"
    }
  ]
}
```

The Monitors page shows the latest evaluation, measured value and incident state. Latency monitors can also require a ratio against preceding complete UTC hours using `BaselineDays` and `BaselineRatio`. No-data monitors measure elapsed time since trusted receipt and allow a grace period for a new signal. Regression monitors compare the latest recorded deployment with the preceding distinct version. Submit release markers and matching event versions before evaluating a regression. A window with too little eligible data appears as insufficient. Keep historical baseline and comparison periods within the relevant hourly rollup and raw retention windows. [Release marker setup](../deployment/teamcity/README.md)

### Approvals, maintenance and auditing

To add a monitor without editing host configuration, open **Administration**, propose a monitor for an authorized scope and enter an operational reason. A second operator with the PlatformAdministrator role reviews and approves or rejects the proposal. The proposer can withdraw it while pending. The same workflow schedules and cancels maintenance. During an active window, an incident transition still records its history, and a new Open or Reopened notification is deferred until the window ends or is cancelled; it is then sent if the incident is still open, so an outage that outlasts the window is never silenced. A later transition replaces anything still deferred. A resolution is sent only to destinations that were already told the incident is open, including during a window, so a page sent before maintenance is always closed. Proposal reasons are kept with the change request and shown in Administration; the immutable audit chain records only a digest of the payload, so reasons can be corrected or redacted without breaking verification. Include the support ticket reference in reasons and keep customer data, secrets and telemetry payloads out of reasons and notes.

| Setting or limit | Operational behavior |
| --- | --- |
| `Administration.ChangeExpiryHours` | Defaults to 72, accepts 1–720. A pending proposal expires at its deadline. |
| `Administration.MaximumPendingChanges` | Global queue, default 50, permitted 1–500. Decide or withdraw outstanding proposals to free capacity. |
| `Administration.AllowSelfApproval` | Production requires `false`. The explicitly enabled local demo permits one operator to exercise the workflow. |
| Managed monitors | Up to 300 defined in the portal plus 300 in configuration. At capacity, approval of an additional monitor returns a conflict. A separate approved deletion frees capacity for a retry while the proposal remains pending. Existing monitors can be updated at capacity. |
| Maintenance | A scheduled window identifies one application/environment. Maximum duration is seven days, with a start at most 90 days in the future. |

Configuration-file monitors are read-only in the portal. An approved portal change applies with its audit entry in one SQLite transaction. If another revision or a host configuration change invalidates the proposal, review the new state and submit a fresh proposal. Use the host’s normal change control for identity, scopes, secrets, deployment settings and configuration-file edits.

Open the **Audit log** in Administration to verify the chain and inspect events. Its NDJSON download contains the caller’s visible rows for the requested period. To archive a verified full-chain snapshot, run the matching staged executable with an absolute config path. Use an approved account with access to that database and a protected output directory. Substitute the active release for the example version.

```powershell
$installRoot = 'C:\ProgramData\PulseOps'
$serverExe = Join-Path $installRoot 'releases\0.1.0\server\PulseOps.Server.exe'
$privateConfig = Join-Path $installRoot 'appsettings.Production.json'
$auditFile = Join-Path $installRoot ("backups\audit-{0}.ndjson" -f [guid]::NewGuid())
& $serverExe --config $privateConfig --export-audit $auditFile
if ($LASTEXITCODE -ne 0) { throw 'Audit verification or export failed.' }
```

The full export requires a new file, checks the chain in one database snapshot, and prints its entry count and SHA-256 head. Keep the file, count and head in separately protected storage. Comparing with a prior trusted export detects rewritten or truncated history. If verification fails, preserve the database and snapshots for investigation. The scoped portal download can omit links belonging to other scopes.

### Route incident notifications

Set the public `PortalOrigin` to the exact HTTPS origin without a trailing slash. Merge the following settings into `PulseOps` and replace the hosts and addresses with approved internal destinations. An empty application or environment filter includes every value on that axis. Route names are unique across both adapters.

```json
{
  "PortalOrigin": "https://pulseops.internal.example",
  "Webhooks": [
    {
      "Name": "reporting-pager", "Url": "https://alerts.internal.example/pulseops",
      "Application": "finance-dashboard", "Environment": "production", "Severities": ["critical"]
    }
  ],
  "Smtp": {
    "Host": "mail.internal.example", "Port": 587, "UseTls": true,
    "From": "pulseops@internal.example", "TimeoutSeconds": 15
  },
  "EmailRoutes": [
    {
      "Name": "reporting-oncall", "Recipients": ["reporting-oncall@internal.example"],
      "Application": "finance-dashboard", "Environment": "production", "Severities": ["high", "critical"]
    }
  ]
}
```

`UseTls: true` requires the relay to advertise STARTTLS and present a trusted certificate matching its host. Use a STARTTLS listener such as port 587. An implicit-TLS-only listener on 465 is incompatible with this sender. Configure `Smtp.UserName` and `Smtp.Password` together when relay authentication is required. Production permits cleartext only for a loopback relay without credentials. Inject credentials and optional webhook tokens through the private host configuration, for example `PulseOps__Smtp__Password` or `PulseOps__Webhooks__0__BearerToken`. A route accepts 1–20 mailbox addresses. Use approved group mailboxes when possible.

The outbox stores matching incident transitions. The worker rechecks the destination’s current scope and severity before dispatch. Email and webhook messages identify the incident and provide a portal link. Evidence remains in the authenticated portal. Webhooks carry an `Idempotency-Key` plus a monotonic `sequence` and the transition time `changedAt`; email carries a stable `X-PulseOps-Delivery` value. Transitions for one incident and destination leave in order: a newer one waits while an older one is still retrying, and receivers should ignore a transition whose `sequence` is lower than the last one they applied for that incident. Receivers can see duplicate attempts after a timeout or restart. When a relay accepts some recipients and refuses others, the delivery counts as sent so accepted recipients are not mailed again; the warning log records only the accepted and total counts and the reply code. SMTP 4xx replies and connection failures retry with exponential backoff and remain pending until accepted or otherwise permanently rejected. Webhooks retry 408, 429 and 5xx responses and use bounded `Retry-After` guidance. Permanent destination or recipient rejections remain failed and visible for operator action; automatic audited requeue is not yet available. Test each route with a labeled staging incident and check delivery counters in Platform before enabling production filters.

### Health and first response

`/health/live` confirms the web process is serving requests. `/health/ready` includes storage/schema checks and recent worker heartbeat. Both return minimal status with no scope data. Use the platform page or the administrator API for detailed data. In detached mode examine both service statuses. In IIS inspect the application pool and Windows Application Event Log.

```powershell
./deployment/windows/PulseOps.ps1 Verify -HealthUrl https://pulseops.internal.example
Get-Service PulseOps, PulseOpsWorker -ErrorAction SilentlyContinue
```

| Symptom | First checks |
| --- | --- |
| TLS connection fails | DNS, Windows Firewall, service status, certificate binding, trust chain, and private-key access |
| Liveness fails on a running host | Windows Application Event Log, process binding, and runtime version |
| Readiness returns 503 | Worker registration or restart loop, shared config path, schema status, data directory ACLs, write contention, and disk reserve |
| Ordinary batches return 401 | Scope identifiers and the relay's scoped bearer secret |
| Startup batches return 403 | Registered exact origin and whether anonymous collection is enabled for that scope |
| Batches return 410 | The scope has `IntakeSuspended` enabled. Coordinate reactivation with the application owner. |
| Batches return 400 | Schema version, batch count, aliases and fixed categories, clock skew, and strict JSON fields |
| Batches return 429 | Server `Retry-After`, relay request fan-in behind one IP, pending queue, disk reserve, and retention |
| Batches return 503 | SQLite lock contention, file access, and I/O errors |
| Portal appears empty | Sign-in role, viewer groups, selected scope and interval, receipt and processing state |
| Incident did not open | Rule scope, received-time window, sample minimum, failure threshold, and whether observations were anonymous |

Failure incidents are evaluated over their server receipt-time window. If processing resumes after that window has elapsed, PulseOps records a clearly labeled historical opening or reopening. If new observations meet the rule while that incident is still open or reopened, PulseOps sends the matching notification once. Track oldest-pending age alongside worker heartbeat to tell whether incidents are being evaluated on time.

### Replay quarantined receipts

In Platform, check **dead letters**, **oldest pending** and **worker heartbeat**. A record-specific processing failure can quarantine an already accepted event. A systemic storage failure can instead leave a backlog and stale heartbeat. First fix the processor or storage issue, verify the current backup and check available disk and inbox headroom. Use the same matching executable and config paths defined in the audit example.

```powershell
& $serverExe --config $privateConfig --replay-dead-letters
if ($LASTEXITCODE -ne 0) { throw 'Dead-letter replay failed.' }
```

Each invocation scans up to 10,000 records in short transactions, checks the disk reserve for each slice, and observes the global and per-scope inbox limits while leaving headroom for live intake. It reports returned, duplicate, capacity-deferred and remaining counts; deferred records stay quarantined for a later invocation. Completed slices remain committed if a later slice fails. Verify that the worker drains the inbox, dead letters decrease and the same failures do not recur before repeating. Replay works on the local SQLite database and requires operator filesystem access.

## Backup, restoration, and upgrades

Create regular SQLite snapshots according to the service's recovery-point objective. The backup script uses SQLite's online backup API and verifies integrity before moving the result into its final name. Copy verified snapshots to approved encrypted backup storage. Apply retention to backup copies separately from the application database. Also secure the private settings, certificate recovery material, and Data Protection keys according to the operations team's recovery process. Keep backup storage outside the monitored host for host-loss recovery. [SQLite Online Backup API](https://www.sqlite.org/backup.html)

```powershell
./deployment/windows/PulseOps.ps1 Backup
./deployment/windows/PulseOps.ps1 VerifyBackup -SnapshotPath 'C:\ProgramData\PulseOps\backups\approved.db'
```

The script defaults to a unique timestamped file under `backups` on the installation volume. A full snapshot needs room in addition to the live database and its journals. Prefer a protected, separate staging volume for scheduled backups and set local retention so snapshots leave the configured database reserve available. Use an external job to copy and verify each snapshot off-host. Monitor the age of the latest verified off-host recovery point from outside the PulseOps host. Keep SQLite journal sidecars with the live database and use the backup command for portable snapshots. Regularly restore into a separate isolated installation and measure RPO and RTO.

Use the staged release that understands the snapshot's schema. Run `VerifyBackup -Version <release>` before a planned stop to check the snapshot's integrity and schema compatibility. Database commands have a 30-minute timeout per process. Set `-DatabaseCommandTimeoutSeconds` for operations whose measured duration requires more time. After a timeout, confirm that the maintenance process has exited and inspect the database and services before retrying.

For a planned upgrade, export your current protected config, record the current release and manifest hash, and keep the previous build in the versioned release directory. Run `Deploy` with the new extracted artifact and trusted digest. The deployment takes a verified pre-release snapshot while hosts are stopped, runs the new release's migrator, and gates startup on health. A failed upgrade leaves service registrations stopped or disabled for recovery. Inspect the Event Log and record the last snapshot path printed by the script.

Restore replaces the live data with the selected snapshot. Take both hosts and any other local process using the database offline first. Restoring an older snapshot rewinds incident activity and telemetry to the backup point. The wrapper creates a pre-restore snapshot when the live database is available. On Windows Service installations, restore registers the selected release and leaves its services disabled until `Start`. For IIS, use the IIS procedure below to select and start the release. Use an authorized clean recovery installation for an unreadable database that cannot be backed up. On Windows Service installations, omit `-Version` only if the currently registered release matches the snapshot's schema.

```powershell
$install = './deployment/windows/PulseOps.ps1'
$snapshot = 'C:\ProgramData\PulseOps\backups\approved.db'
$release = '0.1.0'
& $install VerifyBackup -SnapshotPath $snapshot -Version $release
& $install Stop
& $install Restore -SnapshotPath $snapshot -Version $release
& $install Migrate -Version $release
& $install Start -HealthUrl 'https://pulseops.internal.example'
```

Use `Rollback -Version <old-version> -HealthUrl <origin>` to reactivate an earlier staged release with a compatible schema. Add `-SnapshotPath <pre-upgrade-snapshot>` when the prior binary requires the earlier database. Use the matching private configuration version. A snapshot-based rollback supersedes intervening records. Verify health, then verify SSO, scope access, a new dry-run event, and worker processing. Use `Remove` to remove the SCM registrations while preserving releases, backups, config, and data.

| SQLite records | Retention on the live host |
| --- | --- |
| Processed raw events | `RetentionDays` defaults to 14 (1–90). Pending receipts remain available for processing. |
| Error events | `ErrorRetentionDays` defaults to 30, bounded by the active raw and incident retention settings. Explicit values must be at least raw retention and at most 90 days and incident retention. |
| Hourly and daily rollups | `RollupHourlyDays` defaults to 32 (2–90). Daily `RollupRetentionDays` defaults to 180, at least hourly retention and at most 3650 days. History uses UTC buckets and bounded histograms for latency percentiles. |
| Resolved incidents and releases | `IncidentRetentionDays` defaults to 180 and accepts up to 3650 days. Recent incident history can extend the lifetime of a resolved incident. |
| Quarantined events | Dead-letter retention uses `RetentionDays` from quarantine time. |
| Audit chain | Append-only. Archive verified exports and budget disk for continued growth. |

After upgrading an earlier database, the worker backfills history from retained, processed receipts. Watch `rollupBackfillRemaining` in platform health until it reaches zero. Historical periods whose raw data expired before the upgrade cannot be reconstructed. A database restore can also rewind audit history and revive queued alerts. Compare the restored chain with the off-host archive and reconcile deliveries with receivers using the delivery ID before restoring normal traffic.

SQLite retention removes up to 5,000 of the oldest eligible processed telemetry records per pass. When a pass reaches that bound, the worker schedules another pass after one second. Otherwise, the normal interval is 30 seconds. Platform health reports the expired raw row count, oldest expired receipt, and maximum time a record has exceeded its own retention period. Open incidents remain available beyond the resolved-incident retention value. The database may reuse freed pages without shrinking its on-disk file, so monitor actual free space and WAL size as well as recent ingestion. Arrange a maintenance window for any separate compaction after a verified backup. Observe expiry lag, ingest latency, and disk/WAL headroom under the target workload before sizing long-term retention.

## IIS alternative

Use one HTTPS IIS site at the host root. Install IIS, Management Scripts and Tools, Application Initialization, and WebSocket Protocol if enabled in your environment. Install the .NET 10 Hosting Bundle after IIS, or repair the bundle after enabling IIS. Provide a trusted certificate in LocalMachine `My`. Configure a dedicated `PulseOps` application pool and route browser users through application OIDC. Enable IIS Anonymous Authentication and use app-level OIDC at PulseOps endpoints. [Microsoft IIS hosting prerequisites](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0)

1. In elevated PowerShell 7, run Windows `Initialize` and `Stage` with the approved artifact/digest. Use the same private JSON with `PulseOps.EmbeddedWorker` set to `true` and omit the `Kestrel` section so IIS terminates TLS. Keep the same site DNS name in `AllowedHosts` and the OIDC callbacks. Remove any pre-existing PulseOps Windows Service registrations when converting an installation.
2. In elevated **Windows PowerShell 5.1**, run `deployment/iis/Configure-Iis.ps1 -Version <release> -DnsName <fqdn> -CertificateThumbprint <sha1-thumbprint>`. It configures the SNI HTTPS site, dedicated 64-bit pool, protected external config arguments, and file permissions, leaving the pool and site stopped. IIS request filtering is set to 1 MiB so the application's own 128 KiB limit answers 413, as on Kestrel, and the `Server` header is removed on IIS 10 version 1709 (Windows Server 2019) or later. On older IIS the script warns and the staging smoke reports the header.
3. In elevated PowerShell 7, run `./deployment/windows/PulseOps.ps1 Migrate -Version <release>`. Start the `PulseOps` pool and site from IIS Manager. Run `Verify -HealthUrl <https-origin>`. After verification, enable auto-start for the site and pool in IIS and keep application initialization/preload enabled. Use an independent health probe to detect site initialization failures or recycling gaps.
4. For maintenance and upgrades, disable site and pool auto-start, stop both, then wait for the associated worker process to exit. Verify or create a snapshot using the current staged version, stage and configure the new version, migrate, restart, and verify. For a rollback that needs a snapshot, verify it with the previous staged executable before stopping the site. After both the site and pool stop, configure the previous IIS release, restore and migrate with that release, then verify before re-enabling auto-start. Always pass the staged `-Version` for IIS database commands.

## Production acceptance checks

- Install, start, stop, and recover the Windows services on a representative host. Document applicable machine GPOs, certificate private-key ACLs, Event Log source, and a tested exit procedure.
- Verify OIDC login, failed login, logout behavior, session expiry, claim emission, allowed-scope reads and writes, cross-scope denial, and secret rotation with the enterprise provider.
- Prove the safe relay, input rejection, fail-open client behavior, and rate limiting through the actual reverse proxy and monitored workload. Record an accepted batch and its matching processed event for each application being onboarded. [Blazor application onboarding](#onboard-a-monitored-blazor-application)
- Test a verified offline restore and a release rollback. Measure readiness recovery, queue backlog, disk pressure thresholds, and diagnostic visibility after a restart.
- Measure the target concurrency, peak throughput, dashboard latency, and storage growth on the chosen host. Archive the test method and results with the release. [Local benchmark and commands](Performance.md)
- Record security, operational, dependency approval and change-management sign-off for the intended environment.

Windows service registration, IIS configuration, and enterprise OIDC tests require a representative Windows environment and identity provider. Consult [implementation status](../IMPLEMENTATION_STATUS.md) for repository-level validation.

## Production FAQ

### Can a static Blazor WebAssembly site send managed telemetry?

Yes. Serve an authenticated relay through the site's own origin, for example through the application's backend or gateway. The browser SDK uses that route with the application's session. The relay supplies the collector's scope credential from its server-side secret store. Optional anonymous startup reporting uses its own collector route with an exact origin allowlist and a restricted event schema. [Configure the app-owned relay](SDK.md#configure-the-app-owned-relay)

### How do I know the SDK is working end to end?

Generate a known staging operation in a fresh browser tab. Look for HTTP 202 on the application relay request, then open **Event explorer** and match the event type, alias, version and UTC time in the registered scope. The 202 proves durable intake when the relay forwards the collector result. The matching portal event proves the worker processed it and the operator can see it. [Intake contract](Telemetry.md#batch-contract) · [Staging verification procedure](SDK.md#verify-the-integration-in-staging)

### Why did a batch return 202 while the portal looks empty?

Check the app, environment, version and time filters, then refresh. In the collector response, `accepted: 0` with a positive `duplicates` count means the IDs were already received. A batch with new receipts can still be waiting for processing. Ask a platform administrator to check **Platform health** for the worker heartbeat and oldest pending item. If the application or environment is missing from the picker, verify scope registration and the signed-in operator's role and group claims. [Health and first response](#health-and-first-response) · [Identity and application registration](#identity-and-application-registration)

### Why is there an event but no incident?

An incident requires a matching rule and enough qualifying observations in its window. A single dry run verifies telemetry delivery. Check the rule's scope, alias, minimum sample and failure counts, and threshold. Anonymous startup observations are excluded from trusted incident evaluation. [Health and first response](#health-and-first-response) · [Configure monitors and probes](#configure-monitors-and-probes)

### Can I create a custom report for one application?

Select its **Application** and **Environment**, set the **Time range** and optional **Version**, then choose the view that answers the question. Use **Overview** for overall health, **API explorer** for endpoint aliases, **Event explorer** for matching observations, and **History** for trends over longer periods. Bookmark or share the resulting URL with an authorized teammate. The link reruns the selected filters against a rolling window. Named reports, custom widgets, scheduled delivery, and telemetry CSV export are outside the current dashboard feature set. [Application reporting and drill-down](Support.md#create-a-custom-report-for-a-blazor-app)

### Why do dashboard counts differ from the application's business totals?

The portal counts processed telemetry observations. Sampling, capture flags, network interruptions, and the selected filters affect coverage. An operation alias reports outcome and duration without collecting the report output or financial data. Use the application's approved system of record for business totals. [SDK event fields](SDK.md#what-the-sdk-records) · [Scope and time ranges](Support.md#scope-and-time-ranges)

### Will unsent telemetry survive a browser reload?

The current SDK queue is bounded and in memory. Transient failures can retry while the tab is alive. Closing or reloading a tab can lose queued observations. Verify fail-open behavior in staging by blocking only the telemetry route and repeating a safe business action. [SDK reliability and rollout](SDK.md#reliability-and-rollout)
