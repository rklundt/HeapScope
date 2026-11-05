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
| 3.10 | Visual design and README | Wrapping | | |
| 3.11 | First-show performance and refactor analysis | Planned | | |

---

## Sprint 3.9: Packaging and distribution

Closed 2026-11-01; its section is in [sprints/phase-3.md](sprints/phase-3.md) and its review in
[reviews/sprint-3.9.md](reviews/sprint-3.9.md). ADR-039 records the distribution decisions.

---

## Sprint 3.10: Visual design and README

**Goal**: HeapScope looks modern and consistent in both themes, with its visible defects fixed, and a README written for
people shows it off with real screenshots.

**Background**: the product owner found the look "well organized, but utilitarian" and the Markdown docs written for an AI
audience (2026-11-03). A design review of real captures (30 screenshots, every page in both themes) found stock Fluent at
default settings, an inverted hierarchy and real defects; a mockup of its top changes on the real app set the direction.

**Acceptance criteria**:
- Every page follows one design in both themes: the token values, type scale, control sizes and selection of ADR-040.
- The review's defects are gone: Compare and Allocations show type names at 1440 wide, selected text meets 4.5:1, no
  blank or clipped columns, charts follow a theme change, and the rest of the task list's defects.
- The app has its own icon (logo A) in the window, the executable and the sidebar, and its own Duotone icons; logos B to E
  stay in the repository as options.
- The attribution notice's wording, link, position and size are unchanged; its test still passes with the new colors.
- No checked dispatcher operation over the 100 ms budget, measured by the smoke runs with Inter loaded.
- The README leads with real screenshots that match the reader's GitHub theme and a feature list in the form "Problem?
  Solution", every claim true; nothing is lost: the old detail is in `docs/USER-GUIDE.md`.

**Decisions** (product owner, 2026-11-03 and 2026-11-04):
1. Sprint 3.10 "Visual design and README", version 3.10.0, before Phase 4.
2. Direction: the review's "quiet instrument panel": slate neutrals, one indigo accent, color only for selection, primary
   actions and data. Dense, not airy.
3. Font: Inter (SIL OFL 1.1), with tabular figures on number columns. OFL is allowed for fonts only; it can be changed later.
4. Logo A (a lens over stacked blocks). Logos B to E are kept as options in `docs/design/logo-options/`.
5. Icons: our own, Duotone (outline plus an indigo tint), not a third-party set. If any look weak in real captures, the
   owner sees them before more are drawn.
6. Attribution: the wording stays "Powered by HeapScope by Ray Klundt" (the owner may revisit ADR-022 later); its colors
   follow the theme (contrast 16.6:1 light, 14.0:1 dark, up from 15.8:1 and 13.0:1).
7. "Run synthetic job" moves from Home to About, under Diagnostics.
8. README images are real captures of fixture dumps: the Processes page's made-up rows stay out. They follow the reader's
   GitHub theme (`<picture>`, about 10 files) and are captured at 2x. Real fixture type names are kept. The demo dumps are
   named `demo-before.dmp` and `demo-after.dmp`.
9. `--screenshots` stays in the app beside the other headless smoke modes, driven by hand through `build/screenshots.sh`;
   not in per-PR CI, and no pixel comparison tests.
10. Backlog, not this sprint: inline data bars in grids, dimmed namespaces in type names, a collapsible snapshot rail, and
    the icicle chart colored by application versus framework code.
11. README features read "Feature: Problem? Solution!", a marketing tone with claims that hold. Numbers only where measured;
    no competitor named; limits stated plainly (capture on Windows only, macOS not shipped, builds unsigned).
12. The README's Live picture watches the fixture for real (`--screenshot-live-pid`), not `--smoke-live`'s made-up samples, under
    decision 8's rule that its pictures are real captures. Decided while building it, 2026-11-04.
13. A dump's process name was the first module ClrMD listed, which the demo dump showed as an ASP.NET library. Found by the
    captures and fixed here (loader version 4, so caches are rebuilt). Decided while building it, 2026-11-04.
14. Releases attach the macOS packages after all, with a strong note on the release page that they are built on Linux, not
    on a Mac, and untested (amends decision 11's "macOS not shipped" and ADR-039). README and INSTALL.md say so too, and
    mention building on a Mac with `build/publish.sh`. Product owner, 2026-11-05.
15. README's license summary, the AI point: reworded by the product owner, 2026-11-05. ADDITIONAL-TERMS.md is unchanged.

**Task breakdown**:
1. [x] The screenshot mode: `--screenshots` (one theme per run, 2x), `build/screenshots.sh` and the demo data script. Proof:
   it captures every page in both themes; UI tests stay green.
2. [x] ADR-040 and tokens: the visual design, the OFL exception in DEVELOPER.md 4, the new theme tokens and chart palette.
   Proof: the theme key test; a new test that text on the surfaces and on selection meets 4.5:1 in both themes.
3. [x] Inter: shipped with its license (THIRD-PARTY-NOTICES, `licenses/`), tabular figures on number cells. Proof:
   `build/check-package.sh` finds its license; a smoke run measures the budget and the cold start.
4. [x] Type and controls: 13 px grids and tabs, 18 px SemiBold titles, 28 px controls, neutral buttons with one primary,
   tinted selection, one row rhythm (grids, trees, lists). Proof: captures; UI tests fail on any binding error.
5. [x] The shell: wordmark, sidebar and rail colors, the Jobs panel only while a job runs (finished jobs on the Jobs page and
   as a status line), the compact stat strip, the snapshot rail's wasted space. Proof: view model tests for the jobs state;
   the attribution test; captures.
6. [x] Logo and icons: logo A as the window and executable icon, the options kept, about 25 Duotone icons in
   `Themes/Icons.axaml`, the literal "x" buttons replaced. Proof: a test that every navigation item has its icon; captures at
   100 percent reviewed by the owner.
7. [x] Grid defects: name columns keep a minimum width (Compare, Allocations, Inspections), right-aligned numeric headers,
   no clipped headers or values, one percent format, the zero delta not red, LOH and POH column headers. Proof: unit tests of
   the column building and formats; captures at 1440 wide.
8. [x] Charts follow the theme: the icicle chart, key path diagram and both timelines redraw on a theme change, the brush
   converters replaced. Proof: UI tests that switch the theme and check the resolved colors (Backlog L14).
9. [x] Home and empty states: three start actions, no diagnostics on Home, a labeled cleanup, grids that say what to do when
   empty. Proof: view tests; captures.
10. [x] The retention diagram: thin edges, labels that stay readable, the target visible, Fit to width. Proof: geometry unit
    tests of the edge widths and labels; captures.
11. [x] The Live page: chart gridlines and a time axis, grouped actions, no empty column. Proof: geometry unit tests; captures.
12. [x] Page polish: Allocations layout, Inspections list (its heading outside the selection, zero-found rows compact),
    the small sweep (stray punctuation, the breadcrumb, gutters, title positions). Proof: captures and view tests.
13. [x] README and guide: the new README, `docs/USER-GUIDE.md` holding the old detail, images in `docs/images/`. Proof: every
    relative link resolves; each claim checked against the app; no em or en dashes.
14. [x] Docs: ARCHITECTURE.md and its pages, DEVELOPER.md (screenshots, the design tokens), Backlog items from decision 10.
    Proof: the Markdown sweep.

**Review summary** ([docs/reviews/sprint-3.10.md](reviews/sprint-3.10.md)): 5 High, 27 Moderate, 28 Low after dedupe, counting what the
confirmation rounds found. Highs fixed and confirmed: the screenshot script deleting the folder it was given; a dump's process name,
now from the path the dump records and logged with its rule; three README and guide claims that did not hold (tested to 8 GB, what a row
opens, any number of snapshots). Moderates (product owner): 26 fixed, among them Live's empty state, Fit to width out of reach, clipped
numbers, a cut tree's hidden warning, the time axis, failed jobs' reasons, next steps off Windows, an older cache opening when its
rebuild lacks the DAC, and tests that enforce the theme's contract; M16 moved to sprint 3.11. Lows listed; six fixed in passing. Measured:
start about 535 ms, worst dispatcher lag 184 of 200 ms, cold first shows over 100 ms 5 to 13 per run (from 3 to 7); first load of a Medium
dump 35 to 48 s, against 15.8 s in sprint 1.2, inside budget and in the Backlog. Not run: the Linux and macOS text on those systems; macOS
packages on a Mac.

---

## Sprint 3.11: First-show performance and refactor analysis

**Goal**: know why pages hitch the first time they open, and what in the code is worth refactoring, before Phase 4 adds to both.

**Background**: the product owner asked for this sprint on 2026-11-05, from the 3.10 review. M16: cold first shows over 100 ms went
from 3 to 7 per run to 5 to 13, exempt from the gate, with the worst dispatcher lag 184 of 200 ms and the cause only inferred.

**Scope, to settle at /start-sprint**:
- Trace a cold first show (`--smoke-trace`, with JIT events), attribute the blips, and fix what the trace supports. Consider a ceiling
  on logged first shows, and a run on a Medium dump, not only the demo dumps.
- A refactor analysis of the codebase: what to change, why and at what cost, as a ranked list the owner decides on. It proposes; any
  refactor it leads to is the owner's call.
- To consider pulling in: the Backlog's slower first load (the object pass), and the tests that fail only under full load.

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
