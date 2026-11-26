[CmdletBinding()]
param(
    [string]$ProjectRoot = (Get-Location).Path
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

$skillName = "parallel-development-orchestrator"
$skillSource = Join-Path $ProjectRoot $skillName
$skillDestination = Join-Path $ProjectRoot ".agents\skills\$skillName"
$skillFiles = @("SKILL.md", "references\operations.md")
$skillFilesToCopy = @()

foreach ($relative in $skillFiles) {
    $destination = Join-Path $skillDestination $relative
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        continue
    }
    if (-not (Test-Path -LiteralPath (Join-Path $skillSource $relative) -PathType Leaf)) {
        throw "Missing package file: $destination. Expected a bundled copy under $skillSource."
    }
    $skillFilesToCopy += $relative
}

$required = @(
    ".codex\agents\backend.toml",
    ".codex\agents\frontend.toml",
    ".codex\agents\data-schema.toml",
    ".codex\agents\integration.toml",
    ".codex\agents\testing.toml",
    ".codex\agents\reviewer.toml",
    "scripts\Orchestrator.Common.ps1",
    "scripts\Worker.Runtime.ps1",
    "scripts\Invoke-Workers.ps1",
    "scripts\Register-Tasks.ps1",
    "scripts\Set-TaskState.ps1",
    "templates\worker-prompt.md",
    "templates\worker-report.schema.json"
)

foreach ($relative in $required) {
    $path = Join-Path $ProjectRoot $relative
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing package file: $path"
    }
}

foreach ($relative in $skillFilesToCopy) {
    $destination = Join-Path $skillDestination $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $skillSource $relative) -Destination $destination
}

$dirs = @(
    ".execution\agent-prompts",
    ".execution\agent-reports",
    ".execution\logs",
    ".execution\archive",
    "scripts"
)

foreach ($relative in $dirs) {
    New-Item -ItemType Directory -Force `
        -Path (Join-Path $ProjectRoot $relative) | Out-Null
}

$gitIgnore = Join-Path $ProjectRoot ".gitignore"
$ignoreLines = @(
    ".execution/*",
    "!.execution/*.template.json",
    "!.execution/.gitkeep"
)

if (Test-Path -LiteralPath $gitIgnore) {
    $current = Get-Content -LiteralPath $gitIgnore
    $raw = [IO.File]::ReadAllText($gitIgnore)
    $missing = @($ignoreLines | Where-Object { $current -notcontains $_ })
    if ($missing.Count -and $raw.Length -and -not $raw.EndsWith("`n")) {
        [IO.File]::AppendAllText($gitIgnore, [Environment]::NewLine)
    }
    foreach ($line in $ignoreLines) {
        if ($current -notcontains $line) {
            Add-Content -LiteralPath $gitIgnore -Value $line
        }
    }
} else {
    Set-Content -LiteralPath $gitIgnore -Value $ignoreLines
}

Write-Host "Orchestrator directories and ignore rules are ready."
Write-Host "Merge config/config.toml.fragment into .codex/config.toml."
Write-Host "Merge config/AGENTS.orchestrator.md into root AGENTS.md."
Write-Host "Existing configuration files were not overwritten."
