# Recovery and startup coordination

The dispatcher must acquire IStateStore.AcquireStartupGuard before reserving
an attempt. Hold it through process identity publication, Running transition
and resume. Dispose immediately after startup. Recovery and explicit
reconciliation acquire the same exclusive guard before reading attempts,
worker identities or process observations. Lock contention raises IOException
before recovery changes state. Retry on a later scheduling tick.

The guard is an OS-held exclusive handle to a stable sibling of the SQLite
database. Its file persists across restarts. Closing the handle, including
process termination, relinquishes the lock. All participants must use the
same database path in the trusted local state directory. Never delete the
lock file to force recovery.

A worker identity is inserted once, bound to the plan and attempt ID.
Recovery checks the binding again before invoking the external inspector.
An observed stopped tree plus measured usage can create one Reconciled
fact per attempt. Matching replays preserve the version and journal length.
A Failed attempt with matching usage still needs that first fact before
retry authorization. Blocked attempts stay blocked after reconciliation.

Release tests exercise two independent StateStore instances sharing the
guard, synthetic live/stopped observations, immutable identity and corrupt
historical identity rejection, unknown startup, and replay across terminal
states. OS process-tree and Git inspection are separate module boundaries.

The local inspector now checks actual Windows job identity and Git branch,
base ancestry and the recorded canonical common-directory identity. A clone
with the same branch and commit cannot substitute for the original worktree.
Dirty partial work is preserved. Real job/Git inspection tests passed.

The coordinator also supervised an actual Codex native-MXC sandbox canary:
the sandboxed script started, the three-second deadline stopped its tree,
and no delayed marker appeared after the six-second script delay. This was
a no-model command, not a paid Codex implementation session.
