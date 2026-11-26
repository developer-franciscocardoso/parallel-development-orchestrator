[CmdletBinding()]
param([string]$ProjectRoot = (Get-Location).Path)
. "$PSScriptRoot\Worker.Runtime.ps1"
$root = Get-RepoRoot $ProjectRoot
$state = Get-Run $root
$registry = Get-Registry $root
$totals = [ordered]@{
    input_tokens = 0L
    cached_input_tokens = 0L
    output_tokens = 0L
    unknown_attempts = 0
    active_reservations = 0L
    coordinator_tokens = $null
}
foreach ($task in $registry.tasks) {
    foreach ($attempt in $task.attempts) {
        if ($null -eq $attempt.usage) {
            $totals.unknown_attempts++
        } else {
            $totals.input_tokens += $attempt.usage.input_tokens
            $totals.cached_input_tokens += $attempt.usage.cached_input_tokens
            $totals.output_tokens += $attempt.usage.output_tokens
        }
        if ($attempt.status -in @('starting', 'running')) {
            $totals.active_reservations += $task.token_reservation
        }
    }
}
$rows = foreach ($task in $registry.tasks) {
    $last = $null
    if ($task.attempts.Count) { $last = $task.attempts[-1] }
    $live = $false
    if ($last -and $last.pid) {
        $process = Get-Process -Id $last.pid -ErrorAction SilentlyContinue
        $live = $process -and
            $process.StartTime.ToUniversalTime().ToString('o') -eq
            $last.process_start_utc
    }
    [pscustomobject]@{
        id = $task.id
        status = $task.status
        attempts = $task.attempts.Count
        recorded_process_alive = [bool]$live
        latest_attempt = $last
    }
}
[pscustomobject]@{
    run_id = $state.run_id
    limits = $state.limits
    worker_usage = $totals
    tasks = @($rows)
} | ConvertTo-Json -Depth 20
