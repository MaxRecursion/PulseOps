# Staging acceptance

Use this procedure before production approval to show that a published PulseOps build behaves correctly on the target Windows Server topology and sustains the reference workload. It combines an executable smoke check, a capacity soak and an archived evidence bundle. Run it against a dedicated staging host and staging scope with synthetic, clearly labelled telemetry only.

## Tools

| Tool | Purpose |
| --- | --- |
| [`deployment/staging/StagingSmoke.cs`](../deployment/staging/StagingSmoke.cs) | Functional and security smoke. Local mode starts a disposable Production host over HTTPS with a generated certificate that only the harness trusts. It runs `--migrate`, probes liveness and readiness, checks security headers, operator authentication, intake authentication, cross-scope rejection, allowlists, anonymous-startup origin control, release markers, request size limits and malformed JSON. It then stops the host, takes a native backup, verifies and restores it, exports and verifies the audit chain, restarts the host, and proves durable deduplication after restart. Remote mode runs the same network checks against a staging URL with normal TLS validation. |
| [`deployment/performance/Soak.cs`](../deployment/performance/Soak.cs) | Capacity soak. Local mode starts a disposable Development demo host with four generated scopes and 500 generated session IDs, then drives steady load, periodic bursts and dashboard queries, including history, monitors and audit. Remote mode sends successful ingest events only, so load testing never opens incidents or notifies real receivers on the staging scope, and it never signs in as an operator. |
| [`deployment/windows/Invoke-StagingAcceptance.ps1`](../deployment/windows/Invoke-StagingAcceptance.ps1) | Windows wrapper for the staging host or a runner in the same network. It validates inputs, records environment evidence, runs the smoke and then the soak in remote mode, and writes reports, logs, a transcript and SHA-256 checksums to a new results directory. It never installs or changes services or IIS. |

The harnesses are .NET 10 file-based applications without package dependencies. Credentials are read from environment variables named on the command line and are never printed or written to reports.

## Prerequisites

- A staging deployment produced by the normal release process ([operations guide](Operations.md)), with HTTPS, the selected service topology and enterprise OIDC.
- A staging application scope, for example `staging-smoke`/`staging`, whose `AllowedNames` include the synthetic event name (`smoke-check` by default). Provision its machine token, and optionally a token for a second scope to prove cross-scope rejection, through the approved secret store.
- The .NET 10 runtime on the runner. Extract the reviewed release archive and run the wrapper from it to use the bundled tools. To build the tools separately on a build agent, publish both harnesses and pass their shared output directory as `-ToolsDirectory`.

```sh
dotnet publish deployment/staging/StagingSmoke.cs -c Release -o artifacts/staging-tools/smoke
dotnet publish deployment/performance/Soak.cs -c Release -o artifacts/staging-tools/soak
```

## Run on the staging runner

```powershell
$env:PULSEOPS_STAGING_TOKEN = '<staging token from the secret store>'
$env:PULSEOPS_OTHER_SCOPE_TOKEN = '<token of another staging scope>'
.\deployment\windows\Invoke-StagingAcceptance.ps1 -BaseUrl https://pulseops-staging.internal.example `
    -Application staging-smoke -Environment staging -TokenVariable PULSEOPS_STAGING_TOKEN `
    -OtherTokenVariable PULSEOPS_OTHER_SCOPE_TOKEN -ManifestPath ./manifest.json `
    -ResultsDirectory C:\PulseOpsEvidence\staging-2026-11-22 -SoakMinutes 60
```

When the runner is the PulseOps host itself, add `-ServerProcessId <pid>` and `-DatabasePath <path to pulseops.db>` so the soak samples CPU, working set and SQLite/WAL size. Without them those gates report `not-measured`. Archive the whole results directory with the release record, the artifact manifest digest and the change ticket.

## Run locally

Local mode exercises a published build on the developer machine. It is useful before staging and in CI.

```sh
dotnet publish src/PulseOps.Server -c Release -o artifacts/smoke-server
dotnet run --file deployment/staging/StagingSmoke.cs -- --local artifacts/smoke-server/PulseOps.Server.dll --report artifacts/reports/smoke.json
dotnet run --file deployment/performance/Soak.cs -- --local artifacts/smoke-server/PulseOps.Server.dll --minutes 30 --report artifacts/reports/soak.json
```

## Gates

The smoke exits non-zero when any non-skipped check fails. The soak exits non-zero when any gate fails:

| Gate | Threshold |
| --- | --- |
| Steady acceptance | At least 99% of steady-rate events accepted. Bursts may receive 429 with `Retry-After`. |
| Server errors | No 5xx responses from intake or queries |
| Transport | No failed connections or timeouts |
| Backlog | Zero pending events within 120 seconds of the load ending (local mode) |
| Memory | Working-set growth below 25% across the second half, with the slope reported in MiB per hour |
| WAL | Peak SQLite WAL below 256 MiB |

## What a local run does not prove

A local macOS or Linux run uses loopback networking and a generated certificate, and runs the load generator on the same machine. It does not cover the Windows Service or IIS topology, enterprise TLS, real OIDC sign-in, the production disk subsystem, antivirus scanning of the data directory, or network latency between clients and the host. Only a staging run on the target topology, with the evidence bundle archived, supports a production capacity claim. Record results in [IMPLEMENTATION_STATUS.md](../IMPLEMENTATION_STATUS.md).
