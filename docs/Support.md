# Support portal runbook

## Triage a production incident

1. On **Overview**, select the application, environment, release version where known, and the shortest interval that includes the problem. Compare request volume, failure rate, and p95 latency. Use longer intervals to establish a baseline. Latency buckets without measured samples are gaps in the series.
2. Open **Incidents** and compare severity, lifecycle state, first and last occurrence, and the evidence recorded by the rule. Review the sample count and evaluation window before interpreting a percentage. Acknowledge, assign, or mark the incident investigating according to your team's procedure.
3. Review **Dependencies** for the affected operation and whether failures concentrate around HTTP status, timeouts or high latency. A dependency span represents the client-observed duration. An SAP gateway response may include time spent at the gateway, in the network and inside SAP.
4. Use **Session explorer** to open an affected random session ID and filter the timeline around the event. Look at startup, navigation, operations, API calls and exceptions. Use a trace ID to compare the observations from the same allowed trace where instrumentation supplied it.
5. Check releases in the affected scope. A change beginning near a deployment is evidence to investigate. Inspect version, affected operations and backend measurements before attributing a cause.
6. Use supported incident actions to record a short technical note, mitigation, recovery checks and resolution. Include the reference to your approved backend investigation record if appropriate. Keep customer data and credentials outside notes.
7. For a recurring problem after resolution, verify the new signal and inspect the recorded reopen. Use your organization's alerting system's receiver records for webhook delivery verification.

## Interpreting SAP symptoms

| Observed evidence | Investigation |
| --- | --- |
| HTTP 503 across sessions for one dependency alias | Check the SAP gateway and service health during the event window. Compare approved upstream correlation records where available. |
| Timeout or network category with no HTTP status | Check client connectivity, proxies, CORS, DNS, gateway timeout limits and backend latency. A browser transport exception may have several causes. |
| HTTP success followed by a failed client operation | Inspect app-level parsing, schema changes and transformation errors. For long CSVs, compare the instrumented fetch and parse operations and their trace hierarchy. |
| Slow endpoint with healthy server response | Compare dependency time with overall user operation time. Include download and client-side rendering/processing where measured. Parallel spans may overlap. |
| Startup failures clustered by release | Confirm the current HTML and runtime assets have matching versions. Check CDN/proxy cache behavior, browser resource failures and approved service worker diagnostics. |

## Scope and time ranges

Dashboard totals count accepted, processed observations within the selected interval. Browser sampling, network interruption, collection opt-outs and anonymous startup restrictions affect coverage. Compare equivalent sampling and workload periods where possible. Restrict links to authorized recipients and include application, environment and time range when sharing incident/session references internally.
