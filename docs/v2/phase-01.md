# Phase 01 foundation

The root solution targets .NET 10. Core has no package dependencies.
Persistence uses Microsoft.Data.Sqlite 10.0.12. Tests use xUnit.
Run `dotnet test Pda.slnx` from the repository root.

## Contracts and storage

A task declares literal repository-relative path scopes, dependencies,
acceptance criteria, argument-vector verification commands and resource
limits. Validation rejects missing dependencies, cycles, invalid paths
and unordered writer/writer or writer/reader conflicts. Ordered tasks
can share paths. Comparisons are conservative and case-insensitive.

Applications depend on Core's IStateStore. The SQLite adapter uses WAL,
foreign keys, an application identifier, a transactional initial schema and
immediate writer transactions. Open one StateStore per service or per call.
Store the database outside worker-writable directories. Register projects,
create plans and read task contracts through the same adapter.

Reservations enforce a global six-slot ceiling, global ownership exclusions across projects, plan budgets, dependency integration and attempt limits.
Reservation keys are replayable with matching plan, task and concurrency.
Transition with the observed version. Evidence and the updated record commit
together. Unknown final usage blocks new reservations in that plan. An
interrupted process occupies a slot until reconciled. Failed or cancelled
attempts require confirmed process termination, measured usage and explicit
retry authorization. Security-blocked attempts cannot be authorized to retry.

Persist additional typed records using PutArtifact and Artifacts. Their
versions and complete data snapshots are journaled. Use History(null) for
standalone project registration events. Event payloads are local sensitive
data. Application modules must redact credentials before persistence.
Adapter/service-specific schemas, process supervision, enforcement and
independent candidate verification belong to subsequent phases.

## Backup and migration

Backup uses SQLite online backup into a temporary sibling, checks database
identity, version, integrity and foreign keys, then moves the verified copy
to a previously absent destination. Restore performs the same checks and
never replaces the running database. Keep the previous database for rollback.

ImportLegacy accepts the legacy .execution directory containing both
schema-2 execution-state.json and task-registry.json. A single transaction
archives their original bytes, source locations, hashes and summaries.
Repeat imports deduplicate by source and SHA-256. Imported records remain
archival. They create no runnable assignments, spendable balances or plans.
Active and unknown attempts are counted for operator reconciliation.
Legacy logs, worktrees and prompts remain at their original locations.

## Verification

The initial behavioral tests failed to compile because TaskContract and
StateStore did not exist. The completed suite exercised competing SQLite
connections, the six-slot cap, duplicate request keys, stale versions,
budget exhaustion, unknown usage, explicit retry authorization, dependency
integration, restart, journal rollback, immutable journal triggers, online
backup/restore and idempotent byte-preserving legacy import on Windows.
No paid runtime, crash injection at OS power loss, or Linux run was used.

## Reviewed admission and recovery boundaries

All projects share a conservative path-conflict check. This may serialize
unrelated repositories with matching paths and prevents alias paths or linked
worktrees from bypassing an existing assignment. The validator caps plans at
256 tasks, 32 dependencies per task, 2,048 path declarations and 1 MiB of
text. Reachability uses iterative topological traversal and bounded bitsets.

An imported active or unknown legacy attempt blocks new admissions and the
transition to Running globally, including after restart. ReconcileLegacy takes
the source path and digest, confirmation that the processes stopped, measured
tokens for each unknown attempt in registry order, and supporting evidence.
Evidence remains in the global journal. It is a trusted operator interface.

Schema version 2 upgrades existing archives and applies quarantine to existing
imported summaries. Open, backup and restore compare schema objects against
the exact supported definitions, including constraints, indexes and triggers.
