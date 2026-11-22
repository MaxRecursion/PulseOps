# Telemetry and storage

## Batch contract

POST JSON to `/api/telemetry/v1/batch` from an approved relay or machine producer. The server expects the matching application/environment bearer credential. The envelope is `{ schemaVersion, application, environment, events }` with schema version `1`. Supply up to 100 events. Requests can be accepted with duplicate IDs, which are idempotent within the application and environment while the corresponding row is retained.

| Event field | Meaning |
| --- | --- |
| `id` | Nonzero random UUID. Reuse this ID for a transport retry. |
| `occurredAt` | UTC date-time with offset. The server accepts up to one day old and five minutes in the future. |
| `version` | Application release identifier, up to 32 characters. |
| `sessionId` | Nonzero random UUID for one diagnostics session. |
| `kind` | API, error, operation, startup, navigation, metric, session, synthetic, or log. |
| `name` | Server-registered alias. Startup events use the fixed milestone and diagnostic name set. |
| `outcome` | Success, failure, degraded, unknown, or cancelled. Use lowercase values in the JSON. |
| `durationMs`, `statusCode`, `value` | Optional finite measurement, observed HTTP status, or metric value. |
| `traceId`, `spanId`, `parentSpanId` | Optional lowercase hexadecimal W3C IDs, 32 hex characters for trace and 16 for each span. |
| `category` | Optional fixed or explicitly registered diagnostic alias. |

The server rejects unknown fields and validates aliases, measurements, identifiers and time bounds. Accepted batches receive HTTP 202 after commit with accepted and duplicate counts. Invalid fields produce 400. Full inbox/disk reserve or rate limits can produce 429 and a transient storage failure can produce 503. Retry with the same event IDs and observe the server's retry interval. Browser delivery is best-effort.

`/api/telemetry/v1/startup` is an optional restricted anonymous route for a configured origin, with up to 12 events. It accepts fixed startup names and categories, success/failure, elapsed time, and basic correlation fields. Trace, HTTP status, and numeric value fields are excluded from this route. Origin policy and rate limits mitigate load and browser abuse. Treat these records as untrusted input.

The release route is `POST /api/telemetry/v1/releases`. It accepts application, environment, version, build ID and lowercase 40- or 64-character Git commit ID and uses the same scoped bearer model. The server sets the accepted timestamp. See [TeamCity marker usage](../deployment/teamcity/README.md).

## SQLite schema

The published server and worker use a local SQLite WAL database. Dates are stored as UTC Unix milliseconds. The migration command manages the schema version using `PRAGMA user_version`. The following are logical descriptions of the live schema.

| Table | Purpose |
| --- | --- |
| `telemetry` | Durable receipts and queryable observations in one table. A missing processing timestamp marks a queued record. Scope/event ID is unique. Indexed query paths include time, session, trace, event type, policy windows and retention. |
| `worker_state` | Processor heartbeat and cumulative processed count. |
| `incidents` | One incident identity per application, environment, kind and alias. Records state, time bounds, severity and investigation evidence. |
| `incident_history` | Logged incident transitions, actor, note and assignee. History belongs to the incident and is deleted with it according to retention. |
| `releases` | Accepted release versions and commit IDs keyed by application, environment and build identifier. |
| `alert_deliveries` | Durable incident notification records with destination name, transition, retry state and delivery lease. |

Registration, scope claims, rule definitions, webhook destinations and scheduled checks are operator-managed configuration. Session and trace detail are read from correlated telemetry rows. A worker commit includes the effects of its batch and processing markers. Webhook delivery has its own leasing and acknowledgement lifecycle because an HTTP receiver and local SQLite cannot share a transaction. See [Windows operations and recovery](Operations.md).
