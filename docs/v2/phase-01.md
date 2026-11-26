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

Reservations enforce a global six-slot ceiling, cross-plan ownership within
a project, plan budgets, dependency integration and attempt limits.
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
