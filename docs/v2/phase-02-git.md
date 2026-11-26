# Phase 02 repository worktrees

`GitRepositoryService.Register` resolves the top-level worktree and the
shared Git directory. It requires a clean local branch and records a
complete commit ID. Linked worktrees have different physical roots and
the same common-directory identity. Filesystem links and junctions in
directory ancestry are rejected. Windows paths use the resolved local
drive and long directory names.

`PlanAttempt` validates bounded task and attempt IDs and prepares immutable
metadata for a dedicated branch and a unique path. The application can
persist this evidence before calling `CreateAttempt`. Supply an existing
absolute, trusted storage directory outside the registered repository
and other checkouts. Distinct attempts may share that storage directory.
Creation rechecks cleanliness and requires the source branch and HEAD
to still match the pinned baseline. It verifies the resulting branch,
commit, cleanliness and common directory. Existing attempt paths remain
available for inspection. The service exposes no reset, removal, merge or
arbitrary Git command operation.

`ListWorktrees` reports the Git registry including lock and prune status.
`Inspect` independently reports the actual worktree identity, local
branch (if attached), commit and cleanliness. `IsAncestor` and
`ChangedPaths` require complete, verified commit IDs. Changed paths use
Git's NUL-delimited UTF-8 and preserve both rename endpoints for the scope
checker. A later scope gate must validate each endpoint against its task.

Git uses a native executable from the application's absolute PATH
entries, fixed commands and literal argument arrays. Calls suppress
hooks, external diff and text conversion, user and system Git settings,
ambient GIT variables, replacement objects, lazy fetch and transport.
Local config includes, external filter drivers and the worktree redirect
setting fail closed. Tracked symlinks, submodules, active sparse checkouts
and hidden index entries fail cleanliness checks. Calls time out after
60 seconds and cap retained standard output at 32 MiB. Git diagnostics
expose the failing operation and exit code.

The repository and storage parents must be controlled by the trusted
application user. Filesystem checks do not sandbox concurrent writers
with access to the same paths. The private Git runner executes the fixed
operations above. It is separate from P02A's supervisor for untrusted
worker and verification commands. Neither checkout nor process
supervision grants sandbox enforcement.

## Verification

The initial tests failed to compile with CS0246 because the repository
service was absent. Regression tests also reproduced the inability to
share an external storage parent and creation from a moved source HEAD.

`dotnet test tests/Pda.Repositories.Tests -c Release --no-restore --nologo`
ran 12 real
temporary-repository tests on Windows with .NET 10 and Git 2.56.1.
They cover space and Unicode paths, refusal after source HEAD advances,
linked worktree identity, sibling attempts, dirty source
refusal, preserving dirty attempts, unsafe refs, junctions, hook
suppression, external filter refusal, ambient Git settings, full
commit ancestry and both paths of a rename. No Linux test run was made.
