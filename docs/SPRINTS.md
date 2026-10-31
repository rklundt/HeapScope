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
| 2.6 | Smart leak detectors | Closed | 2026-10-26 | sprint-2.6 |
| 2.7 | Navigation and polish | Closed | 2026-10-27 | sprint-2.7 |
| 3.1 | Object sets | Closed | 2026-10-28 | sprint-3.1 |
| 3.2 | Retention views | Closed | 2026-10-28 | sprint-3.2 |
| 3.3 | Comparison depth | Closed | 2026-10-29 | sprint-3.3 |
| 3.4 | Live workflow and housekeeping | Closed | 2026-10-29 | sprint-3.4 |
| 3.5 | Leak detection depth | Closed | 2026-10-29 | sprint-3.5 |
| 3.6 | Urgent fixes | Closed | 2026-10-29 | sprint-3.6 |
| 3.7 | Hardening | Closed | 2026-10-30 | sprint-3.7 |
| 3.8 | Modularity | Closed | 2026-10-30 | sprint-3.8 |
| 3.9 | Packaging and distribution | Planned | | |

---

## Sprint 3.9: Packaging and distribution

Planned in Phase 2 as sprint 2.8 and renumbered 3.9 on 2026-10-30 (product owner), so that its version, 3.9.0,
follows 3.8.0.

**Goal**: people install and run HeapScope without building it.

**In scope already** (product owner, 2026-10-30):
- A README in the shipped `licenses/` folder: the files there are the licenses of third-party components, and
  HeapScope's own license is `LICENSE` beside it (so no one reads HeapScope as MIT).
- The 3.9.0 release notes say that builds no longer contain Adobe's DNG SDK or Google's piex (ADR-038, 2026-10-31).

**Scope**: otherwise decided at /start-sprint once the product owner picks the audiences and channels (2026-10-26: "good with
your proposal for a packaging sprint once you pick audiences and channels"). Options discussed after 2.4:
- Windows: a self-contained single-file publish (the app and the worker, which is its own process), a
  framework-dependent zip on a shared runtime, winget, MSIX, code signing.
- Linux: a self-contained tar.gz with a `.sh` launcher; AppImage, deb or rpm.
- macOS: an app bundle in a DMG, signed and notarized (needs an Apple developer account).
- The CLI as a `dotnet tool`.
- Release builds from CI on a tag, with checksums; releases to `main` stay the product owner's call (DEVELOPER.md 7).

Constraints: every bundled component keeps an allowed license (MIT or allowed), with its notices shipped; the
attribution strip and the license files ship unchanged (ADR-022); before any Avalonia update, check whether anything
used has moved to a paid model. Signing certificates, store accounts and paid services need the product owner's OK.
Builds ship HeapScope's own SkiaSharp native library, without Adobe's DNG SDK (ADR-038, 2026-10-30); a channel that
repackages the build keeps it, and a SkiaSharp update rebuilds it (DEVELOPER.md 7).

**Open questions for /start-sprint**: audiences (who installs it, on which systems); channels (GitHub Releases only,
or also winget, a dotnet tool, package feeds); self-contained or shared runtime; signing on which systems; how updates
reach users; whether CI becomes the release builder.

**Acceptance criteria**: to be set at /start-sprint with the channels; at least a clean machine of each chosen
system installs the package, runs the app, opens a fixture dump and runs the CLI, with no SDK installed.

**Dependencies**: 2.3.6 (the Avalonia app, ADR-025). Can run before 2.5 if the product owner wants packages earlier.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Peer review sprints

Planned 2026-10-29. After the v3.5.0 release the product owner asked for an outside peer review of the whole codebase
from a tech trio (solution architect, senior developer, DevSecOps): "what do we need to change, refactor, modularize".
The findings are in `docs/reviews/peer-review-3.5.md` (P1 to P21, S1 to S9). The product owner split the work into
three sprints, run before Phase 4 ("write the three sprints and save the review - then execute on the sprints"); they
run autonomously like Phase 3, one sprint at a time.

---

## Phase 4 candidates

Not planned; picked from when Phase 3 closes (product owner, 2026-10-28: what Phase 3 does not address).
- A profiler-style live session: allocation data streamed beside the timeline, and snapshots faster than a full dump.
- What was allocated between two snapshots in Compare, from a trace recorded between them (from 2.5; needs traces in
  `session.json` on the snapshots' clock).
- A sunburst view beside the icicle chart for dominators and call trees.
- Analysis tabs: several analyses of a snapshot side by side (from 2.7).
- Capture on Linux and macOS (from 2.3.5, ADR-025).
- WPF leak detectors with a WPF fixture app (from 2.6).
