# V2 implementation DAG

Binding specification: specification.md. Architecture: modular monolith,
.NET 10, SQLite, xUnit, Minimal API, Blazor and native CLI. No mandatory
external infrastructure. Keep legacy files and execution data intact.

## Contracts and integration boundaries

Core contains records, validation, lifecycle and application abstractions;
it references no infrastructure. Persistence implements durable state and
journal. Execution owns child processes; Repositories owns Git operations.
Agents translates runtime events; Providers supplies independent metadata.
Planning discovers projects and validates contracts. Scheduling reserves
resources transactionally. Quality verifies immutable candidates;
Integration consumes evidence. Recovery reconciles durable attempts.
CLI and Dashboard call application services through one composition root.
Logical modules become projects only when they contain working behavior.

Task contracts include ID, objective, dependencies, owned/read-only paths,
criteria, executable/argument verification commands, deliverables, risk,
maximum attempts, timeout, priority, runtime and token reservation.
Paths are literal repository-relative boundaries; no shell evaluation.
No task is integrated on a worker's assertion alone.

## Ordered DAG and acceptance gates

| ID | Depends on | Owner / writable boundaries | Deliverable / verification |
| --- | --- | --- | --- |
| P00 | none | coordinator, docs/v2 | Audit and migration / source inspection |
| P01 | P00 | foundation, src/Pda.Core, src/Pda.Persistence, tests | Contracts, SQLite / xUnit concurrency and restart tests |
| P02 | P01 | runtime, src/Pda.Execution, src/Pda.Repositories, src/Pda.Recovery, tests | Supervision, Git, recovery / process and repository tests |
| P03 | P02 | adapters, src/Pda.Agents, src/Pda.Providers, tests | Codex and simulator / event and cancellation tests |
| P04 | P03 | planning, src/Pda.Planning, tests | Discovery and DAG / manifest and cycle tests |
| P05 | P04 | scheduling, src/Pda.Scheduling, tests | Atomic admission / duplicate, dependency, budget tests |
| P06 | P05 | quality, src/Pda.Quality, src/Pda.Integration, tests | Verified integration / rejected and accepted candidates |
| P07 | P06 | interfaces, src/Pda.Cli, src/Pda.Api, src/Pda.Dashboard, tests | CLI, protected API, UI / integration and smoke tests |
| P08 | P07 | coordinator, benchmarks, tests | Measured performance and fault tests / benchmark command |
| P09 | P08 | packaging, packaging, .github, docs | Distribution, migration, report / publish and install checks |

All tasks: read-only dependencies are prior phase sources and the legacy
tree. Maximum attempts 2; execution timeout 3600 seconds per development
assignment. P01/P02/P05/P06 risk high; others medium. Tests belong in the
corresponding module's test project. No concurrent ownership overlap.
Coordinator owns shared solution/build files and integration commits.
Each task starts with failing behavioral tests, implements, runs its suite,
and records results below before any dependent phase starts.

## Preflight

P01 produces state contracts used by P02-P09. Freeze these before dispatch.
P02 process result carries exit, timeout, cancellation and identity.
P03 usage is nullable: P05 must never convert unknown to zero.
P06 verification binds candidate SHA, base SHA and task contract digest.
P07 exposes services without moving policy decisions into UI.
These interfaces are sequential prerequisites, not parallel guesses.

## Execution record

- P00 complete: full text audit, ZIP comparison, zero parser errors.
- Ruling: user explicitly authorized autonomous implementation and parallel
  isolated work, so no redundant design-approval pause is required.
- P01 foundation integrated at 321ae2d after independent review and two
  fix rounds. Integrated Release suite: 23 passed, zero failed/skipped.
  Delivered contracts, SQLite transactions/journal, reservations, explicit
  reconciliation/retry, backup/restore and legacy evidence quarantine.
  Generic journaled artifacts hold later module records; their business
  schemas and validation remain the owning later phase's responsibility.
- P01 minor deferred: idempotent reservation replay currently rechecks the
  repository path before returning the prior result. A missing repository
  can make replay throw, but does not admit duplicate work.
- P01 ruling: global matching-path exclusion is deliberately conservative
  across unrelated projects until canonical Git identities exist. Reject
  nested and reparse roots now to prevent scope aliases.
- P02A process and P02B Git implemented in separate worktrees.
  P02B uses a private constrained Git process wrapper and has no dependency
  on P02A's worker supervisor, enabling independent work without shared
  mutable source. Recovery now uses their verified contracts.
- P02 accepted at ff3a19c: 59 Release tests passed across Core (7),
  Persistence (16), Execution (13), Repositories (12), Recovery (11).
  The final repository replacement fix also passed the Recovery suite.
  Real Windows job crash/timeout and Git worktree tests ran. Native MXC
  timeout canary ran without model calls. Non-Windows supervision remains
  explicitly unsupported, with the abstraction ready for a future adapter.
- P03 runtime/security and provider registry dispatched independently.
  P04-P09 pending. No V2 end-to-end capability claimed complete.
- Runtime environment finding: installed bundled Codex's Windows sandbox
  provisioning path failed with "app runtime provisioning service is
  unavailable; refusing helper fallback". Live secure execution requires
  a functioning provisioning service/runtime. Do not bypass isolation.
  Domain/persistence and deterministic simulation remain independent work.
- Follow-up resolved basic sandbox startup: explicit windows.sandbox=mxc
  with :workspace profile ran a no-model canary successfully. Continue
  runtime implementation; strict boundary validation remains required.
- P03 accepted at 7322854. All 110 integrated Release tests passed.
  Native MXC canaries exercised filesystem and network boundaries and
  supervisor termination. The isolated Codex profile needs authentication
  before a paid end-to-end run can be verified.
- P04 accepted at 6e3fc82. All 122 integrated Release tests passed on Windows.
- P05 accepted at cf59794. All 147 integrated Release tests passed.
  The composed application library built with zero warnings. Native Windows
  counters drive admission. Provider rate limits measure admitted workers
  and token reservations. Intra-turn provider requests are opaque.
- P06 full-path acceptance passed at bce1acc. The simulator edited a tracked
  file in an isolated worktree. Native MXC checked the file, mechanical
  review examined the commit and the coordinator advanced the plan branch.
  The user checkout retained its original contents. Focused suites passed
  20 Git, 11 quality and 8 verification tests. No paid model was invoked.
- P07 accepted. Real CLI invocations registered and inspected Git, generated
  and validated a plan, exported diagnostics, imported legacy evidence and
  backed up/restored state. Edge desktop and mobile tests covered login,
  SSE updates, pause/resume, concurrency controls, task details and DAG.
  Anonymous API returned 401 and authenticated plan export returned 200.
- P08 measured dashboard history payloads and latency. Summary returns
  100 activity entries and retains full exports. The regression test
  confirms matching metrics and chronological history.
- P08 accepted. Full native benchmark command ran with three iterations.
  Measured p95: CLI help 252.92 ms, authenticated API 8.53 ms, journal
  write to SSE 433.26 ms, database transition 21.87 ms and scheduling
  61.28 ms. Two simulated workers overlapped. The fixture detected zero
  lost events, duplicate assignments or unverified integrations. One
  native quality-and-merge operation took 43.06 seconds. Raw results
  are in benchmark-results.json. Windows and Ubuntu CI both passed on
  cbfd40a. Linux coverage excludes Windows job and MXC execution.
