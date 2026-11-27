#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $FirstPackage,
    [Parameter(Mandatory)][string] $FirstSha256,
    [Parameter(Mandatory)][string] $NextPackage,
    [Parameter(Mandatory)][string] $NextSha256,
    [Parameter(Mandatory)][string] $ScratchDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$parent = Get-Item -LiteralPath $ScratchDirectory
$run = Join-Path $parent.FullName ('smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$setup = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts/Install-Pda.ps1'
$install = Join-Path $run 'install'
$launcher = Join-Path $install 'pda.ps1'
$state = Join-Path $run 'state'
$oldHome = $env:PDA_HOME
$env:PDA_HOME = $state

function Invoke-Installed([string[]] $Command) {
    $lines = & $launcher @Command --json
    if ($LASTEXITCODE -ne 0) { throw "Installed command failed: $($Command[0])" }
    $data = ($lines -join [Environment]::NewLine) | ConvertFrom-Json
    if ($Command -contains '--status') { return $data.installation }
    return $data
}
function Assert-Digest($Actual, [string] $Expected) {
    if ($Actual.packageSha256 -ne $Expected) { throw 'Unexpected active release digest.' }
}
try {
    $bad = '0' * 64
    try {
        & $setup -PackagePath $FirstPackage -PackageSha256 $bad -InstallDir $install
        throw 'Expected digest failure.'
    } catch {
        if ($_.Exception.Message -notmatch 'supplied digest') { throw }
    }
    if (Test-Path -LiteralPath $install) { throw 'Rejected digest changed the installation.' }
    & $setup -PackagePath $FirstPackage -PackageSha256 $FirstSha256 -InstallDir $install |
        Out-Null
    $status = @('update', '--status', '--install-dir', $install)
    $first = Invoke-Installed $status
    Assert-Digest $first $FirstSha256
    $initialized = Invoke-Installed @('init')
    if (!$initialized.initialized) { throw 'Fresh initialization failed.' }

    $repo = Join-Path $run 'project'
    New-Item -ItemType Directory -Path $repo | Out-Null
    & git -C $repo init -q -b main
    if ($LASTEXITCODE) { throw 'Cannot initialize acceptance repository.' }
    & git -C $repo -c user.name=fixture -c user.email=fixture@localhost -c commit.gpgsign=false commit -q --allow-empty -m initial
    if ($LASTEXITCODE) { throw 'Cannot commit acceptance baseline.' }
    [void](Invoke-Installed @('project', 'add', 'release-smoke', $repo))
    $profile = Invoke-Installed @('project', 'inspect', 'release-smoke')
    $updated = Invoke-Installed @('update', '--package', $NextPackage, '--sha256',
        $NextSha256, '--install-dir', $install)
    Assert-Digest $updated $NextSha256
    if ($updated.previousVersion -ne $first.version) { throw 'Upgrade lost previous release.' }
    Assert-Digest (Invoke-Installed $status) $NextSha256
    $back = Invoke-Installed @('update', '--rollback', '--install-dir', $install)
    Assert-Digest $back $FirstSha256
    Assert-Digest (Invoke-Installed $status) $FirstSha256
    $restored = Invoke-Installed @('project', 'inspect', 'release-smoke')
    if ($restored.version -ne $profile.version -or
        $restored.data.commit -ne $profile.data.commit) {
        throw 'Registered project did not survive the upgrade and rollback.'
    }
    [pscustomobject]@{ First = $first.version; Upgraded = $updated.version
        RolledBack = $back.version; PreservedCommit = $restored.data.commit
        InstallDirectory = $install; StateDirectory = $state }
} finally { $env:PDA_HOME = $oldHome }
