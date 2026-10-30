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
| 2.8 | Packaging and distribution | Planned | | |
| 3.1 | Object sets | Closed | 2026-10-28 | sprint-3.1 |
| 3.2 | Retention views | Closed | 2026-10-28 | sprint-3.2 |
| 3.3 | Comparison depth | Closed | 2026-10-29 | sprint-3.3 |
| 3.4 | Live workflow and housekeeping | Closed | 2026-10-29 | sprint-3.4 |
| 3.5 | Leak detection depth | Closed | 2026-10-29 | sprint-3.5 |
| 3.6 | Urgent fixes | Closed | 2026-10-29 | sprint-3.6 |
| 3.7 | Hardening | Closed | 2026-10-30 | sprint-3.7 |
| 3.8 | Modularity | In Progress | | |

---

## Sprint 2.8: Packaging and distribution

**Goal**: people install and run HeapScope without building it.

**Scope**: decided at /start-sprint once the product owner picks the audiences and channels (2026-10-26: "good with
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

## Sprint 3.8: Modularity

**Goal**: each feature area has its own contract, handler and services, so Phase 4 adds modules instead of growing god
classes.

**Scope** (peer review P5 to P12, P18 to P21, S5 to S9):
- Live sources (P6): a spike and ADR-037 deciding where live allocation data runs; ADR-023 amended.
- Worker contracts by area (P5, S6) with a feature availability list on the snapshot descriptor (P7), in one protocol
  bump; worker handlers per contract; one query helper and one reopen protocol in Application; per-area test fakes.
- Front ends: one query runner for every view model load (P8); safe event raising (P9); the CLI's context, test project,
  columns by name and commands out of `CliCommands.cs` (P10); shared presentation in Application (P18).
- Monitoring split (P11): timeline writer, allocation recording, run record; LiveViewModel split.
- Analysis: detectors one per file with one framework classifier (P19); derived state in the worker's slots (P20); one
  type matcher (S5).
- UI seam: an analysis workspace per snapshot (P21).
- From the 3.7 review: an injected index cache signer with an explicit "none" for tools and tests, replacing the ambient
  key and its `AsyncLocal` seam (M11); one `HeapScopeFolders` in Core with a worker option to redirect it, so tests keep
  the key and trust files out of the profile and a worker-level signature test becomes possible (L, M10); the run file
  policy (`FileInside` with the check that refused, a status per cause) moved from `LiveViewModel` into Application with
  the P11 split (M2).
- Docs (P12): an overview ARCHITECTURE.md with module pages and one ADR per file; SPRINTS.md cut to the current phase and
  a swept Backlog; the comment and file-per-type rules (S8, S9); trace-report on Avalonia (S7).

**Acceptance criteria** (set at /start-sprint, 2026-10-30):
- Behavior preserving: the CLI's JSON and text for the LeakyConsole dump (summary, diff, instances, referrers, paths,
  retainers, set, inspect, trend, open) equal a baseline taken before the sprint, except the changes listed under
  Decisions; known answers and smoke runs pass.
- The worker serves one contract per area (control, test hooks, snapshots, result sets, heap queries, retention,
  inspections, allocation traces), each by its own handler with shared services injected; no class implements them all.
  Application has one query helper and one reopen protocol for snapshots and traces; test fakes are per area.
- The snapshot descriptor lists each optional feature as available or unavailable with the reason, the worker refuses
  with that same reason, and the front ends offer only what the list allows; one protocol bump (21), which also removes
  the obsolete type summary call.
- An event handler that throws never changes a job's outcome, fails an open, or escapes a worker host call; events are
  raised after state is final and outside locks.
- Per-snapshot derived values (reachability, dominator groups, the set cache) live with the snapshot in the worker and go
  when it closes; Analysis keeps no static caches.
- One `HeapScopeFolders` names every HeapScope data folder, and a worker option redirects it; the index cache signer is
  injected, with "none" written out where it is meant.
- Every view model query goes through one runner that logs a refusal under the query's trace id.
- The CLI has a test project, a context instead of a static, reads cells by column name, and keeps only its command
  table in `CliCommands.cs`; texts and monitor rules shared with the app live in Application.
- The monitoring run's timeline, allocation recording and run record are their own types, the trace watch is awaited,
  and the past run file checks say which check refused; the Live page is split by section.
- One framework type rule, one tally, a runtime layouts table, a file per detector, one type matcher.
- ADR-037 decides where live sources run, from a measured spike; ADR-023 amended.
- ARCHITECTURE.md is an overview with a page per topic and one file per ADR; SPRINTS.md keeps the current phase, closed
  sprints and the Backlog move out, and the Backlog is swept; every link in tracked Markdown resolves (a test).

**Decisions** (all decided autonomously, 2026-10-30, peer review sprint autonomy):
1. The contract split keeps the wire: StreamJsonRpc serves several targets on one connection and attaches one proxy
   over several interfaces, and JSON-RPC carries only method names. Protocol 21 comes from the availability list and the
   removed call alone; method names stay unique across contracts (a test).
2. Feature ids are strings in Contracts, so a new feature is not a protocol change; a missing id is unavailable.
3. Texts: the CLI's retained-size wording becomes the shared one; the CLI summary header for a gcdump stops claiming
   unreachable objects (a fix); a past run's file refusal says its cause.
4. The app's monitor form keeps bare minutes; the shared parser takes the grammar as a parameter.
5. One framework type rule, including `Internal.` names; retention paths fold those steps too.
6. A module given as a path matches in instances, referrers and paths as it does in sets (a fix).
7. P21 is an extraction only: one analysis workspace pointed at the selected snapshot. Workspaces per snapshot need a
   design for the worker's result sets of hidden snapshots and a measure of rebinding cost (Phase 4).
8. S8 and S9 as rules in DEVELOPER.md, a ratchet test for public types per file, and both applied to files 3.8 touches;
   not a sweep of the whole code base.
9. ARCHITECTURE.md keeps its section numbers as an overview, so the code's "ARCHITECTURE.md N" references stay valid;
   topic pages under `docs/architecture/`, ADRs under `docs/adr/`, closed sprints under `docs/sprints/`, the Backlog in
   `docs/BACKLOG.md`.
10. P6 is a spike and ADR-037 only; building live allocation streaming is Phase 4.
11. Decided during the sprint: the Live page split (P11's view model half) and the analysis workspace extraction (P21)
    move to the Backlog. Both rebind views the smoke runs exercise, and the workspace is only worth extracting with
    Phase 4's design for the worker's result sets of hidden snapshots (decision 7). P11's run-side problems (eight jobs
    in one type, an unawaited trace watch) are fixed in 3.8.
12. Decided during the sprint: the query runner covers the eleven views the review names (the nine grids through the
    grid base, the retention paths and Similar Retention); the Object view, the inspections page, the allocation trace
    page and the icicle keep their own loading, which already logs, and move to the runner when next touched.
13. Decided during the sprint: worker handlers (task 9), feature availability (task 12), the CLI and shared presentation
    (tasks 16 and 17) and derived state (task 14) were built by agents working from written briefs, in worktrees where
    they ran in parallel; each change was reviewed against the CLI baseline and the full test suite before it joined the
    branch.

**Dependencies**: 3.6, 3.7.

**Task breakdown**:
1. [x] Baseline: the CLI outputs of the acceptance criteria saved before any change; a contract test for unique method
   names. Tests: the baseline diff after tasks 9, 12, 14 and at the end.
2. [x] Docs split (P12): ARCHITECTURE.md overview, topic pages, ADR files, the protocol history in one place; links in
   README, SECURITY, DEVELOPER, the agents and skills. Tests: `DocLinksTests`.
3. [x] SPRINTS.md split and Backlog sweep (P12): closed sprints by phase, `docs/BACKLOG.md` without built items; the
   skills move closed sections. Tests: `DocLinksTests`.
4. [x] Rules (S8, S9): DEVELOPER.md 4; `PublicTypesPerFileTests` ratchet.
5. [x] Safe events (P9): one raise helper; `JobTracker`, `SnapshotSession`, `AllocationSession`, `WorkerHost` raise after
   state is final and outside gates. Tests: throwing-subscriber tests per raiser.
6. [x] `HeapScopeFolders` and the worker's data folder option (3.7 review). Tests: folder tests, option parsing, a text
   rule, a worker that keeps its key in its data folder.
7. [x] Injected index cache signer (3.7 review M11). Tests: `SignedCacheTests`, a rule against "none" in the worker, a
   worker-level untrusted cache test.
8. [x] Contracts by area (P5, S6). Tests: the contract test.
9. [x] Worker handlers per contract with shared services (P5). Tests: registration, in-process RPC, the integration tier,
   the baseline.
10. [x] Application: one query helper, one reopen protocol (P5). Tests: `WorkerResourceTests`, reopen tests.
11. [x] Per-area fakes (P5). Tests: view model and UI tests.
12. [x] Feature availability and protocol 21 (P7, S6). Tests: availability per kind, refusals with the same reason, an
    incomplete dump offers no unreachable set, handshake, the baseline.
13. [x] Detectors (P19): a file per detector, one framework rule, a shared tally, runtime layouts. Tests: detector and
    known answer tests, `FrameworkTypesTests`, `TypeTallyTests`.
14. [x] Derived state (P20): reachability and dominator groups in the worker's derived slots, the set cache with the
    snapshot. Tests: computed once for concurrent callers, no static caches, a set resolved after close is not kept.
15. [x] One type matcher (S5). Tests: a module path matches.
16. [x] Shared presentation (P18): column access, texts, monitor rules parser in Application. Tests: unit tests per text
    and parser, view model tests.
17. [x] CLI (P10): `HeapScope.Cli.Tests`, a context, cells by name, one open helper, commands in their own files. Tests:
    usage and characterization tests, the integration CLI tests, known answers.
18. [x] Monitoring (P11): timeline writer, awaited allocation recording, run record; `MonitorService.cs` a file per type.
    Tests: `MonitorServiceTests`, a run disposed while its watch waits.
19. [x] Run file policy (3.7 review M2): checks with their cause in Application; the Live page shows and logs it. Tests:
    `SessionLibraryTests`, a Live test with a timeline outside the folder.
20. [-] Live page split (P11): deferred (decision 11). Tests: none.
21. [x] One query runner (P8). Tests: `QueryRunnerTests`, the grid view model tests.
22. [-] Analysis workspace extraction (P21): deferred (decision 11). Tests: none.
23. [x] trace-report on Avalonia (S7). Tests: a recorded smoke trace reported by hand.
24. [x] Live sources spike and ADR-037 (P6); ADR-023 amended.
25. [x] Docs: the architecture pages, ADR-036 amended, DEVELOPER, this section.

**Review summary**: to be filled by /wrap-sprint.

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
