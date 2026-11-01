# ADR-039 Distribution (added in sprint 3.9)

- Context: since 3.7, `release.yml` builds every runtime identifier on a published release, self-contained (ADR-025),
  with SBOMs, checksums and attestations; 3.8.0 was the first release with builds attached. Sprint 3.9 decides who
  installs HeapScope, from where and in which forms (product owner, 2026-10-31). HeapScope is open source with no
  budget for certificates or store accounts, and CI has no macOS runners (2026-10-31).
- Decision:
  - Audience: developers and support engineers on Windows and Linux, x64. Channel: GitHub Releases only.
  - Each release attaches a self-contained build of the app and the CLI (each with its worker) for win-x64 as a `.zip`
    (Explorer opens it) and for linux-x64 as a `.tar.gz` (it keeps the executables' modes). No .NET install is needed.
  - The CLI is also a `dotnet tool`: one framework-dependent package for every system (`HeapScope.Cli`, command
    `heapscope-cli`, the .NET 10 runtime), attached to the release and installed from the downloaded file. Capture
    stays Windows only; the tool is untested on macOS. A tool carries no worker executable, so the front end starts
    the worker's assembly through the `dotnet` host that runs it.
  - Every file attached is listed in `SHA256SUMS` and attested; the shipped license files and the attribution strip
    (ADR-022) are unchanged in every form.
  - `INSTALL.md` takes a user from download to a first snapshot on each system.
- Refused or deferred:
  - macOS builds are not attached: they are built on Linux and never run (Backlog "Testing on macOS"); signing and
    notarizing need a paid Apple account.
  - Code signing on Windows: its yearly cost does not fit an open source project; `INSTALL.md` explains SmartScreen's
    warning for an unsigned download.
  - No update check in the app: the releases page lists the versions.
  - Backlog: nuget.org (needs an account; then the tool installs by name), winget, rpm, deb and AppImage, a single-file
    Windows build, a Linux `install.sh` with a desktop menu entry, smaller builds.
- Consequences: a user verifies a download by its checksum and attestation, not a signature. The worker has two ways
  to start (its executable in a self-contained build, its assembly through `dotnet` in the tool), both covered by CI.
  Adding a channel later (nuget.org, winget) repackages these files and keeps the license files and checks.
