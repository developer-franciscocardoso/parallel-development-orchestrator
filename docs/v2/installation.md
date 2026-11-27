# Install and operate on Windows

The Windows x64 package includes the .NET runtime, the CLI, the dashboard,
SQLite support and the worker report schema. Use PowerShell 7 and Git for
Windows. Live Codex and independent sandboxed checks also require the
compatible, deliberately configured native Codex installation described in
[the adapter guide](phase-03-agents.md).

## Build a release

From a verified source checkout with the .NET 10 SDK, choose an output
fresh directory outside the repository.

```powershell
$release = ./scripts/Publish-Pda.ps1 -Version 2.0.0 -OutputDirectory C:\PdaReleases
$release | Format-List
```

The publisher produces a versioned ZIP, a SHA-256 checksum file and a
standalone PowerShell installer. Record the package and bootstrap hashes
with your trusted release record. A checksum obtained exclusively from the
same untrusted download location cannot authenticate the archive.

## Fresh installation

Obtain the expected hashes from the trusted release record. Authenticate
the bootstrap before opening it. Use private local directories. The
application and runtime state have separate lifecycles.

```powershell
$releaseDir = 'C:\PdaReleases'
$install = Join-Path $env:LOCALAPPDATA 'Pda-Bin'
$env:PDA_HOME = Join-Path $env:LOCALAPPDATA 'Pda'
$bootstrap = Join-Path $releaseDir 'Install-Pda.ps1'
$zip = Join-Path $releaseDir 'pda-2.0.0-win-x64.zip'
$expectedBootstrap = Read-Host 'Trusted bootstrap SHA-256'
$expectedPackage = Read-Host 'Trusted package SHA-256'
if ((Get-FileHash -LiteralPath $bootstrap).Hash -ne $expectedBootstrap) {
    throw 'Unexpected bootstrap hash.'
}
& $bootstrap -PackagePath $zip -PackageSha256 $expectedPackage -InstallDir $install
$pda = Join-Path $install 'pda.ps1'
& $pda init
& $pda doctor --json
```

The bootstrap holds the verified ZIP open during the installation, checks
the helper executable and launcher hashes, and invokes the shared
installer. The installer verifies the entire payload before switching
`current.json`. The stable launcher resolves the current version from that
pointer. Its executable and report schema live under
`versions/<version>/`. The install uses local files and requires no admin
elevation, global PATH changes or paid model call.

Place `PDA_HOME` outside source repositories, the installation root and
temporary folders. Its SQLite database, evidence, and isolated worktrees
belong to the operator. Keep this directory private. A successful
installation does not authenticate or configure a live provider.

## Upgrade and rollback

Back up state using the documented CLI backup command before upgrading a
production deployment. Stop dashboards and active executions cleanly. The
application-version pointer and SQLite schema have separate lifecycles.

```powershell
& $pda update --status --install-dir $install --json
$newZip = Join-Path $releaseDir 'pda-2.0.1-win-x64.zip'
$newHash = Read-Host 'Trusted new package SHA-256'
& $pda update --package $newZip --sha256 $newHash --install-dir $install
& $pda update --status --install-dir $install
& $pda update --rollback --install-dir $install
```

Rollback revalidates and activates the prior packaged binaries. It keeps
state, repositories, worktrees, logs and historical reports. If a future
schema migration is incompatible with older code, use the matching state
backup or migration instructions for that release.

For uninstall, stop workers and close the dashboard. Inspect the dedicated
installation root and the state directory as separate locations. Remove
only the application directory you selected for installation. Preserve
`PDA_HOME`, its backups and any project worktrees you intend to retain.

## Reproduce the release smoke test

Publish two different version numbers to a fresh output directory. Invoke
`tests/release/Invoke-ReleaseSmoke.ps1` with both ZIP paths, both verified
digests, and an existing scratch directory outside source repositories.
It checks initial digest rejection, fresh installation, runtime startup,
project registration, live launcher upgrade, binary rollback and recovery
of the same registered Git baseline. Its unique scratch root is retained
for inspection.

### Observed Windows acceptance

Two separate self-contained Windows x64 publications produced ZIPs of
46,420,206 bytes (2.0.0-smoke.1) and 46,420,521 bytes (2.0.0-smoke.2).
An incorrect digest created no install root. With the correct digest, the
bootstrap installed the first version. The installed launcher initialized
a separate database, registered a real Git project, upgraded to the
second version and rolled back to the first. Its stored project profile
and baseline commit matched before and after rollback.

After rollback, the installed executable served the authenticated project
page, registered project API, CSS and JavaScript from the bundled dashboard.
This acceptance run used isolated local state and no model credentials.
