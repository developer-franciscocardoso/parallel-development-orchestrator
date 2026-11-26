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

The implemented foundation includes validated task contracts, transactional
SQLite state and history, bounded reservations, backup/restore, legacy
quarantine, Windows process-tree supervision, isolated Git worktrees,
conservative recovery and a separate provider/model registry.

V2 is not yet a complete autonomous platform. Runtime integration and later
phases are tracked in the [execution plan](docs/v2/implementation-plan.md).
See the [audit](docs/v2/audit.md), [architecture](docs/v2/architecture.md),
and individual phase reports for tested behavior and limitations.
