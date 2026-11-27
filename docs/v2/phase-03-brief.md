# P03: runtime and provider contracts

Depends on accepted P02. Own src/Pda.Agents, src/Pda.Providers,
src/Pda.Security and matching tests. Shared Core changes need coordinator
review. Risk high, maximum attempts 2, timeout 3600 seconds.

Use the real ProcessSupervisor for lifetime ownership. Agent execution is
separate from provider/model registry. Expose capabilities, discovery,
authentication availability without reading secret values, start callback,
streamed events, cancellation, deadline, structured result, error category
and nullable usage. Do not claim adapters for unimplemented runtimes.

Codex adapter must inspect installed version/help and fail closed on missing
capabilities. Use trusted executable and isolated runtime state/config;
never copy credentials automatically. Authenticate through an explicitly
configured runtime profile. Repository/user instructions cannot modify
trusted platform policy. Do not launch real paid sessions in routine tests.

Native MXC is verified locally for basic filesystem/network restrictions.
Use permission profiles rather than --sandbox, which takes precedence over
default_permissions. CLI quoted dotted keys failed; inline tables worked:

```text
-c windows.sandbox="mxc"
-c permissions.pda.extends=":workspace"
-c permissions.pda.filesystem={":root"="deny",":minimal"="read"}
-c permissions.pda.network.enabled=false
```

This is not sufficient alone: validate effective agent integrations,
project configuration, hooks, MCP, plugins, web tools and environment.
No blanket ordinary MCP-disable key is documented; do not invent one or
assume an empty table removes inherited entries. Require verified managed
policy/effective configuration before declaring a strict runtime ready.
Unknown readiness must be a diagnostic denial, never a sandbox bypass.

Parse JSONL conservatively: malformed/truncated lines, failed turns, missing
usage, overflow, negative tokens and cached counts above input make total
usage unknown. Cached input is a subset, not extra tokens. Bind report to
task/attempt, check declared output structure, preserve logs and partial
work. Runtime success only enters independent verification.

Provide deterministic simulated agents for success, crash, timeout,
cancellation and unknown usage. Keep simulator behavior explicitly selected
and distinguish its evidence from real Codex. Tests cover actual parser and
launch argument/policy generation without paid calls. Report any external
capability blocker accurately; continue independent adapter and simulator
work rather than weakening security.
