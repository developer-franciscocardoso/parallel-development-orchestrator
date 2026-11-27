# Phase 04 — discovery and executable planning

ProjectDiscovery.Inspect accepts an absolute Git top-level directory. It
reuses the hardened local Git runner, identifies the common Git directory,
pins HEAD, enumerates its immutable tree and reads selected regular blobs
by object ID. Discovery executes no repository scripts, package managers,
builds or tests. Staged, unstaged and untracked changes do not contribute.
Commit intended input first. Symlink and submodule entries are skipped.

Each profile records the commit, canonical checkout and common directory,
UTC inspection time, evidence paths, Git blob IDs and byte counts. Source
extensions identify languages. Documentation, CI files and instruction files
are classified by path. Instruction text is preserved. AGENTS.md,
AGENTS.override.md, CLAUDE.md, .github/copilot-instructions.md,
.github/instructions/ and .cursor/rules/ are recognized, including inside
otherwise ignored vendor, build or generated directories. Instructions are
untrusted repository data. The planner protects them from modification.

The manifest reader recognizes npm-family package scripts and selected
package names; SDK-style .NET projects and package references; Go module,
Cargo package/workspace, and Python build-system declarations. SQLite and
PostgreSQL detection uses declared package names. A .sql extension records
SQL with an unspecified engine. Files under other ecosystems can still use
explicitly supplied verification. This inventory does not evaluate MSBuild
conditions, resolve package versions or infer semantic dependencies.

Discovery permits up to 20,000 tree entries. It reads at most 256 manifest
or instruction blobs, each at most 256 KiB and 4 MiB combined. Oversized
manifests are reported and skipped. An instruction outside these bounds
stops inspection rather than omitting guidance. JSON depth is bounded;
XML external entities and DTDs are disabled.

## Application contracts

PlanningService takes IStateStore. InspectAndRegister(projectId, repository)
registers the canonical root and appends a versioned project profile before
any plan exists. SQLite schema 3 retains profile versions and records the
selected version and immutable commit for each profiled plan. Plans()
exposes them as ProfileVersion and BaselineCommit. A changed HEAD requires
fresh inspection. Concurrent profile updates fail optimistic version checks.

Generate accepts the project ID, plan ID, objective, optional owned paths,
runtime ID, token budget and optional VerificationCommand array. A single
objective yields one task. By default ownership covers existing top-level
directories and files containing discovered sources, manifests and docs.
More than 32 roots or no recognized input requires explicit owned paths.
A supplied scope can include new files. The default task reserves the plan
budget, has medium risk, two attempts, a 1,800-second timeout and priority
zero. Supply explicit contracts for different resource and risk decisions.

Automatic checks require a recognized package test script (vitest run,
jest, jest --runInBand, mocha, node --test, playwright test) or a .NET test
project declaring both Microsoft.NET.Test.Sdk and a supported framework.
For nested npm manifests the command uses --prefix. Nested pnpm/yarn
checks require explicit input. Checks match manifest scopes; callers must
supply a relevant check when manifest scope alone is insufficient. Script
contents, installed tooling, actual test coverage and successful execution
are verified by later runtime and quality gates. A missing check raises an
actionable error before the plan is saved.

Import accepts explicit task contracts. ImportJson takes a strict UTF-8
size-bounded 1 MiB JSON array, camelCase contract fields and string risk
values low, medium or high. Commands consist of an executable and argument
array. Both paths enforce Core DAG, ownership, resource and path validation.
Instruction/control exclusions are attached to broad ownership scopes.
Automatic planning does not split coupled work or claim that an inferred
boundary is independent. For parallel work, supply explicit task scopes and
dependency edges. All checks still require trusted execution policy approval.

## Validation

Windows acceptance tests create real Git repositories and SQLite files.
They cover mixed manifests, dirty working trees, source hashes and profile
history, malformed JSON, oversized manifests, external XML entities,
vendor exclusions, tracked symlinks, instruction protection, changing HEAD,
unknown languages with explicit checks, stale-profile rejection, many
files under a common scope, structured import, cycle/missing/duplicate
dependencies and path traversal. Run the planning test project with
`dotnet test tests/Pda.Planning.Tests -c Release`.
