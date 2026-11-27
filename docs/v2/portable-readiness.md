# Platform readiness

Windows hosts the V2 worker runtime. The supervisor launches suspended
processes in native job objects, and live Codex and sandboxed verification
require Windows MXC. A hosted runner without the native Codex executable
skips the explicitly marked MXC fixtures. Windows supervisor and
credential-free Git integration tests still execute.

The Linux CI job builds the full solution. It runs the core contracts,
SQLite persistence and migration, provider registry, Git worktrees,
repository discovery, planning, admission, simulated scheduling, quality
coordination and dashboard tests. The supervisor boundary test calls the
normal launch API and verifies that an unsupported host creates neither
worker identity nor evidence files. Deterministic agent lifecycle
fixtures exercise success, failure, timeout, cancellation and unknown
usage without a native worker.

The workflow executes on pushes to the integration branch and main, on
pull requests targeting main, and on manual dispatch. It needs read-only
repository access. It does not require an OpenAI account, provider tokens,
model calls or remote Git write credentials.

For local reproduction, use the Release build and test commands in
`.github/workflows/orchestrator-v2.yml`. The Windows development machine
had no installed WSL subsystem or container engine at the time of the
readiness check. Use the Linux CI job result for Linux execution evidence.
