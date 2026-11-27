# V2 acceptance report

## Implemented

- The native CLI and loopback Blazor dashboard share the same host.
  Registration, repository discovery, plan import/generation, validation,
  worker control, execution history, review and integration are available
  through that host.
- SQLite WAL transactions reserve capacity and record lifecycle events.
  Legacy migration preserves evidence bytes and quarantines uncertain
  executions. Backups and fresh-destination restoration are available
  in the CLI.
- Independent workers use separate Git worktrees. The real Windows
  runtime uses native job supervision and a restricted Codex profile.
  Known usage, ownership, independent verification and an inspected Git
  merge are required before an attempt can reach Integrated.
- The dashboard requires loopback authentication and supports live events,
  pause/resume, cancellation, retry authorization, concurrency changes
  and report export.

## Tested

| Boundary | Evidence |
| --- | --- |
| End-to-end local execution | Simulated implementation wrote an actual file. Windows MXC ran its verification command, the coordinator performed review and regression on fresh checkouts, then advanced the plan branch. The original checkout retained its original file. |
| Native CLI | Real process tests covered help/errors, init, registration, planning, inspection, recovery, state migration, export, backup and restore. |
| Desktop and mobile UI | Edge at 1365×900 and 390×844 exercised login, external pause reflected through SSE, resume, concurrency, task details and the dependency graph. No browser console errors were observed. |
| HTTP boundary | Real Kestrel tests covered authentication, Host/Origin validation, CSRF, event replay and report export. |
| Portability | Hosted Windows and Linux built the solution. Windows ran the full suite and Linux ran the portable suites and native-capability boundary checks. [CI run](https://github.com/developer-franciscocardoso/parallel-development-orchestrator/actions/runs/45976678800) |
| Performance | The three-iteration CLI command measured warm API, native startup, state changes, scheduling, worker overlap and integration. The quality-and-merge observation had one sample. [Raw results](benchmark-results.json) |
| Legacy compatibility | The original scripts, archived skill and execution fixtures match the audited baseline. V2 uses separate state. [Migration audit](audit.md) |

## Packaging and upgrade

The self-contained Windows x64 distribution includes the CLI, dashboard,
SQLite runtime and worker report schema. The bootstrap checks an externally
supplied archive digest and the package manifest before installing.
A stable launcher selects the active version. Two real release builds
passed fresh installation, upgrade and rollback, retaining the registered
Git baseline. The rolled-back executable served the authenticated dashboard.
[Installation and release smoke evidence](installation.md)

## Operating limits

A configured dedicated profile is required for authenticated model calls.
Discovery, native sandbox restrictions and real command supervision were
tested. Paid Codex execution and provider billing remain unverified. The
scheduler applies local request and token reservations. Individual requests
inside a Codex turn remain opaque.

Objective-only planning conservatively creates one task. Explicit task
contracts express parallel scopes and dependency edges. The default
simulator exercises lifecycle handling; it cannot implement arbitrary
objectives. Native job supervision and MXC execution are Windows-specific.

Mechanical review covers bounded changes. Specialist decisions are
candidate-bound and require independently trusted reviewer keys. The
operator is responsible for authenticating reviewers and protecting
those keys. Hardware, repository size, available tooling and the tasks
being executed affect observed timings.


## Downloadable preview provenance

The self-contained Windows x64 preview was built from commit
`1a55688dca7c83e8ca33b3a42892d2ac52c1b832`. The archive was installed
into a new directory. The installed launcher passed `doctor --json` and
reported the expected version and package digest through `update --status`.

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| pda-2.0.0-preview.1-win-x64.zip | 46,426,803 bytes | `6792a4d1e714b8600a47eefc882b8933fec2143716bee7d96462e6f08c81ef26` |
| Install-Pda.ps1 | 8,080 bytes | `2bb1c22228710d2e4283a6ca83c1cf957fa843e598b918b628eca0a60a37ff57` |

The local release suite and the hosted jobs use different capabilities.
Windows MXC tests require a compatible executable. Hosted runners
without it report those cases as skipped. The Windows development host
ran the actual native sandbox tests. The [code-level CI run](https://github.com/developer-franciscocardoso/parallel-development-orchestrator/actions/runs/45978418489)
is attached to the exact source revision used for the archive.
