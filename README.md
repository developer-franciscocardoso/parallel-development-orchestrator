# Parallel development orchestrator

A project-local Codex skill for coordinating independent implementation tasks in Windows Git worktrees.

## Install

Download [codex-orchestrator.zip](./codex-orchestrator.zip) and extract its contents directly into the destination repository root. Review any filename conflicts before overwriting files. Commit the package files so worker worktrees can access them. Use Windows PowerShell 5.1 or newer with Git and an authenticated Codex CLI on PATH.

Open a fresh Codex session in the project and ask:

> Use $parallel-development-orchestrator to execute the plan in plan.md.

See the [operations guide](.agents/skills/parallel-development-orchestrator/references/operations.md) for task registration, review, integration, and recovery.
