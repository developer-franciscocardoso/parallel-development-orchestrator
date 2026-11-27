# Dashboard activity measurements

The dashboard previously fetched the full plan journal and computed
metrics with a second full read. A synthetic plan with 5,000 additional
512-character journal payloads exposed the cost.

| Authenticated request | Rows returned | Bytes | Median | p95 |
| --- | ---: | ---: | ---: | ---: |
| Original full plan report | 5,001 | 3,380,425 | 389.93 ms | 664.10 ms |
| Dashboard summary | 100 | 68,441 | 60.81 ms | 78.04 ms |

Measurements used 15 warm loopback HTTP requests on this Windows
development host. Other development activity was running, and this sample
does not establish production performance. Nearest-rank p95 is the largest
sample for 15 requests. The two responses have different history lengths
by design. The browser displays at most 100 activity rows; exports and the
full plan endpoint still include all rows. The summary metric query reads
lifecycle data without copying unrelated journal payloads.

Run `python tools/benchmarks/report_latency.py PATH_TO_PDA` for a full
report or append `--summary` for the bounded endpoint. The script creates
a separate SQLite database and Git repository and never opens user state.
Raw samples are in [the baseline](benchmarks/report-full-baseline.json)
and [the summary measurements](benchmarks/report-summary.json).
