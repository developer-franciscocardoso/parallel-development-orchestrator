[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TasksPath,
    [string]$ProjectRoot = (Get-Location).Path
)
. "$PSScriptRoot\Orchestrator.Common.ps1"
$root = Get-RepoRoot $ProjectRoot
$lock = Enter-RunLock $root
try {
    $state = Get-Run $root
    Assert-Plan $state
    $registry = Get-Registry $root
    $tasks = @(Read-Json $TasksPath)
    $ids = @($registry.tasks | ForEach-Object { $_.id })
    foreach ($task in $tasks) {
        Assert-Task $task
        if ($ids -contains $task.id) { throw "Duplicate ID: $($task.id)" }
        $ids += $task.id
    }
    foreach ($task in $tasks) {
        foreach ($dep in $task.dependencies) {
            if ($ids -notcontains $dep -or $dep -eq $task.id) {
                throw "Invalid dependency: $dep"
            }
        }
        foreach ($key in @('status', 'attempts', 'worktree', 'commit')) {
            if ($task.PSObject.Properties[$key]) {
                throw "Runtime field is reserved: $key"
            }
        }
        $task | Add-Member status 'pending'
        $task | Add-Member attempts @()
        $task | Add-Member worktree $null
        $task | Add-Member commit $null
    }
    $all = @($registry.tasks) + $tasks
    $remaining = @($all | ForEach-Object { $_.id })
    while ($remaining.Count) {
        $ready = @($all | Where-Object {
            $_.id -in $remaining -and
            @($_.dependencies | Where-Object { $_ -in $remaining }).Count -eq 0
        } | ForEach-Object { $_.id })
        if (-not $ready.Count) { throw 'Dependency graph contains a cycle.' }
        $remaining = @($remaining | Where-Object { $_ -notin $ready })
    }
    $registry.tasks = $all
    Save-Registry $root $registry
    Write-Host "Registered $($tasks.Count) tasks."
} finally {
    $lock.Dispose()
}
