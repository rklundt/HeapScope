# Review: Sprint 1.3 Heap summary by type

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-1.1`). Phase 1 runs in
autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided autonomously".

Totals after dedupe: 1 High, 19 Moderate (two found while verifying fixes), 18 Low (Lows that repeated a Moderate are folded into it).

## High / Urgent

### H1 Distinct types that share a name were merged into one row
- Reviewers: gc-specialist (High), solution-architect (Moderate).
- Location: TypeGroups.cs (`Build` keyed rows by display name), TypeSummarySource.cs (name is the only key).
- What: the type table has one entry per method table, but Type grouping merged rows by name. The same assembly loaded
  into two AssemblyLoadContexts (a stale plugin context, the classic ALC leak) or same-named types in different modules
  became one row with summed counts; `TotalTypes` counted method tables while the rows counted names.
- Why it matters: incorrect analysis results for a common .NET leak; the 1.4 diff and the 2.6 detectors would inherit
  the keying.
- Fix: Type grouping builds one row per type table entry, never merged. Colliding names get the module's file name
  (`Plugin.Cache [Fork.dll]`), then a number in type index order when the module file name is also the same
  (`Plugin.Cache [Plugin.dll] #2`, two load contexts; the loader stores module file names, not paths), so every row name stays unique and a stale copy is visible on its own row. Namespace and assembly
  grouping still merge, by design. `SyntheticGraphBuilder.Object` takes an optional module and method table so tests
  can build such graphs. Test: `Distinct_types_that_share_a_name_keep_their_own_rows` (four rows from four method
  tables: two load contexts of one module, another module, another type).
- Status: **Fixed.** Confirmation: see below.

## Moderate (decided autonomously)

- **M1 A released result set could be re-queried; pages of a replaced list raced the release** (architect,
  senior-dev). **Fix now:** `ResultSetHandle` has a released state; after `ReleaseAsync` paging throws
  `ObjectDisposedException` and never re-runs the query; release takes the re-query gate, so a re-query in progress
  finishes first and its new set is the one released. `VirtualRowList` has a lifetime token, canceled when a newer
  query replaces it: pages in flight stop, their rows and errors are dropped. Tests: `ResultSetHandleTests` (five,
  with a gated fake worker), `A_page_still_loading_when_a_new_query_replaces_the_rows_is_dropped_and_leaves_no_set`,
  and the integration test now also releases the handle and checks it is finished.
- **M2 `Show(null)` did not cancel the running query or the pending filter** (senior-dev). **Fix now:** `Show(null)`
  and every re-query cancel both (a grouping or sort change also drops a pending debounce, so it does not fire twice);
  replaced token sources are disposed. Test: `Closing_the_snapshot_while_its_query_runs_leaves_the_view_empty`.
- **M3 A superseded query's error overwrote the newer query's status** (senior-dev). **Fix now:** every failure of a
  canceled query is ignored. Test: `Only_the_latest_of_several_quick_queries_owns_the_view`.
- **M4 Exceptions lost in fire-and-forget paths; no logging** (senior-dev). **Fix now:** the view model takes an
  `ILogger`; unexpected query and page failures are logged and shown as a plain status with a next step; a failed page
  is removed so scrolling retries it. Writing the test found a second bug: a page that failed synchronously was stored
  after its own removal and never retried; the slot is now reserved before the load starts. Test:
  `A_page_that_fails_shows_a_message_and_is_fetched_again_when_asked_for`.
- **M5 The header arrow disagreed with the sort** (senior-dev; architect and gc-specialist as Low). **Fix now:** the
  view model is the single source of truth (`SortState`, "Bytes|desc"); `GridSorting.State` sets the arrows from it and
  again after each new ItemsSource; header clicks only run the command.
- **M6 Aggregation canceled by its first caller; side dictionaries with a close race** (senior-dev, architect;
  gc-specialist as Low). **Fix now:** derived data lives on the snapshot's entry (`DerivedData`, dropped in one step on
  close); the aggregation is one shared computation with its own lease and a token tied to the snapshot's lifetime, and
  callers only stop waiting when they cancel. This is the home for later derived data (reverse references, dominators).
- **M7 Mirrored `GroupKey` untested; unknown enum values silently fell back** (architect; senior-dev as Low). **Fix
  now:** `Group_key_mirrors_type_grouping`; unknown grouping or sort values fail with `InvalidArgument`. Test:
  `Unknown_grouping_or_sort_values_are_refused`.
- **M8 Filter documented as case-insensitive** (architect; senior-dev as Low). **Fix now:** `TypeSummaryRequest.Filter`
  and the CLI `--filter` help describe smart case.
- **M9 Unreachable objects counted without saying so** (gc-specialist). **Fix now:** the UI totals line, the CLI header
  and the JSON (`IncludesUnreachable`) say the totals cover all objects in the dump, unreachable ones included; the
  contract documents it; reachability comes with 2.x.
- **M10 `HeapKind.Unknown` bytes missing from the bar** (gc-specialist; architect as Low). **Fix now:** `HeapKindDto`
  mirrors Core's `HeapKind` including Unknown = 0; the totals list Unknown when present ("Unknown generation" in the
  bar, its own theme color), so the bar always adds up. The per-kind grid columns stay six (an Unknown column would be
  empty for every fixture). Test: `Objects_of_4_GiB_or_more_and_unknown_heap_kinds_are_counted_exactly`.
- **M11 Per-thread aggregation arrays unbounded by type count** (gc-specialist; architect as Low). **Fix now:** the
  degree of parallelism is capped so the per-thread arrays stay within 256 MB; partition arithmetic in `long`;
  cancellation checked every 64K objects.
- **M12 "N of N" totals for namespaces and assemblies** (gc-specialist; architect and senior-dev as Low). **Fix now:**
  `TypeSummaryResult.UnfilteredRows` (rows of the grouping before the filter) is the denominator for every grouping,
  with proper plurals ("assemblies"); the empty message follows the grouping. The CLI prints "N of M rows match".
- **M13 Enumerating the row list fetched every page** (gc-specialist, senior-dev). **Fix now:** enumeration and
  `CopyTo` return placeholders without fetching; the no-op `CollectionChanged` is explained. Test:
  `Enumerating_the_rows_fetches_nothing`. The unbounded set of rows scrolled through (one list per query) is
  **deferred** to the backlog for 1.5's object lists (a bounded window of pages).
- **M14 Rows read cells by position, ignoring the column descriptors** (architect). **Fix now:** `TypeRowLayout` maps
  cells by column name and fails loudly when a required column is missing. A generic `VirtualRowList` over a row
  factory is **deferred** to 1.4, which brings the second row type.
- **M15 Closed sort enum** (architect). **Deferred** to 1.4's start: choose a per-view sort or a generic sort key (a
  column name validated by the worker) when the diff's delta columns arrive.
- **M16 Missing unit tests** (all three). **Fix now:** `TypeSummarySourceTests` (sort orders both ways with ties,
  filter with the unfiltered count, namespace and assembly grouping with an unknown module, columns with and without
  generations, percent, unknown enums, empty graph); the 4 GiB size path and Unknown kind in Analysis; NameFilter
  cases (`*` with capitals, lone `*`, trimming, CamelHumps in a generic argument, filter longer than the name);
  `ResultSetHandleTests`.
- **M17 UI fake could not model races** (senior-dev). **Fix now:** the fake gates summary queries and pages, removes
  released sets, fails unknown sets with `ResultSetNotFound`, and breaks ties like the worker. Running the view model
  tests on a single-threaded synchronization context and a `TimeProvider` for the debounce are **deferred** to the
  backlog (the tests pass repeatedly as they are).

## Found while verifying the fixes (decided autonomously)

- **M18 Worker GC threads starved the UI on snapshot open** (lead, smoke runs). The Medium smoke open failed its lag gate
  in most Release runs (probe lag 550 to 760 ms, no single operation over 60 ms). New watchdog diagnostics showed the UI
  thread using 16 to 94 ms of CPU during those lags and the time spent outside dispatcher operations, and capping the
  worker's GC heaps at 12 of 24 removed it (5 of 5 runs passed, same load time). **Fix now:** the worker starts with
  `DOTNET_GCHeapCount` at half the logical processors (`Worker:GcHeapCount`); lag warnings now carry the UI thread's
  CPU time and a per-priority breakdown. After the fix, Medium passed 8 of 8; the DenseHeap and Small dumps still showed
  one 300 to 600 ms lag right after the snapshot opened in 2 of 6 runs, again with little UI thread CPU. **Deferred:**
  stays on the backlog's High dispatcher lag item with this evidence; a WPR trace (administrator rights) is the next
  step.
- **M19 An unexpected exception while starting the worker hung the UI** (lead). A bug in the first version of the
  M18 fix (`ProcessStartInfo.Environment[...]` throws for a missing key) escaped `EnsureStartedAsync` as
  `KeyNotFoundException`; the UI's handlers catch only the worker exception types, so the smoke run waited forever.
  **Fix now:** the lookup uses `TryGetValue`, and any unexpected start failure is logged and rethrown as
  `WorkerStartException`, so every front end shows it and the smoke run exits 4.

## Low / Trivial (not fixed)

- Row identity for drill-down (a hidden key mapping a row to its type indices) is needed when 1.4 opens objects from a
  count (architect).
- `ResultColumn.Name` is both identifier and header (architect).
- ADR-016 does not mention shared rule code such as `NameFilter` in Contracts; 4.4 does not list the new wire types
  (architect).
- NameFilter tests live in Application.Tests (no Contracts test project) (architect).
- The paging measurement over the pipe uses four-column synthetic rows; the ten-column cost is measured without the
  pipe (gc-specialist).
- Per-query `SummaryRow` objects and `ulong[7]` arrays; `EstimatedBytes` underestimates them (gc-specialist).
- NameFilter re-parses the filter per row; `NamespaceOf` allocates per call (gc-specialist).
- Gate semaphores of the old aggregation cache were never disposed (gc-specialist; removed with M6).
- Highlighting follows each keystroke before the debounced rows arrive (senior-dev).
- CLI `--top` not validated, `--top` with `--all` not refused, release before output (senior-dev).
- CLI summary failure exit codes untested; JSON test reads merged output (senior-dev).
- Progress from parallel partitions can arrive out of order (senior-dev).
- `ResultSetHandle._gate` disposal and the first-run totals were undocumented (senior-dev; both now commented).
- An invalid `Ui:Theme` falls back silently (senior-dev).
- The theme test sits in the view model test class (senior-dev).
- `#n` suffixes follow each dump's own type index order, so the 1.4 diff must not key on display names (gc-specialist,
  re-review).
- `DisplayNames` is rebuilt per query; it could be computed once per snapshot in `DerivedData` (gc-specialist,
  re-review).
- The shared aggregation reports progress only to the caller that started it (gc-specialist, re-review).

## Confirmations

- H1: **confirmed** by gc-specialist. One row per type table entry, no name-keyed dictionary for Type grouping; unique
  names that leave single names unchanged (known answers still match); numbering stable across queries and reopens
  because the type index order is persisted in the index cache; row count and `TotalTypes` now agree by construction.
  No new High issue in the related changes (`DerivedData`, parallelism cap, Unknown heap kind).
