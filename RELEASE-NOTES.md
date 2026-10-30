# HeapScope release notes

Each sprint sets the version to its number (sprint 3.5 is version 3.5.0). A version is released when the owner says
so: it is merged into `main`, tagged `vX.Y.Z` and published as a GitHub Release whose notes are the sections since the
previous release. Versions without a release are listed too, so every change can be traced to its sprint. The review of
each sprint is in `docs/reviews/sprint-X.Y.md`.

| Version | Released |
|---|---|
| 3.5.0 | 2026-10-29, `v3.5.0` |
| 2.2.0 | 2026-10-25, `v2.2.0` |

Everything else was built on `develop` and first shipped in the next release above it.

## 3.6.0: Urgent fixes (sprint 3.6)

From the peer review of 3.5.0 (`docs/reviews/peer-review-3.5.md`).
- **Session records are no longer lost**: reading `session.json` never makes another program's save fail, and a session
  that cannot be read stops with the reason and what to do instead of moving your next captures into a new session.
- **A DAC that came with a dump is not loaded without your say**: a data access component found only next to a dump is
  native code from whoever made the dump. The app asks, naming the file and its SHA-256, and remembers a yes; the CLI
  refuses and names `--trust-dac <sha256>`. A DAC HeapScope's own capture copied is trusted (unless HeapScope ran
  elevated). After a "no", a dump with an index cache still opens, without field values.
- **Builds carry their licenses**: every published build holds HeapScope's license files and each shipped package's own
  license and notice files under `licenses/`, and leaves out two TraceEvent native binaries nothing uses.
- **CI checks more**: the integration tests run on every pull request, test results and HeapScope's logs are kept for
  14 days, and CI and the local gate (`build/gate.sh`, now bash) share their scripts.
- Worker protocol 20.

## 3.5.0: Leak detection depth (sprint 3.5)

- **Pending async methods**: async methods that started and never finished, by method, with their count and bytes.
  The framework's own waiting work (for example Kestrel's connection loops) is left out and logged.
- **Finalizer backlog**: objects whose finalizer is ready to run but has not, by type; a queue that does not drain
  points to a blocked or slow finalizer thread.
- **Type growth**: types whose live object count grew between two full dumps of the same process, with the count and
  bytes then and now. Gcdumps are refused for it (they name nested types differently and hold no root details).
- **Unreachable objects as a set**: the objects a full dump still holds that no root reaches (garbage the GC has not
  collected yet), opened from the Types page or `heapscope-cli set --unreachable`. Full dumps with a complete graph
  only.
- Fixtures: LeakyApi and LeakyConsole can grow pending async and finalizer backlog leaks on request.
- Worker protocol 19.

## 3.4.0: Live workflow and housekeeping (sprint 3.4)

- **Live page**: Get snapshot, and Force GC (asks first; .NET 5 or later) beside the counters.
- **Allocation trace for a whole run**: recorded while monitoring, with a size limit (1 GB) and a free disk minimum;
  the run's notes say when and why it stopped.
- **Past runs**: a run's timeline is saved in the session and can be reopened.
- **One capture per process at a time**, across every page.
- **Deleting snapshots and sessions**, with their index caches: confined to the session folder, refused while a
  snapshot is open or a capture is writing, and every removed file logged. "Clean up old sessions" on the Home page
  proposes the sessions not opened for 30 days (or the days you pick).

## 3.3.0: Comparison depth (sprint 3.3)

- New, dead and survived objects matched by address between two full dumps; each count opens its objects as a set.
- Retained bytes in diffs: each type's retained size in both snapshots and the change.
- Trends across 2 to 20 snapshots of a session, and comparing snapshots of different sessions.
- Diff growth by generation.

## 3.2.0: Retention views (sprint 3.2)

- **Similar Retention**: a set's objects grouped by how they are retained, each group with its most different paths.
- **Key retention paths as a diagram**: the distinct paths to roots merged into one graph.
- Group by interface in the types grid and in Compare.

## 3.1.0: Object sets (sprint 3.1)

- Any selection becomes an object set with its own views (types, instances, dominators, generations, what it
  retains): several types, a namespace or assembly, a Compare count, an inspection or detector row (exactly the
  objects it counted), bookmarks, or the objects retained by another set.
- Sets are steps in the analysis path; the CLI gets `set`.

## 2.8.0: Packaging and distribution (sprint 2.8, planned)

Not built yet: waits for the audiences and channels. CI already builds self-contained packages per platform.

## 2.7.0: Navigation and polish (sprint 2.7)

- Full back and forward navigation history across views, object bookmarks, consistent links between views.
- Keyboard support throughout, and CSV export of any grid.

## 2.6.0: Smart leak detectors (sprint 2.6)

- Detectors that name the leak: event handler leaks, disposed but still referenced objects, timers keeping objects
  alive, static collections growing across snapshots, HttpClient instances created per use, undisposed service scopes.
- A clean LeakyApi workload with look-alike patterns, on which no detector fires.

## 2.5.0: Allocation tracking (sprint 2.5)

- Allocation sampling with call stacks over EventPipe, symbol resolution, top call stacks and types over a time range,
  in the app and the CLI. A 600 s trace reads in about 2 s.

## 2.4.0: Retention explorer UI (sprint 2.4)

- Drill-down dominator tree, biggest retainers, grouping by type and namespace, and links between the summary, diff,
  inspection and retention views.

## 2.3.7: Port peer review (sprint 2.3.7)

- A wider review of the Avalonia port. Logs now back up any result a user contests: a log file per process, a
  correlation id across the app, CLI and worker, and the inputs and rules behind each result (never dump contents).

## 2.3.6: Avalonia port (sprint 2.3.6)

- The app runs on Avalonia on Windows, Linux and macOS, at parity with the WPF app, which is retired. Capture stays
  Windows only; elsewhere, open a dump taken with `dotnet-dump`.
- CI builds and tests on all three systems and packages self-contained builds.

## 2.3.5: Cross-platform and sharing decision (sprint 2.3.5)

- The engine, worker and CLI run on Linux against Linux core dumps. The decision to move the UI to Avalonia
  (ADR-025), and what sharing results means without sharing dumps.

## 2.3.0: Dominator tree and retained size (sprint 2.3)

- Retained size per object and per type from a dominator tree, cached beside the snapshot.

## 2.2.0: Retention paths (sprint 2.2), released as `v2.2.0`

- Why an object or type is alive: shortest paths to GC roots, root kinds in plain language, the top distinct paths,
  framework noise collapsed. The first public pre-release (source only, Windows x64, WPF).

## 2.1.0: Reverse references (sprint 2.1)

- Who references an object or the instances of a type, from an index cached beside the snapshot.

## 1.9.0: Phase 1 hardening (sprint 1.9)

- Internal clean-up before Phase 2, a measurement of the Phase 2 indices against the memory budgets, and a local test
  gate.

## 1.8.0: gcdump support (sprint 1.8)

- Capture and open gcdumps beside full dumps; views a gcdump cannot answer stay visible, disabled, with the reason.
  Diffs across gcdumps and mixed kinds.

## 1.7.0: Live counters and timed snapshots (sprint 1.7)

- Live counters (heap size, GC counts, allocation rate) in a chart; snapshots every N minutes or above a heap size;
  a session timeline.

## 1.6.0: Simple inspections (sprint 1.6)

- One-click findings: duplicate strings, LOH contents, empty and oversized collections, large and sparse arrays.
  CLI `inspect`.

## 1.5.0: Object search and inspection (sprint 1.5)

- Instances of a type, field values (nested structs, arrays, strings), outgoing references, back and forward.

## 1.4.0: Snapshot diff (sprint 1.4)

- What grew between two snapshots, by type; CLI `diff --fail-on-growth` for builds.

## 1.3.0: Heap summary by type (sprint 1.3)

- Count, size and generations per type in a fast, sortable, filterable grid; CLI `summary` as a table, CSV or JSON.

## 1.2.0: Full dump loader (sprint 1.2)

- Open a full dump with progress and cancellation; reopen it quickly from its index cache.

## 1.1.0: Process discovery and capture (sprint 1.1)

- List local .NET processes and capture full dumps into a session library; CLI `list` and `capture`.

## 0.1.0 to 0.5.0: Foundations (sprints 0.0 to 0.5)

- 0.0: the charter, architecture and sprint process.
- 0.1: the solution skeleton with enforced layering.
- 0.2: the snapshot abstraction and the compact heap graph.
- 0.3: the worker process over named pipes, with progress, cancellation, paging and crash recovery.
- 0.4: the leaky fixture apps (LeakyApi, LeakyConsole) with known answers.
- 0.5: the index cache format.
