# P02A process supervision

The Windows implementation launches a fully qualified executable using
CreateProcessW, an explicitly quoted argument array and an optional
replacement environment. The supervisor uses suspended creation and
the creation-time job-list attribute. Kernel job assignment therefore
precedes execution and failure prevents creation. The only handles
passed to the worker are the three redirected pipe endpoints.

The callback receives the PID, kernel creation time in UTC and a unique
session-local job name before the primary thread resumes. It must commit
the identity before returning. Cancellation bounds the wait for persistence
even if the callback ignores its token. Such a callback can finish later,
so callers must keep late writes idempotent. A failed callback terminates the suspended process.
Cancellation before launch throws. Cancellation or timeout during execution
returns an outcome after checking that the job has zero active processes.
The deadline includes persistence time. Disposal signals active runs.
Await each run to observe full cleanup or an explicit cleanup failure.

Output files use exclusive creation, retain raw bytes, drain concurrently
and report truncation independently at the configured per-file limit.
Standard input is UTF-8 without BOM. Root exit, cancellation and timeout
terminate every job member. Kernel kill-on-last-handle-close handles
abrupt supervisor death. The job lifetime guarantee assumes external
privileged code does not duplicate or retain handles. Filesystem and
network isolation require a separate sandbox.

## Public API

- ProcessSupervisor.RunAsync takes ProcessRequest, a required asynchronous
  persistence callback and an optional cancellation token.
- ProcessOutcome carries identity, native exit code, timeout/cancellation,
  verified tree closure and per-stream truncation flags.
- ProcessSupervisor.Inspect returns Live, Stopped or Unknown for durable
  supervisor-generated identities. No process is signalled by PID.
  Missing tree evidence cannot establish successful work or token usage.
- Non-Windows launches throw PlatformNotSupportedException. Non-Windows
  inspection returns Unknown. Jobs require Windows creation-time job
  association support and compatible limits from any enclosing job.

## Verification

The first test build failed with CS0246 for the missing public contract.
After implementation, 10 Windows tests passed in Debug and Release.
During review, a timeout test for uncooperative persistence and a test
for a mismatched live identity both failed on the original implementation.
The fixes bound callback waiting and check both native job membership
and root creation time before identifying the tree as live. The extended
Release suite passed all 13 tests, including environment replacement.
The Release command was
`dotnet test tests/Pda.Execution.Tests -c Release --no-restore --nologo`.
Tests run actual Windows PowerShell, ping and a separate .NET supervisor
host. The crash test kills only that host and observes both the worker and
its descendant exit. Also covered are literal argv, stdin and both output
streams, nonzero exit, bounded logs, timeout, cancellation, pre-resume
persistence failure, supervisor disposal, parent exit leaving a child
and live/stopped identity inspection. No paid runtime or deployment was
invoked. Incompatible external job policies were not separately induced.
