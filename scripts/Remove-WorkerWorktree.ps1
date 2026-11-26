[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$WorktreePath,
    [string]$ProjectRoot = (Get-Location).Path
)
. "$PSScriptRoot\Worker.Runtime.ps1"
$root = Get-RepoRoot $ProjectRoot
$lock = Enter-RunLock $root
try {
    $state = Get-Run $root
    $target = Get-NormalPath $WorktreePath
    if (-not (Test-ChildPath $target $state.repository.worktree_root)) {
        throw 'Refusing a path outside this run.'
    }
    $listed = @(Invoke-Git $root @('worktree', 'list', '--porcelain'))
    $paths = @($listed | Where-Object { $_.StartsWith('worktree ') } |
        ForEach-Object { Get-NormalPath $_.Substring(9) })
    if ($paths -notcontains $target -or $target -eq $root) {
        throw 'Target is not a registered worker worktree.'
    }
    $registry = Get-Registry $root
    Assert-NoUnresolvedWorkers $registry
    $unsafe = @($registry.tasks | Where-Object {
        $_.worktree -eq $target -and $_.status -ne 'integrated'
    })
    if ($unsafe.Count) { throw 'Worker has not been integrated.' }
    Assert-Clean $target
    $head = "$(Invoke-Git $target @('rev-parse', 'HEAD'))"
    Invoke-Git $root @('merge-base', '--is-ancestor', $head, 'HEAD') |
        Out-Null
    if ($PSCmdlet.ShouldProcess($target, 'Remove integrated Git worktree')) {
        Invoke-Git $root @('worktree', 'remove', $target)
    }
} finally {
    $lock.Dispose()
}
