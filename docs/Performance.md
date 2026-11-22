# Performance verification

This is a local server performance measurement from 21 November 2026. It used a Release publish, ASP.NET Core 10.0.15, local SQLite, one Kestrel process with the embedded worker, and loopback HTTP in Development. The development machine was an Apple M4 Mac mini with 10 logical cores and 16 GB RAM. The load generator ran on the same machine.

## Workload and results

The benchmark registered four generated application scopes and simulated 500 diagnostic session IDs. It sent 25 API events per batch, with a mixture of successes and failures that exercised incident-rule evaluation. It ran a dashboard query approximately twice per second while ingestion and processing were active.

| Phase | Offered | Accepted | Effective accepted rate | HTTP p50 | HTTP p95 | HTTP p99 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 45 seconds at 200 events/second | 9,000 | 9,000 | 199.98/s | 3.99 ms | 9.27 ms | 160.53 ms |
| 10 seconds at 1,000 events/second | 10,000 | 10,000 | 962.29/s | 2.88 ms | 454.16 ms | 755.59 ms |
| Concurrent dashboard query | 108 | 108 |  | 10.96 ms | 26.27 ms | 29.20 ms |

The worker processed all 19,000 additional events and returned to zero pending work. The peak sampled queue was 1,044 events. Over the 55.62-second measured interval, the server consumed 8.98 CPU seconds, equivalent to an average of 16.1% of one core. The peak sampled working set was 212.47 MiB. Observed SQLite storage, including live WAL and shared-memory sidecars, increased by 11.79 MiB. Burst requests took about 10.39 seconds to finish and the high-tail latencies show write contention to investigate on the target host.

The 500 sessions were generated identities, with no browser processes, TLS/OIDC, active synthetic probes, or outbound webhook receiver in this run. The measured storage footprint depends on the fixture distribution, indexes and checkpoint timing. A representative Windows Server acceptance run should include production TLS, the selected service topology, concurrent probes, approved notifications, and dashboard queries over a database populated to intended retention. The target four-core, 8 GB Windows deployment remains unverified by this measurement.

## Reproduce

Publish and run the benchmark harness from the repository root. It starts a disposable server on a dynamic loopback port with a temporary SQLite database. The optional arguments represent steady duration, burst duration, steady events per second, and burst events per second. Defaults reproduce the workload above.

```sh
dotnet publish src/PulseOps.Server -c Release -o artifacts/perf-server
dotnet run --file deployment/performance/Benchmark.cs -- artifacts/perf-server/PulseOps.Server.dll 45 10 200 1000
```

The harness prints JSON with event acceptance, request latency percentiles, dashboard response codes, queue depth, processing count, sampled working set, CPU and storage size. Archive the full report with the SDK version, OS/runtime and artifact digest for the acceptance run.
