# P02: process lifetime, repositories and reconciliation

Dependencies: P01 accepted. Read-only: Core and Persistence. Risk high.
Maximum attempts 2. Timeout 3600 seconds. No paid calls or remote publishing.

## Process ownership task P02A

Own src/Pda.Execution and tests/Pda.Execution.Tests. Provide a native
process supervisor with executable, argument array, working directory,
stdin, stdout/stderr paths, timeout and cancellation. Return process ID,
start identity, exit code, cancellation/timeout and termination evidence.
Persist process-start identity through a callback before untrusted work
is resumed. Windows: create suspended, assign kill-on-close Job Object,
resume. Close/terminate the job on timeout, cancellation and parent exit;
wait for closure before completion. No hidden shell evaluation. Handle
failed job assignment without allowing the process to execute. Bound log
storage and preserve evidence. Non-Windows guarantees must be explicit.

Tests use real child processes: literal argument quoting, stdin and output,
nonzero exit, cancellation, timeout, parent exits leaving child, failure
before resume and supervisor disposal. Do not write a fake-only supervisor.

## Repository task P02B

Own src/Pda.Repositories and tests/Pda.Repositories.Tests. Depend on the
process contract after P02A passes. Register clean Git repositories; pin
full base SHAs, create unique task/attempt branches and external worktrees,
list/inspect state, obtain NUL-delimited changed paths including renames,
verify ancestry, reject unsafe refs and escaped paths. Disable hooks for
orchestrator Git operations; do not inherit unexpected Git environment.
Never reset or remove dirty work. Integration comes later. Tests operate
on temporary real repositories, covering spaces, non-ASCII paths, dirty
source refusal, external worktree containment and preserved failed work.

## Recovery task P02C

Own src/Pda.Recovery and tests/Pda.Recovery.Tests. Reconcile durable attempt
metadata against process identity, worktree, branch, base and logs. Missing
process alone cannot establish success or known usage. Interrupted startup
without PID is ambiguous and blocks retry. Recovery is idempotent and
preserves all partial work. Explicit stopped-process evidence precedes retry
authorization. Unknown usage remains unknown and blocks automatic admission.

## Deliverables and integration

Each task supplies focused source, behavioral tests, actual test results,
public API summary and limitations in its phase report. Coordinator adds
projects to solution, independently reviews and executes integrated tests.
Only then may P03 start. A failed OS capability is recorded as an external
blocker, never replaced by a less secure execution mode.
