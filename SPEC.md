# PulseOps specification

PulseOps helps internal support teams detect failures in Blazor WebAssembly apps and follow evidence from an operational dashboard to dependency, incident, trace and session records. The product provides metrics, logs, tracing, dashboards and monitor workflows. Original Blazor instrumentation and support workflows center on pre-runtime startup, browser-safe telemetry and enterprise client observations.

## P0

- Register and isolate application and environment scopes. Require scoped machine credentials for ingestion and enterprise OIDC for operational access. Enable origin-restricted startup intake explicitly per scope.
- Batch lightweight SDK events and discard under overload without blocking business paths. Assign random session and event IDs, capture declared labels and types, preserve trace relationships and omit payloads.
- Use SQLite receipts as the durable queue. Return acceptance after commit and process records in bounded atomic transactions, preserving retries and incident history.
- Surface operational summaries, dependency health, incident management and correlated session diagnostics in a standalone Blazor WASM portal.
- Provide local synthetic data, automated tests, Windows setup, backup and recovery, and internal CI/package guidance.

## P1 and P2

P1 covers multi-signal window policies, richer release comparisons, scheduled read-only probes, out-of-process notifications, richer metrics and browser signals, and measured operational capacity. P2 covers general-purpose telemetry interoperability, saved dashboards and advanced analytics. Implementation and evidence are tracked in IMPLEMENTATION_STATUS.md.

## Security and reliability invariants

One Windows server and local disk define the deployment boundary. Scope all access on the server. TLS and private operator configuration are required in Production. Telemetry is untrusted evidence. Records use UTC. Traces may be incomplete. Incident explanations cite correlated observations and avoid attributing SAP internal behavior. Browser delivery is best effort.
