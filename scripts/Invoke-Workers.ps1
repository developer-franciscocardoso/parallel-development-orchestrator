[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$TaskIds,
    [string]$ProjectRoot = (Get-Location).Path,
    [string]$CliPath = 'codex'
)
. "$PSScriptRoot\Worker.Runtime.ps1"
$root = Get-RepoRoot $ProjectRoot
$lock = Enter-RunLock $root
$active = [Collections.Generic.List[object]]::new()
$admissionError = $null
try {
    $state = Get-Run $root
    $registry = Get-Registry $root
    Assert-Plan $state
    Assert-Clean $root
    Assert-NoUnresolvedWorkers $registry
    $branch = "$(Invoke-Git $root @('branch', '--show-current'))"
    if ($branch -ne $state.repository.base_branch) {
        throw 'Switch to the recorded integration branch before dispatch.'
    }
    $cli = (Get-Command $CliPath -CommandType Application).Source
    $help = (& $cli exec --help 2>&1) -join "`n"
    foreach ($flag in @('--cd', '--json', '--output-schema',
            '--output-last-message', '--sandbox')) {
        if (-not $help.Contains($flag)) { throw "CLI lacks $flag" }
    }
    $queue = [Collections.Generic.Queue[object]]::new()
    foreach ($id in ($TaskIds | Select-Object -Unique)) {
        $task = @($registry.tasks | Where-Object { $_.id -eq $id })
        if ($task.Count -ne 1 -or $task[0].status -ne 'pending') {
            throw "Task $id must be registered and pending."
        }
        Assert-Task $task[0]
        Assert-Integrated $root $task[0] $registry
        $queue.Enqueue($task[0])
    }
    Assert-NoOverlap $queue.ToArray()
    while ($queue.Count -or $active.Count) {
        Update-ActiveWorkers $active $root $registry
        if (-not $queue.Count -or
                $active.Count -ge $state.limits.max_concurrent_workers) {
            if ($active.Count) { Start-Sleep -Milliseconds 250 }
            continue
        }
        $admission = Get-Admission $registry $state $queue.Peek() $active.Count
        if ($admission -eq 'wait') {
            Start-Sleep -Milliseconds 250
            continue
        }
        if ($admission -ne 'admit') {
            $admissionError = $admission
            $queue.Clear()
            continue
        }
        $worker = Start-Worker $queue.Dequeue() $state $registry $cli
        $active.Add($worker)
    }
    if ($admissionError) { throw $admissionError }
} finally {
    # Preserve active processes on interruption; state blocks further dispatch.
    $lock.Dispose()
}
