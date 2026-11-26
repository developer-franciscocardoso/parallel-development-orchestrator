# Phase 0: repository audit and migration boundary

Baseline: `3d04e8eb39b466bd90c88526ee94ec2cd3fa2bf4` (2026-11-26).
Read every tracked text file, including all eleven PowerShell scripts,
six agent definitions, templates, skill, operations guide and README.
The ZIP entries match the corresponding tracked text files byte-for-text.
All PowerShell files parsed without errors. No existing automated tests,
benchmarks, CI, application projects or database exist.

## Inventory

| Area | Verified behavior | V2 disposition |
| --- | --- | --- |
| Entry point | Local Codex skill and operations guide | Preserve |
| Tasks | Required fields, unique IDs, DAG cycle check | Extend contracts |
| Dispatch | Manual batch, integrated dependency ancestry | Replace scheduler |
| Ownership | Literal prefixes, exclusions, net-diff check | Preserve and harden |
| Isolation | Separate branch/worktree per attempt | Preserve |
| State | Exclusive file lock, atomic JSON replacement | Replace with SQLite |
| Recovery | Manual stopped-process confirmation | Extend reconciliation |
| Accounting | Reservations, unknown usage blocks admission | Preserve invariant |
| Quality | Worker claims checked, review is manual | Independent checks |
| Integration | Manual merge, ancestry/evidence registration | Controlled service |
| Cleanup | Clean integrated worktrees, confirmation | Preserve evidence |
| Runtime | Codex exec, workspace-write, JSONL reports | Adapter abstraction |
| Packaging | ZIP and installer helper | Preserve legacy package |

## Findings from source

- Common.Get-Usage skips malformed JSONL and clears failed-turn uncertainty
  on a later completion. V2 must fail closed on corrupted accounting.
- Runtime.Start-Worker creates a worktree before storing its attempt and
  starts a process before storing its PID. Both are crash windows.
- Runtime.Stop-ExpiredWorker uses taskkill only while the parent is alive.
  Descendants can outlive successful parents; there are no Job Objects.
- Result verification trusts worker test exit codes. It is a review entry
  gate, not independent verification. Set-TaskState does not execute tests.
- Path comparison is Windows-specific and has inconsistent case handling.
- Scope checks inspect committed net changes, not external side effects.
- Installation mentions absent config fragments. No files are removed.
- State and registry are separate writes, without transactional journaling.

## Migration strategy

Add V2 beside the unchanged legacy scripts. V2 stores its SQLite database
outside worker-owned repository paths. Import legacy JSON as immutable,
hashed evidence with explicit schema and source location. Never rewrite,
delete or silently resume legacy executions. Unknown usage stays unknown;
active legacy attempts require reconciliation before scheduling. Back up
SQLite through its online backup API and validate integrity before restore.
Rollback means stopping V2 and using the retained legacy entry point/data.

## Coordination decision

The legacy runtime is unsuitable for supervising this migration because
process-tree ownership and independent verification are unproven. Use
isolated Git worktrees with bounded development agents and coordinator
integration. The host supports three child agents, below the six ceiling.
Keep phase gates sequential. No paid agent calls are needed for tests.

## Validation limits

Parsing is not end-to-end execution. Codex 0.171.0-alpha.13.2 is installed;
exec help exposes the required flags. Authentication and real paid execution
have not been tested. No legacy worker was launched during this audit.

## Reproduced legacy defects

Controlled fixtures executed against the actual Common script confirmed:

| Fixture | Actual | Required conservative result |
| --- | --- | --- |
| 15-token completion then truncated JSON | Known 15 | Unknown |
| Failed turn then 15-token completion | Known 15 | Unknown |
| Owner Src, changed src/file.cs on Windows | Denied | Consistent case rules |
| Owner Src, changed src | Allowed | Consistent case rules |

Overlap validation rejects owners Src and src/file.cs, confirming that
ownership admission and result checking disagree. These checks did not
modify the repository, launch Codex or spend model tokens. The amount of
missing usage cannot be inferred from the fixtures; only the loss of
uncertainty is demonstrated.

## Standalone sandbox readiness

The coordinator executed the supported command shape without a model call:

```powershell
codex sandbox -- pwsh -NoProfile -Command "Write-Output 'pda-sandbox-canary'"
```

It exited 1 before the canary ran:
`windows sandbox failed: app runtime provisioning service is unavailable; refusing helper fallback`

This confirms that the installed bundled CLI's standalone sandbox path is
unavailable in this session. It does not establish that every desktop
execution path fails. No bypass flag, credentials, or paid session was used.

Follow-up with explicit native MXC succeeded (exit 0, output `MXC_OK`):

```powershell
codex -c windows.sandbox=mxc sandbox --include-managed-config `
  --permission-profile :workspace -- cmd.exe /d /c echo MXC_OK
```

This resolves basic sandbox startup without installing another runtime or
changing machine accounts/firewall rules. It is not yet validation of all
read, write, network, credential and configuration boundaries. The adapter
must explicitly select and validate this supported mode rather than rely
on the unavailable default provisioning path.

The next no-model synthetic boundary probe also passed on this host:
workspace writes succeeded; external reads (direct and through a workspace
junction), external writes, .git writes and loopback TCP were denied.
The unsandboxed loopback control connected successfully first. The profile
extended :workspace with :root=deny, :minimal=read and network.enabled=false.
Pass the filesystem rules as one TOML inline table override; quoted dotted
CLI key components were interpreted as literal quotes by this version.
This does not validate effective agent integrations/MCP/plugin policy,
credential isolation or paid Codex execution. Those remain separate gates.
