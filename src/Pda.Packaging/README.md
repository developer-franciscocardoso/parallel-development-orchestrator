# Package installation

`PackageInstaller.Install(zipPath, expectedSha256, installRoot)` opens one
read-only ZIP handle, checks its independently supplied SHA-256 and verifies
each extracted file against its manifest. The root must be local and outside
Git checkouts and orchestrator state. No network requests are made.

The release ZIP uses ordinary single-disk ZIP records. The root
`manifest.json` contains format version 1, a release version, the entry
point `pda.exe`, and SHA-256 hashes for every payload file. Each payload name
inside the ZIP starts with `app/`, followed by its exact manifest path.
The installer bounds the archive to 1 GiB, expanded contents to 2 GiB,
individual files to 512 MiB and the manifest to 8,192 files.

Versions remain under `versions/<version>/`. The installer stores the
verified manifest alongside each version. `current.json` records the active
and previous releases and the original archive and manifest digests. A
cross-process lock serializes version switches. A failed validation leaves
the prior active pointer in place. `Status` rechecks the active payload.
`Rollback` rechecks the previous payload and switches the pointer atomically.

The external SHA-256 establishes package identity. Obtain it through a
trusted channel. Authentication of the publisher depends on that channel.
