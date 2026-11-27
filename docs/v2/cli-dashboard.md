# Native CLI and dashboard

The CLI and the local dashboard share the same state directory, services and
execution journal. In PowerShell, set the state location to a directory
outside every managed repository.

```powershell
$env:PDA_HOME = Join-Path $env:LOCALAPPDATA Pda
pda init
pda doctor --json
pda project add sample C:\Projects\Sample
pda project inspect sample --json
```

Registration inspects the committed Git tree. For a changed baseline, run
`pda project inspect sample --refresh` before creating a new plan. Use a
clean checked-out branch when executing a plan.

```powershell
pda plan create sample feature --budget 24000 --tasks tasks.json
pda plan validate feature
pda task list feature --json
pda plan execute feature
pda execution status feature --json
pda review list feature
pda integration list feature
```

The tasks file is a JSON array of task contracts described in
[repository discovery and planning](phase-04.md). For a single task, use
`--objective` and repeat `--owned` for explicit ownership. Supply
`--checks` with a JSON array of executable/argument-vector objects when
the project does not advertise an applicable test command. Run
`pda --help` for the complete command list.

For interrupted work, use `pda execution recover PLAN` and inspect the
journal, worker evidence and retained Git checkout. Explicit retry needs
a reason. Reconciliation of unknown usage needs evidence and a measured
token count. The default simulated worker returns fixture evidence and
does not implement arbitrary user objectives. A real implementation
requires a configured Codex adapter.

The dashboard serves HTTP on loopback port 7412 by default. Generate a
random secret in the shell that starts it. The login form accepts that
secret; keep it out of command-line arguments and URLs.

```powershell
$env:PDA_DASHBOARD_TOKEN = [Convert]::ToHexString(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
pda dashboard
```

Visit `http://127.0.0.1:7412`. API clients send the token as a bearer
credential. Browser sessions use a cookie and require a CSRF token and
same-origin request for controls. Authenticated server-sent events include
a sequence cursor for reconnecting. Report exports contain local task and
execution evidence and should be handled as project data.
