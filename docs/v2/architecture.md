# Architecture decisions and acceptance evidence

## ADR 001: additive modular monolith

Retain the legacy skill, scripts and package. Build one application with
separate domain and infrastructure assemblies where real behavior warrants
them. Core cannot reference SQLite, Codex, PowerShell, ASP.NET or Blazor.
CLI and web host share application services. A module name in the master
specification does not justify an empty project.

## ADR 002: durable facts and conservative recovery

Use Microsoft.Data.Sqlite rather than an ORM: explicit transactions and
constraints are central to scheduler correctness; the official provider
avoids another abstraction and dependency. Every accepted lifecycle change
and its event belong to one transaction. A unique active assignment and
serialized reservation transaction prevent duplicate dispatch. Unknown
usage blocks further automatic admission. Recovery records uncertainty;
it never turns an absent PID or missing log into success.

Backups use the provider's online backup API, not file copies of an active
WAL database. Restore validates a separate destination before operator
replacement; never overwrite an active database automatically.

Source: [Microsoft backup documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)
and [transaction documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).

## ADR 003: process supervision is not a sandbox

A Windows Job Object owns the complete process lifetime and kills children
when the supervisor closes. A process must not execute untrusted code in
the gap before job assignment: create suspended, assign, then resume, or
use creation-time job association. Assignment failure must fail closed.
Neither a Job Object nor a Git worktree restricts filesystem/network access.
Runtime sandbox enforcement must be independently capability-checked.
Non-Windows behavior must be reported accurately until its process-group
and sandbox guarantees have their own acceptance evidence.

Source: [Microsoft Job Objects documentation](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).

## ADR 004: trusted policy outside repository instructions

Task text and repository files are untrusted inputs. They cannot override
platform policy. Worker commands use argument arrays, never shell command
evaluation. Runtime credentials and configuration must be separated from
repository-owned files. Network, dependency installation, publishing,
deployment and destructive operations are denied unless explicitly enabled
by trusted policy. Policy denial is a durable, non-retryable security event.
Scope validation checks both old and new paths for renames and excludes
platform policy/configuration files even when a broad path is declared.

Verification commands also execute repository code. Running them outside a
sandbox would circumvent the worker policy. Until enforcement is available,
fail closed or require a separately trusted execution environment; do not
present prompt instructions or a command allowlist as sandbox isolation.

## ADR 005: candidate-bound quality evidence

Review and verification evidence binds the immutable candidate commit,
base commit and task-contract digest. Integration uses a separate worktree
and validates the resulting commit before updating the intended branch.
Never merge when the target moved since verification without verifying
the new combined result. A worker report is evidence to inspect, not an
authorization token. Preserve branches on conflict and failed checks.

## Acceptance matrix

| Invariant | Required executable evidence |
| --- | --- |
| Max six workers | Concurrent reservation requests exceed six |
| No duplicate task | Competing connections reserve the same task |
| Durable events | Restart after commit and interrupted rollback |
| Dependency gate | Child cannot start before parent integrated |
| Unknown tokens | Corrupt/missing/failed-turn logs stop admission |
| Process closure | Parent exits while child remains; child terminated |
| Safe cancellation | Cancel/timeout preserves logs and worktree |
| Scope gate | Traversal, rename, excluded and case-boundary tests |
| Quality gate | Successful exit with failing independent tests rejected |
| Integration gate | Candidate/base/target mismatch and conflict rejected |
| Recovery | Repeat recovery produces no duplicate effects |
| API boundary | Missing credential, wrong origin and mutation rejected |
| Legacy safety | Import twice, source bytes unchanged, unknown retained |
| Universal projects | Mixed manifests; no fixed language requirement |

Each phase report must distinguish simulated tests from real runtime,
browser, packaging and production validation. Benchmark targets are goals
until raw measurements and environment are recorded.
