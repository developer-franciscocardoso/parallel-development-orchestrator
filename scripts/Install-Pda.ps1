#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string] $PackageSha256,
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Pda-Bin')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'This installer requires Windows.' }

function Assert-NoLinks([string] $Path) {
    for ($p = $Path; $p; $p = [IO.Path]::GetDirectoryName($p)) {
        try { $attributes = [IO.File]::GetAttributes($p) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if ($attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'Installation paths cannot cross filesystem links.'
        }
    }
}

function Get-LocalPath([string] $Path) {
    if (![IO.Path]::IsPathFullyQualified($Path)) {
        throw 'Supply an absolute local path.'
    }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if ($full -notmatch '^[A-Za-z]:\\' -or
        $full -eq [IO.Path]::GetPathRoot($full)) {
        throw 'Supply a directory or file on a local drive.'
    }
    Assert-NoLinks $full
    return $full
}

function Get-Entry($Zip, [string] $Name, [long] $Limit) {
    $entries = @($Zip.Entries | Where-Object { $_.FullName -ceq $Name })
    if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or
        $entries[0].Length -gt $Limit) { throw "Invalid ZIP entry: $Name" }
    return $entries[0]
}

function Copy-VerifiedEntry($Zip, $Manifest, [string] $Name, [string] $Target) {
    $record = @($Manifest.files | Where-Object { $_.path -ceq $Name })
    if ($record.Count -ne 1 -or $record[0].sha256 -notmatch '^[a-f0-9]{64}$') {
        throw "Missing release digest: $Name"
    }
    $entry = Get-Entry $Zip "app/$Name" 536870912
    $source = $entry.Open()
    try {
        $out = [IO.File]::Open($Target, 'CreateNew', 'Write', 'None')
        try { $source.CopyTo($out) } finally { $out.Dispose() }
    } finally { $source.Dispose() }
    if ((Get-FileHash -LiteralPath $Target).Hash -ne $record[0].sha256) {
        throw "Bootstrap file hash mismatch: $Name"
    }
}

function Assert-ManagedLauncher([string] $Root, [string] $ExistingHash) {
    $pointerPath = Join-Path $Root 'current.json'
    Assert-NoLinks $pointerPath
    $current = (Get-Content -Raw -LiteralPath $pointerPath |
        ConvertFrom-Json -AsHashtable).current
    if ($current.version -cnotmatch '^\d+\.\d+\.\d+([-+][A-Za-z0-9.+-]+)?$') {
        throw 'Conflicting launcher with invalid current pointer.'
    }
    $receipt = Join-Path $Root "versions/$($current.version)/.pda-manifest.json"
    Assert-NoLinks $receipt
    if ((Get-FileHash -LiteralPath $receipt).Hash -ne $current.manifestSha256) {
        throw 'Conflicting launcher with unverified current receipt.'
    }
    $prior = (Get-Content -Raw -LiteralPath $receipt | ConvertFrom-Json).files |
        Where-Object { $_.path -ceq 'pda.ps1' }
    if (@($prior).Count -ne 1 -or $prior.sha256 -ne $ExistingHash) {
        throw 'Refusing to replace an unrelated root launcher.'
    }
}

$package = Get-LocalPath $PackagePath
$root = Get-LocalPath $InstallDir
for ($dir = [IO.DirectoryInfo]::new($root); $dir; $dir = $dir.Parent) {
    if ((Test-Path -LiteralPath (Join-Path $dir.FullName '.git')) -or
        (Test-Path -LiteralPath (Join-Path $dir.FullName 'state.db'))) {
        throw 'Install outside Git worktrees and orchestrator state.'
    }
}
$inputFile = [IO.File]::Open($package, 'Open', 'Read', 'Read')
$stage = $null
try {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($algorithm.ComputeHash($inputFile)) }
    finally { $algorithm.Dispose() }
    if ($actual.Replace('-', '') -ne $PackageSha256) {
        throw 'Package SHA-256 differs from the independently supplied digest.'
    }
    $inputFile.Position = 0
    $zip = [IO.Compression.ZipArchive]::new($inputFile, 'Read', $true)
    try {
        $entry = Get-Entry $zip 'manifest.json' 1048576
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.UTF8Encoding]::new($false, $true))
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
        finally { $reader.Dispose() }
        if ($manifest.formatVersion -ne 1 -or $manifest.entryPoint -cne 'pda.exe') {
            throw 'Unsupported release manifest.'
        }
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Assert-NoLinks $root
        $folder = Join-Path $root ('.bootstrap-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder -ErrorAction Stop | Out-Null
        $stage = $folder
        $exe = Join-Path $stage 'pda.exe'
        $newLauncher = Join-Path $stage 'pda.ps1'
        Copy-VerifiedEntry $zip $manifest 'pda.exe' $exe
        Copy-VerifiedEntry $zip $manifest 'pda.ps1' $newLauncher
    } finally { $zip.Dispose() }
    $launcher = Join-Path $root 'pda.ps1'
    Assert-NoLinks $launcher
    $newHash = (Get-FileHash -LiteralPath $newLauncher).Hash
    $oldHash = $null
    if ([IO.File]::Exists($launcher)) {
        $oldHash = (Get-FileHash -LiteralPath $launcher).Hash
        if ($oldHash -ne $newHash) { Assert-ManagedLauncher $root $oldHash }
    }

    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $stage
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match '^(DOTNET_|CORECLR_|COR_)') {
            [void]$start.Environment.Remove($key)
        }
    }
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $stage 'bundle'
    foreach ($arg in @('update', '--package', $package, '--sha256',
        $PackageSha256, '--install-dir', $root, '--json')) {
        $start.ArgumentList.Add($arg)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "Verified installer failed: $($stderr.Result.Trim())"
        }
        $result = $stdout.Result.Trim()
    } finally { $process.Dispose() }
    if ($oldHash -ne $newHash) {
        Assert-NoLinks $launcher
        if ($oldHash -and (Get-FileHash -LiteralPath $launcher).Hash -ne $oldHash) {
            throw 'Launcher changed concurrently. Preserved the existing file.'
        }
        [IO.File]::Move($newLauncher, $launcher, [bool]$oldHash)
    }
    Write-Output $result
}
finally {
    $inputFile.Dispose()
    if ($stage -and [IO.Directory]::Exists($stage)) {
        $resolved = [IO.Path]::GetFullPath($stage)
        if ([IO.Path]::GetDirectoryName($resolved) -ne $root -or
            [IO.Path]::GetFileName($resolved) -notmatch '^\.bootstrap-[0-9a-f]{32}$') {
            throw 'Unexpected bootstrap cleanup path.'
        }
        Assert-NoLinks $resolved
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
