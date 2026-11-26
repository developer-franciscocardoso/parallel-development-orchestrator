[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TaskId,
    [Parameter(Mandatory = $true)]
    [ValidateSet('integrated', 'retry', 'interrupted')][string]$Status,
    [Parameter(Mandatory = $true)][string]$EvidencePath,
    [string]$ProjectRoot = (Get-Location).Path,
    [switch]$ConfirmStopped
)
. "$PSScriptRoot\Worker.Runtime.ps1"
$root = Get-RepoRoot $ProjectRoot
$lock = Enter-RunLock $root
try {
    $state = Get-Run $root
    $registry = Get-Registry $root
    $matches = @($registry.tasks | Where-Object { $_.id -eq $TaskId })
    if ($matches.Count -ne 1) { throw 'Unknown task.' }
    $task = $matches[0]
    $evidence = (Resolve-Path -LiteralPath $EvidencePath).Path
    if ((Get-Item -LiteralPath $evidence).Length -eq 0) {
        throw 'Evidence file must contain review or recovery findings.'
    }
    if ($Status -eq 'integrated') {
        if ($task.status -ne 'review') { throw 'Task must be in review.' }
        Assert-Clean $root
        $branch = "$(Invoke-Git $root @('branch', '--show-current'))"
        if ($branch -ne $state.repository.base_branch) {
            throw 'Integration must be verified on the recorded branch.'
        }
        Invoke-Git $root @('merge-base', '--is-ancestor',
            $task.commit, 'HEAD') | Out-Null
        $task.status = 'integrated'
    }
    if ($Status -eq 'interrupted') {
        if ($task.status -notin @('starting', 'running', 'interrupted')) {
            throw 'Only an unresolved attempt can be reconciled as interrupted.'
        }
        if (-not $ConfirmStopped) {
            throw 'Verify worker and descendants stopped; use -ConfirmStopped.'
        }
        $attempt = $task.attempts[-1]
        $process = $null
        if ($attempt.pid) {
            $process = Get-Process -Id $attempt.pid `
                -ErrorAction SilentlyContinue
        }
        if ($process -and $process.StartTime.ToUniversalTime().ToString('o') `
                -eq $attempt.process_start_utc) {
            throw 'Recorded process is still alive.'
        }
        $attempt.usage = Get-Usage $attempt.log
        $attempt.status = 'failed'
        $attempt.failure = 'Interrupted; exit result unknown. See evidence.'
        $task.status = 'failed'
    }
    if ($Status -eq 'retry') {
        if ($task.status -ne 'failed') { throw 'Only failed tasks can retry.' }
        if ($task.attempts.Count -ge $state.limits.max_attempts) {
            throw 'Attempt limit reached; reassess the task.'
        }
        Get-BudgetUsed $registry | Out-Null
        $task.status = 'pending'
    }
    $record = [pscustomobject]@{
        status = $Status
        path = $evidence
        sha256 = (Get-FileHash $evidence -Algorithm SHA256).Hash
        utc = [DateTime]::UtcNow.ToString('o')
    }
    if (-not $task.PSObject.Properties['evidence']) {
        $task | Add-Member evidence @()
    }
    $task.evidence = @($task.evidence) + @($record)
    Save-Registry $root $registry
} finally {
    $lock.Dispose()
}
