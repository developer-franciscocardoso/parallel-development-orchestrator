Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalPath([string]$Path) {
    [IO.Path]::GetFullPath($Path.Replace('/', '\')).TrimEnd('\')
}

function Test-ChildPath([string]$Path, [string]$Root) {
    (Get-NormalPath $Path).StartsWith(
        (Get-NormalPath $Root) + '\',
        [StringComparison]::OrdinalIgnoreCase
    )
}

function Invoke-Git([string]$Root, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $result = @(& git -C $Root @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Git failed: $($Arguments -join ' '): $($result -join ' ')"
    }
    $result | ForEach-Object { "$_" }
}

function Get-RepoRoot([string]$Path) {
    $root = Invoke-Git $Path @('rev-parse', '--show-toplevel')
    Get-NormalPath "$root"
}

function Read-Json([string]$Path) {
    $value = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $value
}

function Write-Json([string]$Path, $Value) {
    $temp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    $json = $Value | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Path) {
        [IO.File]::Replace($temp, $Path, "$Path.bak", $true)
    } else {
        [IO.File]::Move($temp, $Path)
    }
}

function Enter-RunLock([string]$Root) {
    $dir = Join-Path $Root '.execution'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        [IO.File]::Open((Join-Path $dir 'run.lock'), 'OpenOrCreate',
            'ReadWrite', 'None')
    } catch {
        throw 'Another orchestrator owns this repository. Do not dispatch.'
    }
}

function Get-Run([string]$Root) {
    $state = Read-Json (Join-Path $Root '.execution/execution-state.json')
    if ($state.schema_version -ne 2) {
        throw 'Legacy state: reconcile workers, archive, then reinitialize.'
    }
    if ((Get-NormalPath $state.repository.root) -ne $Root) {
        throw 'Execution state belongs to a different repository.'
    }
    $state
}

function Get-Registry([string]$Root) {
    Read-Json (Join-Path $Root '.execution/task-registry.json')
}

function Save-Registry([string]$Root, $Registry) {
    Write-Json (Join-Path $Root '.execution/task-registry.json') $Registry
}

function Assert-Clean([string]$Root) {
    $status = @(Invoke-Git $Root @('status', '--porcelain'))
    if ($status.Count) {
        throw 'Checkout is dirty. Preserve changes and establish a clean base.'
    }
}

function Assert-Plan($State) {
    $hash = (Get-FileHash -LiteralPath $State.plan.path -Algorithm SHA256).Hash
    if ($hash -ne $State.plan.sha256) {
        throw 'Plan changed. Reconcile requirements before a new run.'
    }
}

function Assert-Integrated([string]$Root, $Task, $Registry) {
    foreach ($id in $Task.dependencies) {
        $dep = @($Registry.tasks | Where-Object { $_.id -eq $id })
        if ($dep.Count -ne 1 -or $dep[0].status -ne 'integrated') {
            throw "Dependency $id is not integrated."
        }
        Invoke-Git $Root @('merge-base', '--is-ancestor',
            $dep[0].commit, 'HEAD') | Out-Null
    }
}

function Assert-Task($Task) {
    foreach ($key in @('id', 'role', 'brief', 'dependencies', 'owned_paths',
            'excluded_paths', 'acceptance_criteria', 'verification_commands',
            'token_reservation', 'timeout_seconds', 'model', 'effort')) {
        if ($Task.PSObject.Properties.Name -notcontains $key) {
            throw "Task missing $key."
        }
    }
    if ($Task.id -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{1,60}$') {
        throw 'Invalid task ID.'
    }
    if ($Task.role -notmatch '^[a-z][a-z-]+$') { throw 'Invalid role.' }
    foreach ($key in @('token_reservation', 'timeout_seconds')) {
        $number = 0L
        if (-not [long]::TryParse("$($Task.$key)", [ref]$number) -or
                $number -lt 1) {
            throw 'Token reservation and timeout must be positive integers.'
        }
    }
    if ($Task.effort -notin @('low', 'medium', 'high')) {
        throw 'Use a supported low, medium, or high reasoning effort.'
    }
    if (-not $Task.brief -or -not $Task.acceptance_criteria.Count) {
        throw 'Task requires a brief and acceptance criteria.'
    }
    foreach ($path in @($Task.owned_paths) + @($Task.excluded_paths)) {
        if ([string]::IsNullOrWhiteSpace($path) -or
                $path -match '(^[/\\]|:|(^|[/\\])\.{1,2}([/\\]|$)|[*?\[])') {
            throw 'Ownership paths must be literal repository-relative paths.'
        }
    }
    if (-not $Task.owned_paths.Count) { throw 'Declare owned paths.' }
    if ($Task.role -eq 'reviewer') {
        throw 'Reviewer is read-only; use it outside implementation batches.'
    }
}

function Assert-NoOverlap($Tasks) {
    $owners = @()
    foreach ($task in $Tasks) {
        foreach ($path in $task.owned_paths) {
            $normalized = $path.Replace('\', '/').TrimEnd('/')
            $conflict = @($owners | Where-Object {
                $_.id -ne $task.id -and ($_.path -eq $normalized -or
                    $_.path.StartsWith("$normalized/",
                        [StringComparison]::OrdinalIgnoreCase) -or
                    $normalized.StartsWith("$($_.path)/",
                        [StringComparison]::OrdinalIgnoreCase))
            })
            if ($conflict.Count) { throw 'Task ownership paths overlap.' }
            $owners += [pscustomobject]@{ id = $task.id; path = $normalized }
        }
    }
}

function Test-Owned([string]$Path, $Task) {
    $path = $Path.Replace('\', '/').TrimEnd('/')
    $allowed = $false
    foreach ($owned in $Task.owned_paths) {
        $prefix = $owned.Replace('\', '/').TrimEnd('/')
        if ($path -eq $prefix -or $path.StartsWith("$prefix/")) {
            $allowed = $true
        }
    }
    foreach ($excluded in $Task.excluded_paths) {
        $prefix = $excluded.Replace('\', '/').TrimEnd('/')
        if ($path -eq $prefix -or $path.StartsWith("$prefix/")) {
            return $false
        }
    }
    $allowed
}

function Get-Usage([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $inputTokens = 0L
    $cached = 0L
    $outputTokens = 0L
    $found = $false
    $incomplete = $false
    foreach ($line in [IO.File]::ReadLines($Path)) {
        try { $event = $line | ConvertFrom-Json } catch { continue }
        if (-not $event -or -not $event.PSObject.Properties['type']) {
            continue
        }
        if ($event.type -in @('turn.started', 'turn.failed')) {
            $incomplete = $true
        }
        if ($event.type -ne 'turn.completed') { continue }
        if (-not $event.PSObject.Properties['usage']) { return $null }
        $usage = $event.usage
        if ($null -eq $usage) { return $null }
        foreach ($key in @('input_tokens', 'output_tokens')) {
            if (-not $usage.PSObject.Properties[$key] -or
                    "$($usage.$key)" -notmatch '^\d+$') { return $null }
        }
        $inputTokens += [long]$usage.input_tokens
        $outputTokens += [long]$usage.output_tokens
        if ($usage.PSObject.Properties['cached_input_tokens']) {
            if ("$($usage.cached_input_tokens)" -notmatch '^\d+$' -or
                    $usage.cached_input_tokens -gt $usage.input_tokens) {
                return $null
            }
            $cached += [long]$usage.cached_input_tokens
        }
        $found = $true
        $incomplete = $false
    }
    if (-not $found -or $incomplete) { return $null }
    [pscustomobject]@{
        input_tokens = $inputTokens
        cached_input_tokens = $cached
        output_tokens = $outputTokens
        total_tokens = $inputTokens + $outputTokens
    }
}

function ConvertTo-NativeArgument([string]$Value) {
    # Windows CRT escaping, including quotes and trailing backslashes.
    '"' + [regex]::Replace(
        [regex]::Replace($Value, '(\\*)"', '$1$1\"'),
        '(\\+)$', '$1$1') + '"'
}
