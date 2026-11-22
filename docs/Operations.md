# PulseOps operations

Use a dedicated Windows Server with local NTFS storage. PulseOps runs as one ASP.NET Core service with the portal, ingestion, queries, and processing in the same process. To isolate processing in a second process, use the same published executable in worker mode on that server. Both modes use the same SQLite database. SQLite WAL coordinates processes on one host and has one writer at a time. This deployment has a single-host failure domain. [SQLite WAL documentation](https://www.sqlite.org/wal.html)

## Fresh Windows Service installation

1. Provision a Windows Server supported by .NET 10, native PowerShell 7.4 or newer, and the approved **.NET 10 ASP.NET Core Runtime** for the server architecture. The installer requires 10.0.15 or a later .NET 10 servicing release. Allow inbound HTTPS from approved users and relays and outbound HTTPS to the configured identity provider and explicitly approved synthetic targets. Provision space for the database, its WAL, snapshots, and the published releases. [Microsoft runtime downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
2. Create a DNS record such as `pulseops.internal.example`. Install a trusted TLS server certificate for that host with a private key in **Local Computer → Personal**. Use the exact certificate subject in the Kestrel settings, and ensure the certificate covers the chosen DNS name.
3. Register a confidential OIDC web client. Register the exact sign-in redirect `https://pulseops.internal.example/signin-oidc`. Configure authorization-code flow with PKCE, emit the role and group claims described below, and provision a client secret through the approved secret process. Test with a platform administrator and a scoped support user. [ASP.NET Core OIDC guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)
4. Obtain the reviewed Windows ZIP and the manifest SHA-256 from the trusted release record. The ZIP includes these instructions and the Windows and IIS setup scripts. In elevated PowerShell 7, extract and verify it in an administrator-controlled folder before running a bundled script. The example assumes the reviewed ZIP and digest were copied to `C:\Staging`. [Build and artifact guide](../deployment/teamcity/README.md)

   ```powershell
   $releaseDirectory = 'C:\Staging\PulseOps-release'
   $approvedManifestHash = (Get-Content 'C:\Staging\PulseOps-0.1.0-win-x64.manifest.sha256' -Raw).Trim()
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
       "IncidentRetentionDays": 180,
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
           "AllowedNames": ["sap-report", "generate-report", "csv-parse"],
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

6. Validate and register the release. The digest must come from a trusted release record. The deploy script checks the manifest and every file, stages a versioned release, migrates the database with the release executable, and registers service recovery.

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

8. Open the HTTPS portal and confirm that both the administrator and the scoped user receive their intended access. Verify that the scoped user receives no events from a fixture application outside their OIDC groups. Configure the monitored application's server relay and SDK, send an approved dry-run event, and confirm it appears in the selected application/environment. Configure independent HTTPS probes for `/health/live` and `/health/ready`. [SDK setup](SDK.md)

`NT SERVICE\PulseOps` has read and execute permissions for binaries, read permissions for private configuration, and modify permissions for SQLite files and Data Protection keys. The managed data directory and key directory are inherited by their contents. If a private config file was replaced, run the managed `Start` operation to reapply its access rules. Existing processes load config on startup. Use `Stop` then `Start` for config changes. With a custom `-Root`, set the two config paths to that root's `data\pulseops.db` and `keys` entries. Supply `-Root` consistently to operations commands. [Windows virtual service identities](https://learn.microsoft.com/windows/security/identity-protection/access-control/service-accounts)

### Identity and application registration

`PlatformAdministrator` sees all scopes. `ApplicationOwner` and `ProductionSupport` may manage incidents in scopes matched by a configured viewer group. `Developer` and `ReadOnlyAuditor` have read access to their matched scopes. Every scoped user needs an accepted role and an exact group claim match. Prefer stable group IDs. Configure claim names from your provider with `RoleClaim` and `GroupsClaim`. Review group overage behavior and role assignment with the identity team.

Add an entry to `Scopes` for each application and environment combination. Use lowercase slug identifiers up to 40 characters and explicit telemetry aliases. Give each combination a distinct random ingest token, with at least 32 characters. Send ordinary browser telemetry to an authenticated same-origin relay in the monitored application. The relay must bind the application/environment on the server, check CSRF and body size, and attach the token on its outgoing HTTPS request to PulseOps. The demo relay is an integration sample for local use. Keep `StartupOrigins` empty unless you intend to collect restricted anonymous milestones. When enabled, register each exact HTTPS origin with no trailing slash.

Use ASP.NET Core environment keys such as `PulseOps__Auth__ClientSecret` and `PulseOps__Scopes__0__IngestToken` for an approved secret injection mechanism. The private JSON provider takes precedence over bundled defaults and environment values override the private file. When using the Windows wrapper, keep the machine environment neutral for the managed database/key paths, processing topology, and runtime environment selection. Restart the affected processes after config or secret changes. Keep secrets out of release archives, terminal transcripts, screenshots, logs, and browser assets.

### Worker topology and probes

Set `PulseOps.EmbeddedWorker` to `false` before deploying with a detached worker. The deployment scripts register `PulseOpsWorker` with `--worker`, grant its virtual identity access to the managed data, and start it before the web service. Re-deploy an approved release when changing topology. Use embedded mode for the initial single-process setup.

To enable automatic incident recovery for a rule, set `RecoveryPercent` strictly below its opening `FailurePercent` and choose `RecoveryWindows` from two to six. Every consecutive post-opening window must satisfy the recovery percentage and minimum sample count. A scope with too little recovery data remains open for operator investigation.

Add scheduled probes under `PulseOps.Probes`. Each item accepts `Application`, `Environment`, `Name`, `Url`, `IntervalSeconds` and `ExpectedStatus`. Add the same name to the scope's `AllowedNames`. Use fixed approved HTTPS health URLs, without credentials or sensitive query strings. The worker uses read-only GET, no redirects, and regular certificate validation. Place network restrictions around the service so DNS changes cannot redirect probes into an unexpected trust zone. Probe data records observed HTTPS status and elapsed time. Independent certificate-expiry and DNS monitor coverage belongs in the host's operational monitoring.

### Incident notifications

Set `PulseOps.PortalOrigin` to the exact public HTTPS portal origin, without a path or trailing slash, and add an approved destination to `PulseOps.Webhooks`. Each destination accepts `Name` (stable unique alias), `Url`, optional `Application` and `Environment` filters, `Severities` (defaults to high and critical), and optional `BearerToken`. Keep the token in protected host configuration. Treat an omitted scope filter as every application or environment for that axis.

The worker sends a compact JSON notification for matching incident openings, reopenings and resolutions. It includes delivery and incident IDs, application, environment, severity, status, and the internal incident URL. Configure the approved receiver to acknowledge only accepted deliveries with a 2xx response and deduplicate on the `Idempotency-Key` header, since timeouts or worker restarts can result in redelivery. Retryable responses use bounded retries and server guidance where provided. Redirects are disabled. Include the destination in outbound network policy and verify its delivery-failure counter on the administrator platform health view.

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
| Batches return 400 | Schema version, batch count, aliases and fixed categories, clock skew, and strict JSON fields |
| Batches return 429 | Server `Retry-After`, relay request fan-in behind one IP, pending queue, disk reserve, and retention |
| Batches return 503 | SQLite lock contention, file access, and I/O errors |
| Portal appears empty | Sign-in role, viewer groups, selected scope and interval, receipt and processing state |
| Incident did not open | Rule scope, received-time window, sample minimum, failure threshold, and whether observations were anonymous |

## Backup, restoration, and upgrades

Create regular SQLite snapshots according to the service's recovery-point objective. The backup script uses SQLite's online backup API and verifies integrity before moving the result into its final name. Copy verified snapshots to approved encrypted backup storage. Apply retention to backup copies separately from the application database. Also secure the private settings, certificate recovery material, and Data Protection keys according to the operations team's recovery process. Keep backup storage outside the monitored host for host-loss recovery. [SQLite Online Backup API](https://www.sqlite.org/backup.html)

```powershell
./deployment/windows/PulseOps.ps1 Backup
./deployment/windows/PulseOps.ps1 VerifyBackup -SnapshotPath 'C:\ProgramData\PulseOps\backups\approved.db'
```

The script defaults to a unique timestamped file under `backups`. Pass an explicit destination only inside protected storage. Keep SQLite journal sidecars with the live database and use the backup command for portable snapshots. Use the version that understands the snapshot's schema. Regularly restore into a separate isolated installation and smoke-test RPO and RTO. Database snapshots contain operational information including incident history and should be protected accordingly.

For a planned upgrade, export your current protected config, record the current release and manifest hash, and keep the previous build in the versioned release directory. Run `Deploy` with the new extracted artifact and trusted digest. The deployment takes a verified pre-release snapshot while hosts are stopped, runs the new release's migrator, and gates startup on health. A failed upgrade leaves service registrations stopped or disabled for recovery. Inspect the Event Log and record the last snapshot path printed by the script.

Restore replaces the live data with the selected snapshot. Take both hosts and any other local process using the database offline first. Restoring an older snapshot rewinds incident activity and telemetry to the backup point. Preserve the current state for recovery before replacing it. The wrapper creates a pre-restore snapshot when the live database is available. Use an authorized clean recovery installation for an unreadable database that cannot be backed up. During a recovery, omit `-Version` only if the registered release matches the snapshot's schema.

```powershell
$install = './deployment/windows/PulseOps.ps1'
& $install Stop
& $install Restore -SnapshotPath 'C:\ProgramData\PulseOps\backups\approved.db' -Version '0.1.0'
& $install Migrate -Version '0.1.0'
& $install Start -HealthUrl 'https://pulseops.internal.example'
```

Use `Rollback -Version <old-version> -HealthUrl <origin>` to reactivate an earlier staged release with a compatible schema. Add `-SnapshotPath <pre-upgrade-snapshot>` when the prior binary requires the earlier database. Use the matching private configuration version. A snapshot-based rollback supersedes intervening records. Verify health, then verify SSO, scope access, a new dry-run event, and worker processing. Use `Remove` to remove the SCM registrations while preserving releases, backups, config, and data.

SQLite retention removes old processed telemetry in bounded batches. Open incidents remain available beyond the resolved-incident retention value. The database may reuse freed pages without shrinking its on-disk file, so monitor actual free space and WAL size as well as recent ingestion. Arrange a maintenance window for any separate compaction after a verified backup. Observe the production workload before sizing long-term retention.

## IIS alternative

Use one HTTPS IIS site at the host root. Install IIS, Management Scripts and Tools, Application Initialization, and WebSocket Protocol if enabled in your environment. Install the .NET 10 Hosting Bundle after IIS, or repair the bundle after enabling IIS. Provide a trusted certificate in LocalMachine `My`. Configure a dedicated `PulseOps` application pool and route browser users through application OIDC. Enable IIS Anonymous Authentication and use app-level OIDC at PulseOps endpoints. [Microsoft IIS hosting prerequisites](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0)

1. In elevated PowerShell 7, run Windows `Initialize` and `Stage` with the approved artifact/digest. Use the same private JSON with `PulseOps.EmbeddedWorker` set to `true` and omit the `Kestrel` section so IIS terminates TLS. Keep the same site DNS name in `AllowedHosts` and the OIDC callbacks. Remove any pre-existing PulseOps Windows Service registrations when converting an installation.
2. In elevated **Windows PowerShell 5.1**, run `deployment/iis/Configure-Iis.ps1 -Version <release> -DnsName <fqdn> -CertificateThumbprint <sha1-thumbprint>`. It configures the SNI HTTPS site, dedicated 64-bit pool, protected external config arguments, and file permissions, leaving the pool and site stopped.
3. In elevated PowerShell 7, run `./deployment/windows/PulseOps.ps1 Migrate -Version <release>`. Start the `PulseOps` pool and site from IIS Manager. Run `Verify -HealthUrl <https-origin>`. After verification, enable auto-start for the site and pool in IIS and keep application initialization/preload enabled. Use an independent health probe to detect site initialization failures or recycling gaps.
4. For maintenance and upgrades, disable site and pool auto-start, stop both, then wait for the associated worker process to exit. Verify or create a snapshot using the current staged version, stage and configure the new version, migrate, restart, and verify. To roll back, stop both, configure the previous staged version, restore the matching snapshot if required, migrate, and verify before re-enabling auto-start. Pass the staged `-Version` for database commands because an IIS installation has no PulseOps service registration.

## Production acceptance checks

- Install, start, stop, and recover the Windows services on a representative host. Document applicable machine GPOs, certificate private-key ACLs, Event Log source, and a tested exit procedure.
- Verify OIDC login, failed login, logout behavior, session expiry, claim emission, allowed-scope reads and writes, cross-scope denial, and secret rotation with the enterprise provider.
- Prove the safe relay, input rejection, fail-open client behavior, and rate limiting through the actual reverse proxy and monitored workload.
- Test a verified offline restore and a release rollback. Measure readiness recovery, queue backlog, disk pressure thresholds, and diagnostic visibility after a restart.
- Measure the target concurrency, peak throughput, dashboard latency, and storage growth on the chosen host. Archive the test method and results with the release. [Local benchmark and commands](Performance.md)
- Record security, operational, dependency approval and change-management sign-off for the intended environment.

Windows service registration, IIS configuration, and enterprise OIDC tests require a representative Windows environment and identity provider. Consult [implementation status](../IMPLEMENTATION_STATUS.md) for repository-level validation.
