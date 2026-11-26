# Scheduling and verified integration contract

P05 depends on accepted P04. P06 depends on accepted P05. Every development
assignment has two attempts, a 3600-second timeout and high risk. These
are two sequential implementation gates, not permission to skip review.

## P05 scheduler and application service

Use the common Core state store, runtime registry, provider registry,
Git service and startup guard. Dispatch depends on integrated predecessors,
priority, fair queue order, capabilities, deadlines, provider quotas,
token reservations and measured host resource pressure. Never exceed six.
Atomically reserve before worktree/process creation; persist intended
worktree metadata before side effects. Keep attempts and partial evidence.
Hold the shared startup guard until durable worker identity and Running
state exist. A suspended process already assigned to its job is live;
release the guard once identity is published so other workers can start.
Cancellation and timeout must reconcile actual process closure.

The simulator has no native process identity. Record explicit simulated
evidence rather than inventing a PID. Runtime reports and known usage move
successful work only to AwaitingVerification. Unknown usage stops admission.
No automatic retries of failures. Pause/resume/cancel controls share the
same application service used later by CLI and web. Store all state changes
and errors, including model assignment and reservations, durably.

Tests use real temporary Git repositories and deterministic runtime
factories, not paid model calls. Cover duplicate dispatch, max concurrency,
dependencies, resource pressure, deadlines, cancellation, unknown tokens,
rate limits and restart. Document measured scheduler behavior.

## P06 candidate ownership and quality

Git metadata is protected from worker tools. A worker may return completed
edits with an empty commit field. The trusted coordinator checks every
changed old/new path, read-only/excluded boundaries and untracked files,
then creates the candidate commit. Never broaden worker sandbox permissions
to let it commit. A supplied worker commit must match inspected Git state.
Bind evidence to candidate SHA, base SHA and task-contract digest.

Run declared builds/static checks/tests in the same constrained sandbox.
Use a separate reviewer interface; an implementation report is never its
own approval. Structured findings have severity, location, impact and
verification. Simulated review is explicitly identified in test runs.
Security/scope failures cannot be waived by the worker or auto-retried.

## Integration destination

Create a dedicated local plan integration branch/worktree from the clean
registered project baseline. The user's checkout is not the mutable
integration workspace. Merge verified candidates in separate disposable
integration worktrees, run regression checks against the combined result,
and advance only the orchestrator-owned plan branch with expected-old-SHA
checks. Preserve conflicts and failed branches. Never reset dirty work.
Return the resulting branch and commit in the final report; remote
publication and deployment remain separate explicitly authorized actions.

Tests cover out-of-scope renames, empty changes, failed independent checks,
review rejection, changed candidates/bases/target heads, Git conflicts,
combined regression failures and successful dependent tasks. Integrate only
after candidate-bound evidence and successful resulting commit validation.
