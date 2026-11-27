# Local orchestration host

The CLI and dashboard open the same state directory. Initialization creates
`state.db`, `host.json`, `providers.json`, worktree storage and evidence
storage. Keep this directory outside registered checkouts. Existing
configuration survives initialization. Reopen the host after configuration
changes. Plan concurrency changes take effect at the next reservation.

The default runtime is `simulated`. Its local fixture records synthetic usage
and a report without modifying a user repository. Real quality gates require a
nonempty candidate, independent checks and review before integration.

## Enable a live Codex worker

Install native Codex for Windows and authenticate a dedicated Codex home.
Use the same dedicated home for login and dispatch. In PowerShell, set
`$env:CODEX_HOME` to that directory and run
`codex -c 'cli_auth_credentials_store="file"' login`. The host never imports
credentials from the desktop session.

Set these fields in `host.json`. Replace the executable, directory and model
with the installation and entitlement actually in use.

```json
{
  "defaultConcurrency": 2,
  "enableLiveCodex": true,
  "codexExecutable": "C:\\Tools\\Codex\\codex.exe",
  "codexHome": "C:\\PdaState\\codex-home",
  "codexModel": "YOUR_MODEL_ID",
  "toolDirectories": [],
  "readOnlyDirectories": []
}
```

Add the following provider to the existing `providers` array in
`providers.json`. These sample limits are administrator-selected admission
ceilings. Configure them for the account in use.

```json
{
  "id": "openai",
  "endpoint": "https://api.openai.com/v1",
  "maxConcurrentRequests": 2,
  "requestsPerMinute": 10,
  "tokensPerMinute": 50000
}
```

For a ChatGPT login use the supported
`https://chatgpt.com/backend-api/codex` endpoint. The live adapter applies
the chosen URL explicitly, uses Responses and requires native OpenAI
authentication. Custom routes and credential environment overrides are
rejected. Each live task names the `codex` runtime.

Add a corresponding entry to the existing `models` array. Supply verified
model capacity values in place of the example numbers.

```json
{
  "id": "YOUR_MODEL_ID",
  "providerId": "openai",
  "runtimeIds": ["codex"],
  "contextTokens": 32000,
  "maxOutputTokens": 4000
}
```

The runtime repeats its confinement and authentication checks during dispatch.
Only an authenticated live session establishes account readiness
and model access. Verification commands use the native sandbox and require no
model session. Grant SDK or cache reads only where the task needs them.

Live Codex admission requires a positive token reservation. The reservation
controls local admission. For a hard billing ceiling, configure an
enforced provider-side spending limit before enabling paid workers.
