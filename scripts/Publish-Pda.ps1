#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'Publish the Windows release on Windows.' }
$number = '(?:0|[1-9][0-9]*)'
$identifier = '[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*'
$semver = "^$number\.$number\.$number(?:-$identifier)?(?:\+$identifier)?`$"
if ($Version.Length -gt 80 -or $Version -cnotmatch $semver) {
    throw 'Use a numeric major.minor.patch version with an optional prerelease.'
}
$sourceRoot = Split-Path $PSScriptRoot -Parent
$dest = [IO.Path]::GetFullPath($OutputDirectory)
for ($dir = [IO.DirectoryInfo]::new($dest); $dir; $dir = $dir.Parent) {
    if (Test-Path -LiteralPath (Join-Path $dir.FullName '.git')) {
        throw 'Select an output directory outside Git worktrees.'
    }
    if ($dir.Exists -and $dir.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw 'Release output cannot cross directory links.'
    }
}
[IO.Directory]::CreateDirectory($dest) | Out-Null
$name = "pda-$Version-win-x64.zip"
$archive = Join-Path $dest $name
$checksum = "$archive.sha256"
if ([IO.File]::Exists($archive) -or [IO.File]::Exists($checksum)) {
    throw 'Release files already exist. Select a fresh output directory.'
}
$stage = Join-Path $dest ('.pda-publish-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $stage 'package/app'
[IO.Directory]::CreateDirectory($app) | Out-Null

try {
    $project = Join-Path $sourceRoot 'src/Pda.Cli/Pda.Cli.csproj'
    $publish = @('publish', $project, '--configuration', 'Release',
        '--runtime', 'win-x64', '--self-contained', 'true', '--output', $app,
        '-m:1', '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:PublishTrimmed=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', "-p:Version=$Version")
    & dotnet @publish | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    $exe = Join-Path $app 'pda.exe'
    if (![IO.File]::Exists($exe)) { throw 'Missing bundled executable.' }
    $schema = Join-Path $sourceRoot 'src/Pda.Agents/pda-worker-report.schema.json'
    [IO.File]::Copy($schema, (Join-Path $app 'pda-worker-report.schema.json'), $true)
    [IO.File]::Copy((Join-Path $PSScriptRoot 'pda.ps1'),
        (Join-Path $app 'pda.ps1'))
    $files = @(Get-ChildItem -LiteralPath $app -Recurse -File | Sort-Object FullName |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($app, $_.FullName).Replace('\', '/')
            [ordered]@{ path = $relative; sha256 = (
                Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
            ).Hash.ToLowerInvariant() }
        })
    $manifest = [ordered]@{ formatVersion = 1; version = $Version
        entryPoint = 'pda.exe'; files = $files }
    $package = Join-Path $stage 'package'
    [IO.File]::WriteAllText((Join-Path $package 'manifest.json'),
        ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $zip = Join-Path $stage $name
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $zip,
        [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $bootstrap = Join-Path $PSScriptRoot 'Install-Pda.ps1'
    $bootstrapCopy = Join-Path $dest 'Install-Pda.ps1'
    if ([IO.File]::Exists($bootstrapCopy)) {
        if ((Get-FileHash -LiteralPath $bootstrapCopy).Hash -ne
            (Get-FileHash -LiteralPath $bootstrap).Hash) {
            throw 'Output contains a different bootstrap script.'
        }
    } else { [IO.File]::Copy($bootstrap, $bootstrapCopy) }
    [IO.File]::WriteAllText((Join-Path $stage 'digest'), "$hash  $name`n")
    [IO.File]::Move($zip, $archive)
    [IO.File]::Move((Join-Path $stage 'digest'), $checksum)
    [pscustomobject]@{ Package = $archive; Sha256 = $hash
        Bootstrap = $bootstrapCopy
        BootstrapSha256 = (Get-FileHash -LiteralPath $bootstrapCopy).Hash.ToLowerInvariant() }
}
finally {
    $resolved = [IO.Path]::GetFullPath($stage)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $dest -and
        [IO.Path]::GetFileName($resolved) -match '^\.pda-publish-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
