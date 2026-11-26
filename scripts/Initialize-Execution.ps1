[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PlanPath,
    [string]$ProjectRoot = (Get-Location).Path,
    [ValidateRange(1, 6)][int]$MaxWorkers = 2,
    [ValidateRange(1, 2147483647)][int]$TokenBudget = 200000
)
. "$PSScriptRoot\Orchestrator.Common.ps1"
$root = Get-RepoRoot $ProjectRoot
$lock = Enter-RunLock $root
try {
    $statePath = Join-Path $root '.execution/execution-state.json'
    if (Test-Path -LiteralPath $statePath) {
        throw 'State exists. Reconcile and archive before a new run.'
    }
    $registryPath = Join-Path $root '.execution/task-registry.json'
    if (Test-Path -LiteralPath $registryPath) {
        throw 'Orphan registry exists. Preserve and reconcile it first.'
    }
    $plan = (Resolve-Path -LiteralPath $PlanPath).Path
    $branch = Invoke-Git $root @('branch', '--show-current')
    if (-not $branch) { throw 'Attach HEAD to an integration branch first.' }
    $runId = 'run-' + [guid]::NewGuid().ToString('N')
    $parent = Split-Path $root -Parent
    $name = Split-Path $root -Leaf
    $state = [ordered]@{
        schema_version = 2
        run_id = $runId
        plan = @{
            path = $plan
            sha256 = (Get-FileHash $plan -Algorithm SHA256).Hash
        }
        repository = @{
            root = $root
            base_branch = "$branch"
            baseline_commit = "$(Invoke-Git $root @('rev-parse', 'HEAD'))"
            initial_status = @(Invoke-Git $root @('status', '--porcelain'))
            worktree_root = Join-Path $parent "$name.codex-worktrees\$runId"
        }
        limits = @{
            max_concurrent_workers = $MaxWorkers
            token_budget = $TokenBudget
            max_attempts = 2
        }
        created_at_utc = [DateTime]::UtcNow.ToString('o')
    }
    foreach ($dir in @('agent-prompts', 'agent-reports', 'logs')) {
        New-Item -ItemType Directory -Force `
            -Path (Join-Path $root ".execution/$dir") | Out-Null
    }
    Save-Registry $root @{ schema_version = 2; tasks = @() }
    Write-Json $statePath $state
    Write-Host "Initialized $runId; workers=$MaxWorkers; tokens=$TokenBudget"
} finally {
    $lock.Dispose()
}
