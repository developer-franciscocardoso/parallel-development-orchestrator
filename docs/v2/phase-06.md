# Candidate verification and integration

Each completed attempt records its implementation report, measured usage and
worktree identity. The coordinator compares those records with the pinned
plan and inspects the actual Git changes. Both names of a rename must be
within task ownership. Empty changes, protected files, ignored worker
output and unexpected Git state block capture.

The coordinator creates a candidate commit and runs the declared commands
in a fresh checkout under Windows MXC. Command evidence includes the
argument vector, exit status and hashes of bounded external logs. A second
checkout supports independent mechanical review of the same commit. Scope,
merge markers, possible key material and review size limits are checked.
High-risk tasks require specialist judgment and block under the default
mechanical reviewer.

After candidate verification, the coordinator prepares a merge against the
current plan head in a separate worktree. It runs regression commands from
the current and previously integrated tasks. It journals its intent before
advancing the local plan branch with an expected previous SHA. Recovery
uses that intent to distinguish a completed reference update from a pending
one. The original user checkout remains available throughout.

The focused test projects are Pda.Repositories.Tests, Pda.Quality.Tests
and Pda.Verification.Tests. Native MXC command tests require Windows and
the Codex executable. They use no model calls or account credentials.
