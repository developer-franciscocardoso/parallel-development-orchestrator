#Requires -Version 7.0
# Stable launcher. Keep the entry point outside version-specific directories.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'This package requires Windows x64.' }

function Assert-RegularPath([string] $Path) {
    for ($item = Get-Item -Force -LiteralPath $Path; $item; ) {
        if ($item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'The installed launcher cannot cross a filesystem link.'
        }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory }
        else { $item = $item.Parent }
    }
}

function Read-SmallJson([string] $Path) {
    Assert-RegularPath $Path
    if ([IO.FileInfo]::new($Path).Length -gt 1048576) {
        throw 'Unexpected metadata size.'
    }
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable
}

$pointer = Read-SmallJson (Join-Path $PSScriptRoot 'current.json')
$active = $pointer.current
if ($pointer.formatVersion -ne 1 -or $active.entryPoint -cne 'pda.exe' -or
    $active.version -cnotmatch '^\d+\.\d+\.\d+([-+][A-Za-z0-9.+-]+)?$' -or
    $active.manifestSha256 -cnotmatch '^[a-fA-F0-9]{64}$') {
    throw 'Invalid installation pointer.'
}
$directory = Join-Path $PSScriptRoot ('versions/' + $active.version)
$receipt = Join-Path $directory '.pda-manifest.json'
Assert-RegularPath $receipt
if ((Get-FileHash -LiteralPath $receipt).Hash -ne $active.manifestSha256) {
    throw 'Installed receipt failed hash verification.'
}
$manifest = Read-SmallJson $receipt
if ($manifest.formatVersion -ne 1 -or $manifest.version -cne $active.version -or
    $manifest.entryPoint -cne 'pda.exe') { throw 'Invalid release receipt.' }
foreach ($name in @('pda.exe', 'pda-worker-report.schema.json')) {
    $entry = @($manifest.files | Where-Object { $_.path -ceq $name })
    $file = Join-Path $directory $name
    Assert-RegularPath $file
    if ($entry.Count -ne 1 -or
        (Get-FileHash -LiteralPath $file).Hash -ne $entry[0].sha256) {
        throw "Installed file failed hash verification: $name"
    }
}
$exe = Join-Path $directory 'pda.exe'
& $exe @args
exit $LASTEXITCODE
