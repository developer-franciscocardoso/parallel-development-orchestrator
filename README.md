# Parallel development orchestrator

A project-local Codex skill for coordinating independent implementation tasks in Windows Git worktrees.

## Install

Download [codex-orchestrator.zip](./codex-orchestrator.zip) and extract its contents directly into the destination repository root. Review any filename conflicts before overwriting files. Commit the package files so worker worktrees can access them. Use Windows PowerShell 5.1 or newer with Git and an authenticated Codex CLI on PATH.

Open a fresh Codex session in the project and ask:

> Use $parallel-development-orchestrator to execute the plan in plan.md.

See the [operations guide](.agents/skills/parallel-development-orchestrator/references/operations.md) for task registration, review, integration, and recovery.

## V2 development

The additive V2 implementation targets .NET 10. The legacy entry point and
execution data remain supported and are not rewritten by V2.

```powershell
dotnet test Pda.slnx -c Release
```

The native CLI and local Blazor dashboard share SQLite state, task
contracts, admission controls, worker supervision and recovery. Completed
work is verified in an independent checkout before integration into a
local plan branch.

```powershell
dotnet run --project src/Pda.Cli -c Release -- --help
dotnet run --project src/Pda.Cli -c Release -- doctor --json
```

Start with the [CLI and dashboard guide](docs/v2/cli-dashboard.md).
The [host settings](src/Pda.Hosting/README.md) describe local simulation
and the dedicated Codex runtime. [State commands](docs/v2/state-commands.md)
cover legacy import, backup and restore.

The [execution plan](docs/v2/implementation-plan.md) records tested phases
and outstanding work. The [audit](docs/v2/audit.md),
[architecture](docs/v2/architecture.md) and
[verification guide](docs/v2/phase-06.md) describe the compatibility and
security boundaries.
