# P03 runtime, policy and evidence

Pda.Agents exposes IAgentRuntime, AgentRequest, RuntimeDiscovery,
AgentCallbacks and RuntimeResult. CodexRuntime uses the native supervisor.
The suspended worker's identity is sent to Started before resume. The
callback must persist identity and be idempotent if it finishes late.
Diagnostics run without model calls before each launch. The deadline covers
capability inspection and execution. Partial raw logs and worktrees survive
failures. Short stdout events become visible before the worker exits.
An explicitly selected SimulatedRuntime produces deterministic local
evidence without publishing a native process identity.

The request carries task and attempt IDs, worktree, fresh external log and
report paths, stdin prompt, deadline and optional model. The executable,
isolated home and approved tooling/cache directories are deployment
configuration. CodexPolicy.WorkerOptions constructs the native overrides
also usable by a separate sandboxed verification service. Approved tool
directories enter PATH and acquire read-only access. Explicit cache roots
are read-only. The default has no user cache or credential directories.
Each run requires an absolute trusted binary outside checkouts, a
dedicated home without a user config layer, and no project config layer.
No caller credentials or environment variables are copied automatically.

The adapter inspects installed help, validates strict CLI configuration,
checks effective feature states, verifies empty ordinary MCP and installed
plugin catalogs, and checks for incompatible managed requirements.
The absence of enterprise requirements alone does not block execution.
An optional deployment verifier may impose additional restrictions.
MXC confinement is tested in the target checkout before authentication:
a positive workspace write, denied external profile reads and writes,
protected Git metadata and blocked loopback connectivity with a reachable
host control. Ambient temporary directories are denied. The real CLI
receives a custom permissions profile with root deny, minimal system
read access, workspace access, network disabled and approvals set to never.
The adapter disables nested agents, hooks, apps, plugins, browser/computer
tools and web search. Native process-tree ownership is separate from
the shell sandbox. Codex itself requires a provider connection for an
authenticated model session.

JSONL events are parsed as writes arrive. Failed or incomplete turns,
truncation, invalid UTF-8/JSON, missing or invalid counts, overflow,
conflicting totals and cached tokens above input leave usage unknown.
Cached tokens form part of input. Worker reports use a trusted shipped
schema and a fresh external path. Identity and structure are checked;
a reported completion still requires independent verification.

## Evidence

The initial failing builds recorded missing contract types. Additional
red tests reproduced seven input/evidence boundary bugs and the buffering
of a short live event. The corrected suites include simulated success,
crash, cancellation, timeout and unknown usage; real supervised
PowerShell JSONL/report fixtures; validation of task/attempt binding;
malformed streams; external path validation and catalog conflicts.

Local no-model discovery ran the installed codex-cli 0.171.0-alpha.13.2
using the actual adapter, a clean temporary profile and a real temporary
Git repository. It reported events, reports, usage, restricted-tools and
mxc, then returned Authentication for the empty profile. All native
policy checks passed, including the Git write and loopback canaries.
The isolated login was not configured and no paid session was invoked.
An operator may authenticate the explicitly chosen dedicated CODEX_HOME
with `codex -c 'cli_auth_credentials_store="file"' login` before a real
run. Never reuse or copy a personal auth.json into task storage.

The final focused Release runs passed 26 agent tests, seven security tests
and 13 process tests. One native case preserves a complete valid turn at
the exact truncation boundary and confirms unknown usage.

The focused Release commands are `dotnet test tests/Pda.Agents.Tests -c
Release`, `dotnet test tests/Pda.Security.Tests -c Release` and
`dotnet test tests/Pda.Execution.Tests -c Release`. A paid end-to-end
session, provider billing and production deployment are unverified.
