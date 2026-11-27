# Migrating and protecting execution state

The native CLI uses `--home DIR` or `PDA_HOME` for the local V2 state directory.
Add `--json` to emit compact machine-readable output. Each command returns
zero on success and a nonzero exit code for rejected input or failed operations.

## Import a legacy run

Pass the directory containing the original schema-version-2
`execution-state.json` and `task-registry.json` files.

```powershell
pda --home C:\PdaState --json migrate C:\LegacyRun\.execution
pda --home C:\PdaState --json migration list
```

Import snapshots the exact bytes of both JSON documents, their absolute
source paths and their SHA-256 digests in one SQLite transaction. The
source files stay in place. Reimporting identical bytes from identical
paths reuses the evidence. A digest identifies the bytes and does not
authenticate their author. Review the source before importing it. Source
code, commands and legacy task objectives are never executed on import.

A running or starting legacy attempt, or a missing or invalid token count,
blocks new admissions. Determine whether all affected workers have stopped
and recover measured token totals for each unknown attempt. Keep unresolved
usage unknown until there is evidence. Save measured totals as a JSON array
of nonnegative integers in original registry order. An active registry with
no unknown totals uses an empty array. Supply the registry path and SHA-256
shown by `migration list`.

```powershell
pda --home C:\PdaState migration reconcile C:\LegacyRun\.execution\task-registry.json $sha256 --confirm-stopped --usage C:\Evidence\totals.json --evidence "Worker closure and usage verified in run 27"
```

Reconciliation binds those measurements to the imported registry and appends
an audit event. Preserve the logs cited in the evidence alongside the run.

## Back up and restore SQLite

```powershell
pda --home C:\PdaState backup C:\Backups\pda-2026-11-27.db
pda restore C:\Backups\pda-2026-11-27.db C:\Recovered\state.db
```

The destination parent must already exist. Both commands require a fresh
destination filename. Backup takes a consistent SQLite snapshot including
committed WAL changes. Restore checks database integrity, foreign keys and
the recognized V2 schema. It writes through a temporary file and publishes
the recovered database only after validation. Restore works independently
of the current state directory.

The snapshot covers the database. For full operational recovery, separately
preserve the orchestrator configuration, worktree registrations, Git
repositories and execution evidence at the recorded locations. After
restoring them, inspect affected plans with `execution status PLAN` and run
`execution recover PLAN` to reconcile worker state.
