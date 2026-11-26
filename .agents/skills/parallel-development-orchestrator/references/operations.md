# Operations

Run from the target repository root using Windows PowerShell 5.1 or newer.
The complete package must be installed; copying SKILL.md alone is insufficient.
Scripts invoke Git and Codex without shell evaluation. Do not execute task
briefs or verification command strings through Invoke-Expression.

## Setup and first batch

Extract the ZIP at the repository root. Commit the package files so worker worktrees receive them. Start a fresh Codex session in the repository.

```powershell
powershell -NoProfile -File scripts/Initialize-Execution.ps1 `
  -PlanPath C:\plans\implementation.md -MaxWorkers 2 -TokenBudget 200000
powershell -NoProfile -File scripts/Register-Tasks.ps1 `
  -TasksPath .execution/tasks.json
```

Create task JSON using the shape in
[task example](../../../../templates/tasks.example.json).
The brief contains the relevant plan excerpt, shared contracts, file
references and any environment setup. Prefer one coherent task over many
small ones. Declare a generous token reservation based on observed runs.
Treat the example's budget as a starting estimate, not a prediction.

From an existing PowerShell session, pass arrays directly:

```powershell
& .\scripts\Invoke-Workers.ps1 -TaskIds T001,T002
& .\scripts\Get-ExecutionStatus.ps1
```

The runner maintains up to the configured live processes and starts the next
queued task when a slot opens. Every task in a batch must have dependencies
already integrated. New worktrees do not include ignored dependencies,
environment files, databases or build outputs. Supply documented setup;
do not copy secrets or reuse mutable build/database state across workers.

The task model and effort are applied explicitly by the runner. Role text
comes from the selected .codex/agents/<role>.toml instruction block and is
inserted into the worker prompt. Other custom-agent TOML settings are for
native subagents and are not silently applied to standalone CLI workers.
The runner disables nested agents and uses workspace-write; it does not
relax approval policy or automatically authorize hooks.
If sandbox policy prevents commits through shared Git worktree metadata,
report the attempt blocked. Do not broaden permissions to force it through.
Preserve the diff for coordinator review under the operator's normal policy.

Owned paths are normalized and compared by directory boundaries. Overlapping
ownership in one batch is rejected conservatively, even if exclusions might
make it safe. Split batches or assign disjoint literal paths instead.

## Review and integration

Read the report and diff against attempt.base_commit. Check all acceptance
criteria and test commands against the task; worker reports are claims, not
independent proof. Review security and shared contracts proportionately.

Merge the reviewed commit on the recorded integration branch using normal
Git commands. Preserve ancestry: this version expects merge or fast-forward,
not squash/cherry-pick. Run relevant integration checks. Put commands,
results, reviewed commit and findings in .execution/review-T001.md, then:

```powershell
& .\scripts\Set-TaskState.ps1 -TaskId T001 -Status integrated `
  -EvidencePath .execution/review-T001.md
```

This records evidence and verifies ancestry; it does not merge or run tests.
Ownership checks reject committed out-of-scope changes after execution.
They are not a filesystem security boundary. Inspect ignored/untracked
artifacts and external effects during review.

## Interrupted or failed attempts

The repository lock is released when its owning process exits. A registry
record can still represent a live worker. PID alone is insufficient: compare
process_start_utc, inspect descendants, Git status, commits and output files.
A starting record without a PID is ambiguous: investigate processes before
confirming that all of the attempt's processes stopped.

After verifying stopped processes, write recovery findings:

```powershell
& .\scripts\Set-TaskState.ps1 -TaskId T001 -Status interrupted `
  -EvidencePath .execution/recovery-T001.md -ConfirmStopped
& .\scripts\Set-TaskState.ps1 -TaskId T001 -Status retry `
  -EvidencePath .execution/recovery-T001.md
```

Interrupted attempts retain unknown exit codes. A retry gets a new worktree,
keeps the previous attempt, and requires reviewing useful partial work first.
Update the task brief through a reviewed registry edit if recovery requires
reusing a prior patch; never reset the old worktree. No automatic retries.

Unknown usage blocks further dispatch, including retries. Recover authentic
usage events from CLI logs if available. If usage cannot be recovered, report
the run as partial and obtain a consciously revised budget/run rather than
inventing zero usage. Do not edit records just to bypass admission checks.

Runtime JSON updates are atomic and retain .bak copies. Recover a backup only
after checking current processes and Git; do not treat backups as current.
Legacy schema-1 runs must be reconciled and archived before initialization.
The task registry is the sole dependency graph; do not maintain a second copy.

## Accounting and cleanup

JSONL turn.completed usage is summed per attempt. Cached input is a subset of
input tokens and is not added again. Missing/invalid usage remains unknown.
Reservations include in-flight attempts, but are not hard spending caps.
Reports cover CLI workers only. Include coordinator and separate read-only
agent usage when comparing whole-run efficiency; label unavailable totals.

Benchmark equivalent tasks from the same base and environment with one,
two and four workers, several repetitions, and identical acceptance checks.
Compare median wall time, worker plus coordinator tokens, retries, conflicts
and accepted work. Do not infer savings from shorter prompt text alone.

Retained worktrees do not occupy process slots. When cleanup is authorized:

```powershell
& .\scripts\Remove-WorkerWorktree.ps1 -WorktreePath C:\path\to\worker
```

Removal requires a clean registered worktree under this run and HEAD reachable
from integration. Branches remain. Never use force deletion to bypass checks.
