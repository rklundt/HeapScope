# HeapScope Sprints

Phases: **0.x** architecture, framework and skeleton; **1.x** quick wins; **2.x** heavy hitters; **3.x** depth to
match dotMemory (planned 2026-10-28); **Phase 4 candidates** what Phase 3 leaves out; the Backlog ([BACKLOG.md](BACKLOG.md)) everything else. Closed sprints are
in [sprints/](sprints/): [Phase 0](sprints/phase-0.md), [Phase 1](sprints/phase-1.md),
[Phase 2](sprints/phase-2.md), [Phase 3, the peer review sprints and 3.9](sprints/phase-3.md).

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
| 3.9 | Packaging and distribution | Closed | 2026-11-01 | sprint-3.9 |
| 3.10 | Visual design and README | Closed | 2026-11-05 | sprint-3.10 |
| 3.11 | Screens that explain themselves | In Progress | | |
| 3.12 | First-show performance and refactor analysis | Planned | | |

---

## Sprint 3.9: Packaging and distribution

Closed 2026-11-01; its section is in [sprints/phase-3.md](sprints/phase-3.md) and its review in
[reviews/sprint-3.9.md](reviews/sprint-3.9.md). ADR-039 records the distribution decisions.

---

## Sprint 3.10: Visual design and README

Closed 2026-11-05; its section is in [sprints/phase-3.md](sprints/phase-3.md) and its review in
[reviews/sprint-3.10.md](reviews/sprint-3.10.md). ADR-040 records the visual design.

---

## Sprint 3.11: Screens that explain themselves

**Goal**: every screen says what it does and how to use it, so a developer new to memory profilers can find their way, and the two
defects the owner saw in 3.10's testing are gone.

**Background**: the product owner tested 3.10 before its merge (2026-11-05) and could not tell what several screens do or how to use
them. The app has about 104 tooltips, found only by hovering over each control, and most describe a control, not what its page is for.

**Acceptance criteria**:
- Every page and tab has an info icon saying what it is for, when to use it and how to start.
- Settings and features whose purpose is not obvious have an info icon too, including beside a control that is disabled.
- Each opens by hover, click or keyboard, and reads well in both themes.
- Every text was approved by the owner and checked against the code: no claim the code does not back.
- After a refused reopen, opening the dump again refreshes the Snapshots page: the row and its notes are current (3.10 review M40).
- The Retainers chart's narrowest bars are grouped into one bar that says how many it holds and their size (M44).
- A read of the key file while another process moves it into place no longer fails.

**Decisions** (product owner, 2026-11-05):
1. Split from the old 3.11: this sprint goes first; first-show performance, the refactor analysis and the older cache fallback move to 3.12.
2. Every page is covered, starting with Retainers, Live, Inspections and Compare.
3. An info icon beside each page title, and beside each setting or feature that is not obvious. Hover, click or keyboard opens it, so it
   works beside a disabled button.
4. The owner approves the wording first, from a table of every text with the code that backs it (`docs/design/info-text.md`).
5. Names stay for now (Retainers, Why alive, Focus): the icons explain them, and the owner decides on a list of those that still do not work.
6. Out: a guided tour or first-run walkthrough, the slower first load, and the flaky tests other than the key file's.
7. Existing tooltips that the drafting found wrong or incomplete are fixed in this sprint.
8. The names stay. The Retainers tab gets the subtitle "What is holding or retaining memory".
9. Settings get one icon per group, not one per field (option B, 2026-11-06): its popup has a line per control, and a single control keeps
   a tooltip, shown even when it is disabled. The owner approved Tables 2 and 3 on 2026-11-08 with the recommended answers: a group's
   fields lose their tooltips except two format hints and the filters, and the Retainers chart caption shrinks to one line.

**Task breakdown**:
1. [x] The wording table: every page, tab, setting and feature, its draft text and the code that backs it. Proof: the owner's mark-up.
   Table 1 (pages) approved 2026-11-05; Tables 2 (20 group icons) and 3 (43 tooltips) approved 2026-11-08.
2. [x] The info icon: one control (`InfoIcon`), one source for its texts, ADR-040 amended. Proof: UI tests that hover, click and keyboard open
   it and Escape closes it, its accessible name, its contrast in both themes (`ThemeTests`), and that it opens beside a disabled button.
3. [x] Page info on every page and tab. Proof: a test that walks the pages and finds each one's icon and text; no binding errors; captures.
4. [x] Setting and feature info, as the approved table lists them; tooltips that now repeat it go. Proof: a test that every approved row
   is on its view; captures.
5. [x] The Snapshots page refresh (M40). Proof: view model tests where a reopen is refused and the dump opened again: the same handle
   shows again, the row and its notes are current, and a query runs.
6. [x] Sliver grouping (M44): in the chart's geometry, so the worker's answer and protocol stay as they are. Proof: geometry tests (widths
   still sum, a group's count and size); a UI test of its hover text; what a click on a group does is settled by a test.
7. [x] The key file's read (Backlog). Proof: a test that holds the file against a read, then lets it go.
8. [ ] Docs: `docs/USER-GUIDE.md`, the code map, README pictures if the pages change, and the owner's list of names. Proof: the Markdown sweep.

**Review summary**: to be filled by /wrap-sprint.

## Sprint 3.12: First-show performance and refactor analysis

**Goal**: know why pages hitch the first time they open, and what in the code is worth refactoring, before Phase 4 adds to both.

**Background**: the product owner asked for this on 2026-11-05, from the 3.10 review. M16: cold first shows over 100 ms went from 3 to 7 per
run to 5 to 13, exempt from the gate, with the worst dispatcher lag 184 of 200 ms and the cause only inferred.

**Scope, to settle at /start-sprint**:
- Trace a cold first show (`--smoke-trace`, with JIT events), attribute the blips, and fix what the trace supports. Consider a ceiling
  on logged first shows, and a run on a Medium dump, not only the demo dumps.
- A refactor analysis of the codebase: what to change, why and at what cost, as a ranked list the owner decides on. It proposes; any
  refactor it leads to is the owner's call. It includes `PlatformHints` (3.10 review M36).
- To consider pulling in: the Backlog's slower first load (the object pass), and the tests that fail only under full load.
- The older cache fallback, moved from 3.10 by the product owner (2026-11-05; 3.10 review M14, M29 to M31, M37). After a loader update,
  a dump whose DAC is gone or not trusted cannot rebuild its cache, so it does not open. Open the older cache instead, with a note. It must
  cover every way a rebuild can lack the DAC (a symbol server failure, an untrusted DAC after a "no"), set the oldest loader version it accepts,
  free the first read's memory before the second, and say which views are refused without a DAC. Its first version caused most of the
  findings on 3.10's Moderate fixes, so review it as a feature.

**Tasks**: to be filled by /start-sprint.

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
