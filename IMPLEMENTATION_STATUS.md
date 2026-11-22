# Implementation status

## Delivered

- .NET 10 modular server, standalone Blazor WASM portal, Razor Class Library SDK, finance demo with mock API, and optional standalone Windows worker mode. Embedded processing is the default.
- Self-contained SQLite with WAL, an explicit schema migrator, atomic durable acceptance and processing, deduplication, bounded backlog, retention, local disk protection, native backup/restore and heartbeat readiness.
- Scoped OIDC role/group authorization, antiforgery for operator actions, authenticated machine intake, controlled anonymous startup, allowlists and request/rate bounds.
- Dashboard and drill-downs for incident triage, sessions, dependencies, traces, exceptions, structured logs, metrics and releases. DataCat's upstream Boolean search was translated to allowlisted C# and SQLite.
- Configurable windowed incident detection with optional automatic recovery after consecutive fully observed windows, lifecycle history, read-only scheduled HTTPS checks and durable webhook delivery with retry.
- Windows and IIS deployment scripts, TeamCity/Artifactory guidance and operator, security and integration documentation.

## Verification evidence

- 15 NUnit tests passed, including bUnit coverage for portal loading, error/retry, empty and chart states. Server coverage includes restart, concurrent idempotence, scoped incident actions, atomic backpressure, validation, Boolean search, webhook leasing, recovery thresholds and SQLite restore. Four opt-in Playwright scenarios passed in Chromium against a published Release host.
- SDK incremental payload measured on equivalent trimmed Release demo publishes at **47,281 Brotli bytes** (46.17 KiB), generally beneath the stated size target for this app. Baseline 2,796,507 bytes and instrumented 2,843,788 bytes. In a 20-pair local Chromium cold-navigation experiment, median first report-workspace render rose from 172.3 to 202.6 ms and p95 from 199.4 to 248.6 ms. SDK CPU, memory and environment-specific startup remain unmeasured.
- The local 45-second steady run accepted 199.98 events/s. The 1,000 events/s offered burst accepted 10,000 events in about 10.39 seconds, with p95 intake latency of 454 ms. All 19,000 events were processed. The loopback run used four synthetic scopes and 500 generated session identifiers. [Full benchmark](docs/Performance.md)
- The Windows x64 artifact cross-published with the native SQLite DLL, portal assets and upstream notices. Local TLS Production-mode smoke verified migration, native backup/integrity, readiness, rejection of unsigned-in operators and cross-environment credentials, successful and duplicate intake, and rejection of unapproved metadata and anonymous startup. [SDK tests](docs/SDK.md) · [Windows acceptance](docs/Operations.md)

## Before production approval

Validate the deployed HTTPS binding, Windows Service and/or IIS, enterprise IdP claims and deprovisioning, private keys and file ACLs, application isolation, telemetry relay controls, real internal webhook delivery and restart, fresh backup/restore rehearsal, and recovery on the target Windows host. Complete workload/soak and security acceptance using representative data and synthetic applications. The local demonstration and test host do not establish production capacity or regulatory approval.

## Remaining scope

OTLP and Datadog protocols, generalized log/metric dimensions, saved dashboard layouts and long-range materialized rollups remain pending. Incident detection implements bounded failure windows and grouped recurrence; historical baselines, maintenance suppression, escalation schedules and advanced regression/latency/no-data rules remain incomplete. SMTP and additional external adapters, audited dynamic admin configuration, immutable audit export, detailed TLS/DNS probe diagnostics and service-side ASP.NET instrumentation are pending. Expanded coverage for release correlation, configurable retention by data class/legal hold, persistent SDK browser queue, additional framework support and operator workflows remains pending.
