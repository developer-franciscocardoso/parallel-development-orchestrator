# Running the local benchmark

Use the native executable from a Release build or installation. Choose a
fresh output file and a state home outside any Git checkout.

```powershell
pda --home C:\PdaState --json benchmark --iterations 3 --output C:\Results\pda-benchmark.json
```

The default is three iterations. Accepted values range from one to twenty.
Add `--frontend-only` to repeat the CLI, API and event measurements alone.
The optional output parent must exist. The run retains a separate synthetic
state home, bounded dashboard process logs and frontend samples under
`<home>/benchmarks/<run-id>`. The output includes the run directory, OS,
.NET runtime, architecture, logical processor count and stopwatch frequency.

Each frontend iteration measures the following on the same workstation.

| Measurement | Boundaries |
| --- | --- |
| Native help startup | Start the installed apphost with JSON help, drain output and wait for exit. Each sample creates a process. OS file caches remain warm between samples. |
| Native doctor invocation | Start the apphost against the isolated benchmark home and wait for the integrity and platform diagnostics to finish. |
| Authenticated API latency | Send a bearer-authenticated GET to the loopback dashboard and read the complete projects response over a warmed HTTP connection. One synthetic project is added per iteration. |
| Journal delivery latency | Open the authenticated event stream, wait for its handshake, register a new synthetic project, then read its exact committed sequence and matching event payload. Timing starts immediately before the database write. |

The event measurement covers delivery to an HTTP client. Browser rendering
time requires separate browser instrumentation. Dashboard startup and the
initial readiness request occur outside the API samples. Each benchmark
uses a fresh random local bearer secret and terminates its supervised
dashboard process tree on exit.

Milliseconds are wall-clock measurements. Percentiles use nearest rank,
so a small sample count often makes p95 equal to the maximum observation.
Each result preserves the raw samples. Frontend targets use strict
less-than comparisons. Missing engine measurements retain their reason.
