# HeapScope Sprints

Phases: **0.x** architecture, framework and skeleton; **1.x** quick wins; **2.x** heavy hitters; **3.x** depth to
match dotMemory (planned 2026-10-28); **Phase 4 candidates** what Phase 3 leaves out; the Backlog ([BACKLOG.md](BACKLOG.md)) everything else. Closed sprints are
in [sprints/](sprints/): [Phase 0](sprints/phase-0.md), [Phase 1](sprints/phase-1.md),
[Phase 2](sprints/phase-2.md), [Phase 3 and the peer review sprints](sprints/phase-3.md).

## Status

| Sprint | Title | Status | Closed | Tag |
|---|---|---|---|---|
| 0.0 | Project charter, architecture and process | Closed | 2026-10-22 | sprint-0.0 |
| 0.1 | Solution skeleton | Closed | 2026-10-22 | sprint-0.1 |
| 0.2 | Snapshot abstraction and compact graph | Closed | 2026-10-22 | sprint-0.2 |
| 0.3 | Worker process and IPC | Closed | 2026-10-22 | sprint-0.3 |
| 0.4 | Fixtures and test harness | Closed | 2026-10-22 | sprint-0.4 |
| 0.5 | Index cache format | Closed | 2026-10-22 | sprint-0.5 |
| 1.2 | Full dump loader | Closed | 2026-10-23 | sprint-1.2 |
| 1.1 | Process discovery and capture | Closed | 2026-10-23 | sprint-1.1 |
| 1.3 | Heap summary by type | Closed | 2026-10-23 | sprint-1.3 |
| 1.4 | Snapshot diff | Closed | 2026-10-23 | sprint-1.4 |
| 1.5 | Object search and inspection | Closed | 2026-10-23 | sprint-1.5 |
| 1.6 | Simple inspections | Closed | 2026-10-23 | sprint-1.6 |
| 1.7 | Live counters and timed snapshots | Closed | 2026-10-23 | sprint-1.7 |
| 1.8 | gcdump support | Closed | 2026-10-24 | sprint-1.8 |
| 1.9 | Phase 1 hardening | Closed | 2026-10-24 | sprint-1.9 |
| 2.1 | Reverse references | Closed | 2026-10-24 | sprint-2.1 |
| 2.2 | Retention paths | Closed | 2026-10-24 | sprint-2.2 |
| 2.3 | Dominator tree and retained size | Closed | 2026-10-25 | sprint-2.3 |
| 2.3.5 | Cross-platform and sharing decision | Closed | 2026-10-25 | sprint-2.3.5 |
| 2.3.6 | Avalonia port | Closed | 2026-10-26 | sprint-2.3.6 |
| 2.3.7 | Port peer review | Closed | 2026-10-26 | sprint-2.3.7 |
| 2.4 | Retention explorer UI | Closed | 2026-10-26 | sprint-2.4 |
| 2.5 | Allocation tracking | Closed | 2026-10-26 | sprint-2.5 |
| 2.6 | Smart leak detectors | Closed | 2026-10-27 | sprint-2.6 |
| 2.7 | Navigation and polish | Closed | 2026-10-27 | sprint-2.7 |
| 3.1 | Object sets | Closed | 2026-10-28 | sprint-3.1 |
| 3.2 | Retention views | Closed | 2026-10-28 | sprint-3.2 |
| 3.3 | Comparison depth | Closed | 2026-10-29 | sprint-3.3 |
| 3.4 | Live workflow and housekeeping | Closed | 2026-10-29 | sprint-3.4 |
| 3.5 | Leak detection depth | Closed | 2026-10-29 | sprint-3.5 |
| 3.6 | Urgent fixes | Closed | 2026-10-29 | sprint-3.6 |
| 3.7 | Hardening | Closed | 2026-10-30 | sprint-3.7 |
| 3.8 | Modularity | Closed | 2026-10-30 | sprint-3.8 |
| 3.9 | Packaging and distribution | In Progress | | |

---

## Sprint 3.9: Packaging and distribution

Planned in Phase 2 as sprint 2.8 and renumbered 3.9 on 2026-10-30 (product owner), so that its version, 3.9.0,
follows 3.8.0.

**Goal**: people install and run HeapScope without building it.

**In scope already** (product owner, 2026-10-30):
- A README in the shipped `licenses/` folder: the files there are the licenses of third-party components, and
  HeapScope's own license is `LICENSE` beside it (so no one reads HeapScope as MIT).
- Done 2026-10-31: the 3.8.0 release notes say that builds no longer contain Adobe's DNG SDK or Google's piex
  (ADR-038); 3.8.0 was released first.

**Scope**: otherwise decided at /start-sprint once the product owner picks the audiences and channels (2026-10-26: "good with
your proposal for a packaging sprint once you pick audiences and channels"). Options discussed after 2.4:
- Windows: a self-contained single-file publish (the app and the worker, which is its own process), a
  framework-dependent zip on a shared runtime, winget, MSIX, code signing.
- Linux: a self-contained tar.gz with a `.sh` launcher; AppImage, deb or rpm.
- macOS: an app bundle in a DMG, signed and notarized (needs an Apple developer account).
- The CLI as a `dotnet tool`.
- Release builds: `release.yml` (3.7) already builds every runtime identifier on a published release, with checksums
  and attestations; releases to `main` stay the product owner's call (DEVELOPER.md 7).

Constraints: every bundled component keeps an allowed license (DEVELOPER.md 4), with its notices shipped; the
attribution strip and the license files ship unchanged (ADR-022); before any Avalonia update, check whether anything
used has moved to a paid model. Signing certificates, store accounts and paid services need the product owner's OK.
Builds ship HeapScope's own SkiaSharp native library, without Adobe's DNG SDK (ADR-038, 2026-10-30); a channel that
repackages the build keeps it, and a SkiaSharp update rebuilds it (DEVELOPER.md 7).

**Open questions for /start-sprint**: audiences (who installs it, on which systems); channels (GitHub Releases only,
or also winget, a dotnet tool, package feeds); self-contained or shared runtime; signing on which systems; how updates
reach users.

**Acceptance criteria** (set at /start-sprint, 2026-10-31):
- On a Linux machine with no .NET (a plain Ubuntu container in CI) and a Windows machine with .NET hidden (a CI runner
  with it off the path), the release archive unpacks, the app runs its headless smoke, and the CLI pings its worker and
  answers a summary of a fixture dump.
- The CLI's tool package installs from the downloaded file with `dotnet tool install` on Windows and Linux, and the
  installed `heapscope-cli` pings its worker and answers the same summary.
- A release attaches a `.zip` for win-x64, a `.tar.gz` for linux-x64, the tool package, the SBOMs and `SHA256SUMS`, each
  attested, and no macOS build; the shipped `licenses/` folder has its README.
- `INSTALL.md` takes a new user from download to a first snapshot on each system, linked from the README and the
  release notes.

**Dependencies**: 2.3.6 (the Avalonia app, ADR-025).

**Decisions** (product owner, 2026-10-31):
1. Audience and channel: people on Windows and Linux (x64) download a release from GitHub Releases; the builds are
   self-contained, so no .NET install is needed.
2. Windows gets a `.zip` (Explorer opens it), Linux a `.tar.gz` (keeps the executables' modes).
3. The CLI is also a `dotnet tool`: one framework-dependent package for every system, ID `HeapScope.Cli`, command
   `heapscope-cli`; capture stays Windows only and macOS is untested. Inside the tool the worker runs through the
   `dotnet` host (`HeapScope.Worker.dll`), since a tool carries no worker executable.
4. The tool package is attached to each GitHub Release and installed from the downloaded file. Publishing to nuget.org
   goes to the Backlog (no account yet).
5. macOS is not shipped: `release.yml` stops attaching macOS builds; CI still builds them on Linux so they keep building.
   The 3.8.0 macOS files stay. (Standard runners are free in public repositories; the owner checks the billing report
   before macOS testing comes back, Backlog "Testing on macOS".)
6. No code signing (its yearly cost does not fit an open source project); `INSTALL.md` explains SmartScreen's warning.
7. Backlog: winget, rpm, deb and AppImage, a single-file Windows build, a Linux `install.sh` with a desktop menu entry,
   smaller builds (about 356 MB unpacked for win-x64; the worker carries its own runtime).
8. No update check in the app: GitHub Releases lists the versions.
9. The clean-machine proof runs in CI (a plain Ubuntu container; a Windows runner with .NET off the path); the owner may
   check once more in Windows Sandbox.

**Task breakdown**:
1. [x] ADR-039, distribution: the channels, formats and refusals above. Proof: the ADR index and ARCHITECTURE.md link it.
2. [x] The shipped `licenses/` folder has a README saying its files are third-party licenses and HeapScope's own is
   `LICENSE` beside it. Proof: `build/check-package.sh` fails without it.
3. [x] The worker found inside a tool: when `worker/` holds `HeapScope.Worker.dll` and no worker executable, the front
   end starts it through the `dotnet` host that runs the front end; with neither, the error names the folder. Proof:
   unit tests of the worker path choice (executable, dll through the host, neither).
4. [ ] The tool package: `HeapScope.Cli` packs as a tool with the worker (no executable), HeapScope's license files and
   the third-party licenses, built by `build/pack-tool.sh`. Proof: CI packs it, installs it from the file into a tool
   folder on Windows and Linux, and runs `--version`, `worker ping` and a summary of a fixture dump.
5. [ ] Release archives: `release.yml` builds win-x64 and linux-x64 only, zips the Windows build, and attaches the tool
   package with the others (checksummed and attested). Proof: a dry run of `release.yml` and the CI package job's
   unpack-and-run of the same archive.
6. [ ] Clean-machine runs: the Linux build in a plain Ubuntu container (no .NET; the system libraries it needs found and
   listed) and the Windows build with .NET off the path, each opening a fixture dump with the CLI and running the app's
   headless smoke. Proof: the CI jobs.
7. [ ] `INSTALL.md`: download, checksum and attestation checks, Windows (unblocking, SmartScreen), Linux (libraries,
   `~/.local/bin` links), the tool, the data folder, uninstalling. Linked from the README and the release notes. Proof:
   the Markdown sweep and the clean-machine steps match it.
8. [ ] Backlog and docs: decision 4 and 7's items in the Backlog; DEVELOPER.md (release section), ARCHITECTURE.md (code
   map) and README describe what was built.

**Review summary**: to be filled by /wrap-sprint.

---

## Peer review sprints

3.6, 3.7 and 3.8 worked through `docs/reviews/peer-review-3.5.md`; all closed ([sprints/phase-3.md](sprints/phase-3.md)).

---

## Phase 4 candidates

Not planned; picked from when Phase 3 closes (product owner, 2026-10-28: what Phase 3 does not address).
- A profiler-style live session: allocation data streamed beside the timeline, and snapshots faster than a full dump.
- What was allocated between two snapshots in Compare, from a trace recorded between them (from 2.5; needs traces in
  `session.json` on the snapshots' clock, and each tick's type id and module to join a diff's rows, ADR-028).
- A sunburst view beside the icicle chart for dominators and call trees.
- Analysis tabs: several analyses of a snapshot side by side, each with its own path (from 2.7; the branching analysis
  path covers coming back to an earlier analysis for now).
- Capture on Linux and macOS (from 2.3.5, ADR-025): process discovery through `/proc` and the diagnostics client, full
  dumps through the runtime's createdump (the diagnostics client's `WriteDump`), gcdumps through the vendored code;
  elevation and ptrace rules per system. Until then capture refuses there and points to `dotnet-dump`.
- WPF leak detectors with a WPF fixture app (from 2.6): binding to a non-INotifyPropertyChanged source, collection
  bindings, dependency property descriptors, x:Name.
