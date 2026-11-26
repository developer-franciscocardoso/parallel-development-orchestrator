. "$PSScriptRoot\Orchestrator.Common.ps1"

function Get-RoleInstructions([string]$Root, [string]$Role) {
    $path = Join-Path $Root ".codex/agents/$Role.toml"
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $match = [regex]::Match($text,
        '(?s)developer_instructions\s*=\s*"""(.*?)"""')
    if (-not $match.Success) {
        throw 'Role requires a literal triple-double-quoted instruction block.'
    }
    $match.Groups[1].Value.Trim()
}

function Get-TaskCharge($Task) {
    $total = 0L
    foreach ($attempt in $Task.attempts) {
        if ($attempt.status -eq 'running') {
            $total += [long]$Task.token_reservation
            continue
        }
        if ($null -eq $attempt.usage) {
            throw 'Usage unknown. Reconcile before launching more workers.'
        }
        $total += [long]$attempt.usage.total_tokens
    }
    $total
}

function Get-BudgetUsed($Registry) {
    $total = 0L
    foreach ($task in $Registry.tasks) { $total += Get-TaskCharge $task }
    $total
}

function Get-Admission($Registry, $State, $Next, [int]$ActiveCount) {
    try { $used = Get-BudgetUsed $Registry } catch {
        return $_.Exception.Message
    }
    if ($used + $Next.token_reservation -le $State.limits.token_budget) {
        return 'admit'
    }
    if ($ActiveCount) { return 'wait' }
    'Token budget cannot admit next reservation.'
}

function Update-ActiveWorkers($Active, [string]$Root, $Registry) {
    foreach ($worker in @($Active.ToArray())) {
        Stop-ExpiredWorker $worker
        if (-not $worker.process.HasExited) { continue }
        Complete-Worker $worker
        $Active.Remove($worker) | Out-Null
        Save-Registry $Root $Registry
    }
}

function Stop-ExpiredWorker($Worker) {
    $age = [DateTime]::UtcNow - [DateTime]$Worker.attempt.started_at_utc
    if ($age.TotalSeconds -le $Worker.task.timeout_seconds -or
            $Worker.process.HasExited) { return }
    $Worker.attempt.failure = 'Worker timeout; review partial work.'
    $ErrorActionPreference = 'Continue'
    & taskkill /PID $Worker.process.Id /T /F 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Timeout cleanup failed. Reconcile process descendants manually.'
    }
}

function Assert-NoUnresolvedWorkers($Registry) {
    $unresolved = @($Registry.tasks | Where-Object {
        $_.status -in @('starting', 'running', 'interrupted')
    })
    if ($unresolved.Count) {
        throw 'Unresolved worker attempts. Inspect status and reconcile first.'
    }
}

function Start-Worker($Task, $State, $Registry, [string]$CliPath) {
    $root = $State.repository.root
    Assert-Clean $root
    Assert-Task $Task
    Assert-Integrated $root $Task $Registry
    $number = $Task.attempts.Count + 1
    if ($number -gt $State.limits.max_attempts) {
        throw "Attempt limit reached for $($Task.id)."
    }
    $base = "$(Invoke-Git $root @('rev-parse', 'HEAD'))"
    $name = "$($Task.id)-$number"
    $branch = "codex/$($State.run_id)/$name"
    $worktree = Join-Path $State.repository.worktree_root $name
    $role = Get-RoleInstructions $root $Task.role
    $templatePath = Join-Path $root 'templates/worker-prompt.md'
    $template = Get-Content -LiteralPath $templatePath -Raw -Encoding UTF8
    $prompt = $template.Replace('{{ROLE_INSTRUCTIONS}}', $role)
    $prompt = $prompt.Replace('{{TASK_JSON}}',
        ($Task | Select-Object id, brief, dependencies, owned_paths,
            excluded_paths, acceptance_criteria, verification_commands |
            ConvertTo-Json -Depth 10))
    $prompt = $prompt.Replace('{{BASE_COMMIT}}', $base)
    $promptPath = Join-Path $root ".execution/agent-prompts/$name.md"
    [IO.File]::WriteAllText($promptPath, $prompt,
        [Text.UTF8Encoding]::new($false))
    Invoke-Git $root @('worktree', 'add', '-b', $branch, $worktree, $base) |
        Out-Null
    $prefix = Join-Path $root ".execution/agent-reports/$name"
    $attempt = [pscustomobject]@{
        number = $number
        status = 'starting'
        base_commit = $base
        branch = $branch
        worktree = $worktree
        pid = $null
        process_start_utc = $null
        started_at_utc = [DateTime]::UtcNow.ToString('o')
        elapsed_seconds = $null
        exit_code = $null
        usage = $null
        log = "$prefix.jsonl"
        stderr = "$prefix.stderr.txt"
        report = "$prefix.report.json"
        failure = $null
    }
    $Task.attempts = @($Task.attempts) + @($attempt)
    $Task.status = 'starting'
    $Task.worktree = $worktree
    Save-Registry $root $Registry
    $cliArgs = @('exec', '--cd', $worktree, '--json', '--sandbox',
        'workspace-write', '--output-schema',
        (Join-Path $root 'templates/worker-report.schema.json'),
        '--output-last-message', $attempt.report,
        '-c', 'agents.enabled=false',
        '-c', ('model_reasoning_effort="' + $Task.effort + '"'))
    if ($Task.model) { $cliArgs += @('--model', $Task.model) }
    $cliArgs += '-'
    $arguments = ($cliArgs | ForEach-Object {
        ConvertTo-NativeArgument "$_"
    }) -join ' '
    try {
        $process = Start-Process -FilePath $CliPath -ArgumentList $arguments `
            -WorkingDirectory $worktree -WindowStyle Hidden -PassThru `
            -RedirectStandardInput $promptPath `
            -RedirectStandardOutput $attempt.log `
            -RedirectStandardError $attempt.stderr
        $null = $process.Handle
        $attempt.pid = $process.Id
        $attempt.process_start_utc = $process.StartTime.ToUniversalTime().
            ToString('o')
        $attempt.status = 'running'
        $Task.status = 'running'
        Save-Registry $root $Registry
        [pscustomobject]@{
            task = $Task; attempt = $attempt; process = $process
        }
    } catch {
        $Task.status = 'interrupted'
        $attempt.status = 'interrupted'
        $attempt.failure = $_.Exception.Message
        Save-Registry $root $Registry
        throw
    }
}

function Assert-WorkerResult($Worker) {
    $task = $Worker.task
    $attempt = $Worker.attempt
    if ($attempt.exit_code -ne 0) { throw 'Worker exited unsuccessfully.' }
    $report = Read-Json $attempt.report
    if ($report.task_id -ne $task.id -or $report.status -ne 'completed') {
        throw 'Worker report is blocked, incomplete, or for another task.'
    }
    if (-not $report.tests.Count -or -not $report.criteria.Count) {
        throw 'Worker report lacks verification evidence.'
    }
    $failed = @($report.tests | Where-Object { $_.exit_code -ne 0 })
    if ($failed.Count) { throw 'Worker reported failed verification.' }
    foreach ($command in $task.verification_commands) {
        if ($report.tests.command -notcontains $command) {
            throw "Missing required verification: $command"
        }
    }
    Assert-Clean $attempt.worktree
    $head = "$(Invoke-Git $attempt.worktree @('rev-parse', 'HEAD'))"
    if ($report.commit -ne $head -or $head -eq $attempt.base_commit) {
        throw 'Report commit does not identify a new worker commit.'
    }
    Invoke-Git $attempt.worktree @('merge-base', '--is-ancestor',
        $attempt.base_commit, $head) | Out-Null
    $paths = @(Invoke-Git $attempt.worktree @('-c', 'core.quotepath=false',
        'diff', '--no-renames', '--name-only', $attempt.base_commit, $head))
    foreach ($path in $paths) {
        if (-not (Test-Owned $path $task)) {
            throw "Out-of-scope change: $path"
        }
    }
    $task.commit = $head
}

function Complete-Worker($Worker) {
    $process = $Worker.process
    $process.WaitForExit()
    $attempt = $Worker.attempt
    $attempt.exit_code = $process.ExitCode
    $elapsed = [DateTime]::UtcNow - [DateTime]$attempt.started_at_utc
    $attempt.elapsed_seconds = [math]::Round($elapsed.TotalSeconds, 2)
    $attempt.usage = Get-Usage $attempt.log
    try {
        if ($attempt.failure) { throw $attempt.failure }
        Assert-WorkerResult $Worker
        $attempt.status = 'review'
        $Worker.task.status = 'review'
    } catch {
        $attempt.status = 'failed'
        $Worker.task.status = 'failed'
        $attempt.failure = $_.Exception.Message
    }
    $process.Dispose()
}
