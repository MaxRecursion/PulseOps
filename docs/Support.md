# Support portal runbook

## Create a custom report for a Blazor app

Use the built-in views to create a filtered report for one registered application and environment. Save the view as a browser bookmark or share its URL. The portal has fixed charts and tables. It currently has no saved report builder, custom formulas, or telemetry CSV/PDF export.

1. Sign in, open **Applications** from the sidebar, and find the row with your app and the intended **Environment**. Select the application name to open its **Overview**. If the app is missing, clear the **Application** and **Environment** filters. Ask the PulseOps administrator to confirm registration and your access if the app is still absent.
2. Confirm **Application** and **Environment** at the top of **Overview**. Set **Time range** to the period you need, such as **Last 24 hours**. To focus on one release, enter its exact SDK-reported identifier in **Version** and press Enter. Leave **Version** blank for all versions.
3. Review **API requests**, **Success rate**, and **P95 latency**. Compare **Request volume** and **Response latency** over time. Use the links at the bottom for observed sessions, exception events, and startup failures. **Open incidents** counts current unresolved incidents for the application and environment across releases and time ranges.
4. Choose a drilldown below, then bookmark the resulting URL or share it with operators who have access to that application and environment.

Your report uses telemetry already collected from the app. A business operation or API endpoint appears under the stable alias registered by the app team. To add an observation that the app does not yet emit, follow the [Blazor SDK instrumentation guide](SDK.md#add-a-business-operation).

### Report on an instrumented operation

1. With your application and environment selected, open **Logs** in the sidebar. The page title is **Event explorer**.
2. Set **Event type** to **Business operation**, enter the exact **Event alias**, and select **Search**. For an app instrumented with the SDK guide's example, the alias is `generate-report`.
3. To find failures or slow executions, enter `outcome:failure OR duration:>=1000` in **Search expression** and select **Search** again. Here, `duration` is in milliseconds. The expression combines with the selected event type and exact alias. The field supports `AND`, `OR`, `NOT`, and parentheses. **Clear** removes the search and event filters and keeps the application scope and time range.
4. Review **Outcome** and **Value**, which shows elapsed duration for operation events. Select **Session** to see nearby events or **Trace** when available to inspect correlated operations and API requests.

### Report on API requests or browser metrics

- For endpoint traffic, select **API explorer** in the sidebar. Compare **Requests**, **Failures**, **Error rate**, and **P50**, **P95**, and **P99** in **Endpoint performance**. Select an **Endpoint alias** to review individual observations and their HTTP status and duration when available. **All endpoints** returns to the summary. To search for 5xx responses for one alias, open **Logs**, set **Event type** to **API request**, enter the exact **Event alias**, and search for `status:>=500`.
- For an API chart, open **Metrics**, choose **API request count**, **API error rate**, or **P95 API latency** from **Metric**, and select **Apply**. These charts summarize API calls for the selected application, environment, time range, and version. Use **History** for an alias-specific trend.
- For measured browser values, open **Metrics**, choose **Browser metric samples**, and enter an optional exact **Metric alias**, such as `lcp`. Select **Apply** and read the samples in **Browser metric events**. Browser support, SDK capture settings, and available measurements determine which aliases appear. For the supported metric names, see [what the SDK records](SDK.md#what-the-sdk-records).

### Report on longer-term trends

1. Open **History** in the sidebar and confirm **Application** and **Environment**.
2. Choose a **Range** within the deployment's rollup retention, such as **Last 30 days**. Select **Business operation** or **API request** from **Event type**. Enter an exact **Event alias** and an optional exact **Version**, then select **Apply**. Leave **Event alias** blank to see the busiest aliases in **Top series**, and select one to focus the charts.
3. Review **Events**, **Failure rate**, **Event volume**, and **Latency percentiles**. The page uses hourly or daily rollups based on the selected range and the deployment's retention settings. History percentiles are approximate and empty duration buckets have no percentile. For browser metric samples, use **Metrics**.

### Save or share a filtered view

After applying the filters or running a search, copy the address bar URL. For example, a last-24-hours operation view can end in `/logs?application=finance-dashboard&environment=production&hours=24&kind=operation&name=generate-report`. The URL preserves the applied filters for that page. If a recipient is prompted to sign in and lands on **Overview**, they can reopen the URL after signing in.

Time ranges are relative to when the page loads. For a review record that needs a fixed reference, record the observed results and the UTC period separately in your team's approved location, alongside the live view link.

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
