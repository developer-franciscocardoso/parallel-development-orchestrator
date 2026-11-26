---
name: parallel-development-orchestrator
description: >-
  Use when executing or resuming a substantial development plan with
  independent tasks in a Windows Git repository using Codex CLI workers.
---

# Parallel development orchestrator

Optimize accepted work per token and elapsed time. Parallelism is useful
only when independent work outweighs dispatch, discovery, and review costs.

## Choose the execution mode

| Work | Mode |
| --- | --- |
| Small fix, coupled files, or sequential reasoning | Primary session |
| Substantial independent implementation tasks | Isolated CLI workers |
| Focused investigation or review | Primary session or bounded read-only agent |

Start with two workers; six is a ceiling, not a target. Compare measured
time, total tokens, retries, and accepted results before raising concurrency.
Keep expensive builds sequential when they compete for CPU, memory or disk.
Read [operations](references/operations.md) for commands and recovery.

## Prepare

Inspect applicable project instructions and the registered plan. Read the
full plan once in the coordinator; give each worker only its relevant
excerpt, contracts, acceptance criteria, source references, and checks.
Project conventions come from the actual repository, never starter defaults.

When the plan is known, initialize it directly. Ask only if its identity or
requirements are missing. Preserve dirty changes and establish a clean
integration baseline without resetting, stashing, or discarding user work.

Group coupled changes. Register tasks with literal owned/excluded paths,
dependencies, model choice, effort, token reservation, and timeout.
Use low effort for straightforward tasks, medium for implementation, and
high for difficult reasoning. Select only locally available models; an empty
model explicitly inherits the CLI configuration. Escalate after diagnosis.

## Execute

Use the supplied runner for delegated implementation workers. It owns the
repository lock, process slots, explicit role prompts, usage and attempt
records. Workers cannot delegate. Do not launch extra CLI workers beside it.
Run read-only subagents outside implementation batches; include their usage
and coordinator usage separately in the final accounting.

Dispatch only tasks whose prerequisite commits are integrated and reachable
from the recorded integration branch. The runner pins each attempt to HEAD.
An accepted report alone does not establish a dependency base.

Monitor compact status; open full logs only for a failure or unclear result.
Unknown usage is unknown, never zero. Budget reservations are admission
controls, not hard model token limits; a running request can exceed them.

## Review, integrate, recover

A successful worker enters review, never automatic acceptance. Check the
actual diff, ownership, criterion evidence, tests, and commit. Merge reviewed
commits in dependency order, run affected integration checks, and record
integration only after ancestry and evidence are verified.
If integration checks fail, block dependent dispatch until repaired.

Reuse passing check evidence only for unchanged code and environment.
Run final relevant validation on the integrated result. Test success does
not establish browser usability or production behavior.

On interruption, inspect PID plus start time, descendants, Git and logs.
Preserve partial work. Reconcile stopped attempts before any new dispatch;
retry only after diagnosis, within two attempts. Missing usage blocks further
admission until evidence restores accounting. Never guess a successful exit.
Confirming a stopped process establishes neither success nor known usage.
Carry prior attempts and unknown usage into replacement-run reporting;
only the user can authorize resetting a task's attempt or budget allowance.

## Finish

Report completed/partial/blocked tasks, verification, measured usage and
timings, unknown totals, and retained branches/worktrees. Keep measured
results separate from expected improvements. Cleanup requires authorization
and an integrated, clean registered worktree; never delete branches here.
Completion requires integrated accepted work, passing required checks and no
active attempts. State accounting gaps even when implementation is complete.
