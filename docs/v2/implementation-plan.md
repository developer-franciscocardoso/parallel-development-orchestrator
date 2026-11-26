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
- P01 pending. All later phases pending. No V2 capability claimed complete.
