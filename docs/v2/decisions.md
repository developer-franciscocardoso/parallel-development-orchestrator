# Architecture decisions

## 1. Keep a single local application boundary

The CLI and the authenticated loopback dashboard compose the same
`OrchestratorHost`. Both use the same SQLite database and coordinator.
Control requests from another process persist cancellation and pause
signals. The web UI reads the event journal with a continuation cursor.
This permits a CLI run and an open dashboard to coexist across restarts.

The API handlers live with the dashboard HTTP host. The integration gate
lives with the quality coordinator and delegates Git operations to the
repository module. Journal-derived metrics live in the application layer.
These are logical modules inside the monolith. The immutable task contract
and state-store interface have no dependency on the runtime or SQLite.

## 2. Bind work to immutable evidence

A plan holds the inspected profile version and Git baseline SHA. Worker
admission reserves capacity and token budget before creating the worktree.
A real worker publishes OS process identity while suspended. Simulators
use a separate filesystem lease. An unknown worker or unknown final usage
requires reconciliation before reuse of capacity.

The trusted coordinator commits changes only after checking actual paths.
Checks and reviewer records bind the candidate SHA and task contract hash.
Integration verifies a separate merged checkout and journals the intent
to advance the plan branch. An interrupted update can be examined and
replayed without resetting the user's checkout.

## 3. Give each operation the smallest execution boundary it needs

Discovery reads committed Git objects. Native Windows workers use a
restricted Codex profile and a supervised job. Native verification runs
the declared executable and argument vector under the same filesystem and
network restrictions. Independent mechanical review reads bounded content
and records actionable findings. A high-risk contract requiring specialist
judgment blocks under the default reviewer.

## 4. Keep project data and operational state separate

Each state home owns its database, trusted evidence and managed worktrees
outside project checkouts. Legacy import retains original evidence bytes
and hashes. Backup validates a new copy before publication and restore
requires a fresh destination. Local execution and report export work
without publishing branches or transmitting telemetry.
