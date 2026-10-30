# HeapScope architecture: Threading model

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 8" still name the right section.

## 8. Threading model
- **UI**: view models are touched only on the dispatcher thread. All Application calls are `async` and
  awaited from view models; `.Result`, `.Wait()` and `Dispatcher.Invoke` for long work are forbidden.
  Application events (job progress, worker state) arrive on any thread and are marshaled with `IUiDispatcher`
  (`AvaloniaUiDispatcher` in the app, `ImmediateUiDispatcher` in tests). No dispatcher work item may block more than
  100 ms.
- **Responsiveness measurement**: `DispatcherWatchdog` posts an input-priority probe every 25 ms and logs any
  delay over 100 ms (with UI-process GC pause time). Since the Avalonia port (sprint 2.3.6) a second probe at the
  highest priority waits only for the operation already running (Avalonia's dispatcher has no operation hooks; the WPF
  app timed each operation and its priority). Its wait is a lower bound of the longest single operation. An operation
  starts after the previous highest-priority probe ran, so the time from that run to the next probe's run is an upper
  bound; smoke runs check the upper bound, probing every 10 ms from a thread of their own (sprint 2.3.6 review). Since sprint 1.3 each lag warning also gives the
  UI thread's own CPU time in the lag (Windows; the WPF app also gave the operations and time per priority that ran
  meanwhile): a lag with little UI thread CPU means the thread was waiting or not scheduled, not working. `HeapScope.exe --smoke-synthetic <n>` runs
  a warm-up job, then an n-object synthetic job, and exits 0 when no single dispatcher operation exceeded 100 ms
  and the probe lag stayed under the smoke gate
  (2x budget), 3 when it did not, 4 when the job failed, 5 when the first window took over 30 s, 6 when a grid walked a
  whole row list (every smoke run logs the UI's private and managed memory and the count of such walks, sprint 2.3.6
  review). Measured in sprint 0.3, steady state during a 5M job:
  2 to 16 ms (Debug) and 43 ms (Release, cold); intermittently 105 to 130 ms in Debug at job completion while the
  full integration suite runs (backlog item). One-time cold costs are not covered by the budget yet: first window
  render about 1 s, first job row about 130 to 190 ms, first showing of the Compare tab with rows 97 to 238 ms (1.4)
  (backlog: UI cold start). `--smoke-compare <dump>` (with `--smoke-open`) logs that first show and checks a warm
  re-query of the comparison. Sprint 2.3.6 (Avalonia, Release build, this machine): Small dump checked lag 102 to
  105 ms and longest operation at most 64 ms (the upper bound; the lower bound was 44 to 46 ms; WPF, timed exactly:
  lag 90 to 127, operation 44 to 68), Medium lag 71 ms and operation at most 69 ms; cold first shows (not checked) at
  most 94 to 109 ms; UI process 219 to 229 MB private (50 to 92 MB managed) after the Small or Medium smoke, with no
  whole-list grid walk (the prototype: 197 MB); the first window 2.2 s after process start warm (WPF the same), 8 to 15 s for the first launch after a build
  while its files are scanned. Every smoke run logs the first-window time and fails above 30 s (a start that hangs).
- **Hybrid CPUs (ADR-019, sprint 1.5)**: on a CPU with performance and efficiency cores, Windows runs the threads of a
  window that is not in the foreground on efficiency cores (EcoQoS). The UI thread then did the same layout 2.5 to 5
  times slower (measured by pinning the UI process: performance cores 42 to 44 ms, efficiency cores 118 to 127 ms and up
  to 250 ms under contention), which was the run-to-run variance of earlier smoke runs. The UI process opts out of
  execution speed throttling at startup (`SetProcessInformation`, `ProcessPowerThrottling`); the worker does not.
- **Grids (sprint 1.5)**: a grid's first page is fetched before the grid binds to the new rows (one layout of filled
  rows, not an empty layout and a second pass), and the rows are bound after a yield at background priority so a
  pending render runs as its own operation. The types grid shows bytes per generation only on request (a
  "Generations" checkbox, off by default; the six columns were half the layout time). Highlighting a filter match sets
  `TextBlock.Text` unless the cell has a match (the first access to `Inlines` forces a synchronous layout pass). The
  object view's field and retention trees are Avalonia `TreeView`s, which are not virtualized; they stay small because
  the worker caps them (100 array elements per level, field text 1,000 characters, 2,000 retention nodes). A tree of
  unbounded size (the 2.4 dominator tree) is a flattened, paged grid instead (sprint 2.3.6 review): the Retainers tab's
  grid indents its rows and expands them in place with a click on the arrow, Enter, Right and Left (`GridBehaviors`
  `ExpandCommand` and `CollapseCommand`), and the worker flattens the tree again on each expansion. `VirtualRowList` keeps at most 50 pages (10,000 rows) and drops the pages
  farthest from the last one asked for.
- **Pages and views are built once (sprint 1.5)**: the shell builds each page on first show and keeps it, so switching
  pages neither rebuilds grids (about 100 ms on the Snapshots page) nor loses scroll and expanded state; the Snapshots
  page keeps its snapshot panel built without a snapshot. In the WPF app a hidden panel was laid out, so templates were
  applied when the page was built; Avalonia neither measures nor templates a control with `IsVisible` false, so a hidden
  panel's templates are applied on its first show (sprint 2.3.6). The navigator's types, instances and object views are
  built with the window; navigating swaps their data.
- **Smoke runs measure phases**: `--smoke-open` warms up with a synthetic job and the Snapshots page shown once (logged
  as a cold cost), then checks the open phase (logged on its own line), a drill-down (largest type's instances, the
  largest object, its references; the first show is logged as a cold cost, a re-sort and a second object are checked)
  and, with `--smoke-compare`, the comparison. The checked maxima of all phases are combined for the exit code.
  `--smoke-live N` (sprint 1.7) charts N made-up samples on the Live page at up to 200 a second (each a redraw of up to
  30 minutes of history, far more often than the real one a second) and checks the whole feed; it writes no timeline.
  `--smoke-trace <file>` also records a CPU trace of the run (sprint 1.5); tracing slows the UI enough that traced runs
  are for finding where time goes, never for checking the budget. Measured after these changes (Release, 24 logical
  cores): Medium and DenseHeap smoke with drill-down and comparison, longest operation 36 to 63 ms, lag 49 to 128 ms,
  every run within the gates.
- **Leases**: `GraphSnapshot.Dispose` never blocks; the last lease released disposes the graph (section 7).
- **Libraries** (everything below UI): `ConfigureAwait(false)` on every await; every async method takes a
  `CancellationToken`.
- **Worker**: StreamJsonRpc dispatches requests concurrently. Loading a snapshot is serialized per snapshot
  (`SemaphoreSlim`); read-only queries on a loaded snapshot run concurrently. Derived data (the type aggregation, the
  reverse index, the root path tree, the dominators, the retained sizes per grouping) is one shared computation per
  slot; slots can run at once, so those with large working arrays reserve their memory in one worker-wide
  `MemoryReservations` first (sprint 2.3 review M2).
  CPU-heavy algorithms may use `Parallel.For` with explicit partitioning, never per-object tasks. Queries on
  mapped snapshots hold a lease for their whole duration (section 6.5). The per-snapshot type aggregation
  (sprint 1.3) is computed once, behind its own per-snapshot `SemaphoreSlim`, with `Parallel.For` over 1M-object
  partitions and thread-local arrays merged at the end; it is dropped when the snapshot closes.
- **Host lifecycle**: UI and CLI start their generic host with `StartAsync` and stop it with a bounded
  `StopAsync` (UI 5 s, CLI 10 s) before exit, so hosted services such as the worker client shut down in order; the
  worker host honors that bound and kills the worker when it is reached. In the desktop app,
  closing the main window cancels the close once, awaits the stop on the dispatcher without blocking, disposes
  the host and then shuts the application down; a stop requested by the host closes the window through the same
  path.
