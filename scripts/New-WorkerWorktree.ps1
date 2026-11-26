[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{1,60}$')]
    [string]$TaskId,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{1,50}$')]
    [string]$Slug = 'worker',
    [string]$ProjectRoot = (Get-Location).Path
)
. "$PSScriptRoot\Orchestrator.Common.ps1"
$root = Get-RepoRoot $ProjectRoot
$lock = Enter-RunLock $root
try {
    $state = Get-Run $root
    Assert-Plan $state
    Assert-Clean $root
    $base = "$(Invoke-Git $root @('rev-parse', 'HEAD'))"
    $branch = "codex/$($state.run_id)/$TaskId-$Slug"
    $path = Join-Path $state.repository.worktree_root "$TaskId-$Slug"
    if (-not (Test-ChildPath $path $state.repository.worktree_root)) {
        throw 'Worktree must be inside this run.'
    }
    Invoke-Git $root @('worktree', 'add', '-b', $branch, $path, $base)
    [pscustomobject]@{ branch = $branch; path = $path; base_commit = $base }
} finally {
    $lock.Dispose()
}
