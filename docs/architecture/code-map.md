# HeapScope architecture: code map

Part of [ARCHITECTURE.md](../ARCHITECTURE.md), section 3: what each project holds, with the sprint that added it in
brackets.

Code map by project (reorganized in sprint 1.9 from the per-sprint notes; the sprint that added a piece is in brackets):

- **Core.** `ISnapshot`, `GraphSnapshot` and `DumpSnapshot` (a full dump: the graph plus a reader, section 5.1) [1.5];
  `SnapshotInfo` with `FreeBytes` and `SnapshotFacts` [1.6]; the capability model; `IObjectReader` (fields as a tree of
  `ObjectField`, typed `ObjectValue`s with `ObjectValueKind` and `EnumMember`, `ReadField` by path, `ReadStrings` in bulk,
  string text, reference paths) [1.5, 1.6]. `Graph/`: the compact graph, `HeapGraphBuilder`, `TypeKind.ReferenceArray`
  [1.6] and `TypeNameForms` (a full dump's type name in the form a gcdump records it) [1.8]; types without a method
  table are told apart by name and module [1.8]. `Loading/`: the loader contract both loaders share (`ISnapshotLoader`
  with its `Version`, `LoadResult`, `LoadProgress`, `LoadTimings`, `LoadDiagnostics`) and `SnapshotFileSignature` (a
  file's first bytes tell a minidump, `MDMP`, from a gcdump, a FastSerialization stream whose first object is a
  `GCHeapDump`) [1.8]; a Linux or macOS core file (ELF `ET_CORE`, Mach-O `MH_CORE`) is a full dump too, and
  `IsMinidump` tells the two apart [2.3.5]. `RecordedPath` (a path recorded in a snapshot, split on both separators
  whatever system reads it) [2.3.5]. `Synthetic/`: `SyntheticGraphs` and `SyntheticGraphBuilder`, which also makes arrays and strings
  [1.6]. `Graph/ReverseReferences` (the incoming references CSR, built by counting and filling, or from an index file's
  columns) and `HeapGraph.Attach` (an optional index is disposed with the graph, after the snapshot's last lease) [2.1];
  `DependentHandle` and `HeapGraph.DependentHandles` / `IsDependentHandleEdge` [2.2].
- **Analysis.** `TypeAggregation`: one parallel pass over the objects, counting objects, shallow bytes and bytes per
  heap kind for every type, computed once per snapshot and kept on the snapshot's entry in the worker [1.3].
  `TypeGroups`: rows by type, namespace or assembly. Type rows are one per type table entry (method table), never merged
  by name: distinct types that share a name (one assembly in two load contexts, same-named types in different modules)
  keep their own rows, named `Name [Module.dll]` and then `#n` in type index order (stable for a snapshot, not across
  snapshots), so a stale copy of a plugin's types is visible on its own row; namespace and assembly rows merge by
  design. Totals count every object in the snapshot: a full dump's unreachable ones included, a gcdump's survivors only
  (the UI and CLI say which); a live and dead split comes with the retention analysis (2.x) [1.3, 1.8]. `TypeDiff`: rows
  matched by type name plus module file name, summed per key, grouped like the summary; each side keeps its own names,
  so a row opens on either side by that side's name; across snapshot kinds the full dump's names are matched in their
  gcdump form and the "copies" marker is off [1.4, 1.8]. `Inspections/`: `IInspection`, `InspectionContext`,
  `InspectionResult`, `InspectionTable`, `RowTable<TRow>` and `InspectionCatalog`; the graph-only inspections in
  `GraphInspections.cs` (large object heap, large arrays, sparse arrays, finalizable objects, fragmentation) and the
  reader ones in `ReaderInspections.cs` (empty and oversized collections, duplicate strings) [1.6, ADR-020].
  `IncomingReferences`: an object's distinct referrers with their reference counts, and a type's referrers grouped by
  their type name and module (referring objects, references, targets referenced, referrer bytes); `TypeGroups.MatchTypes`
  finds the method tables of a type key (name and module file name), shared with instances [2.1]. `Retention/`:
  `PathRoots` (where paths start, root priorities, roots in plain language), `RetentionSearch` (one object's paths),
  `RootPathTree` and `KeyPaths` (a type's key retention paths) [2.2]; `DominatorTree` (semi-NCA and retained sizes into
  spans the caller gives), `Dominators` (the kept columns, managed or from a sidecar) and `GroupRetention` (a group's
  retained lower bound), with `TypeGroups.GroupOfType` numbering the groups of a grouping [2.3]; the dominator tree's
  children (`DominatorTree.BuildChildren`, kept with the dominators), `DominatorTreeRows` (the tree flattened for a paged
  grid, with `DominatorRow`, `DominatorTreeView`, `DominatorGrouping` and, since 3.8, the caller's `DominatorGroupTables`), `IcicleLayout` and `SetRetention` (what a set
  retains together, exactly) [2.4]. `Sets/ObjectSet` (a set's members as sorted object indices and a bit mask, built from
  types, addresses or indices), `Sets/SetDominators` (the members no other member dominates), `TypeAggregation.Compute`
  over a set, `SetRetention.Compute` over a set (optionally returning the retained objects as a set),
  `TypeGroups.MatchGroup` (a namespace's or an assembly's types) and `InspectionTable.MembersOf` (the objects a leak
  detector row counted) [3.1]. `Retention/SimilarRetention` (a set's members grouped by retention pattern, each group
  with its most different paths, and a group's members by rank), `Retention/PathSignatures` (the interned key path
  signatures, shared by key paths and Similar Retention), `KeyPaths.ForMembers` (a set's key paths), and interface
  grouping in `TypeGroups` (`GroupKey.Interface`, `InterfaceKeys`, `MatchInterface`) and `TypeDiff` [3.2].
  `ObjectIdentity` (two snapshots' objects matched by address and type in one merge, a bit per object of each side),
  `TypeDiff`'s `DiffExtras` (New and Dead per row, retained bytes per group of each side, bytes per heap kind) and
  `TypeTrend` (rows across several snapshots) [3.3].
- **Storage.** The index cache format, reader and writer (section 7); the metadata records the loader version that
  built the graph [1.8]. `IndexCacheFileWriter` (one index file section by section, header last, rename; the base writer
  uses it and returns the cache identity), `IndexCacheFile` (any index file opened and verified in memory or mapped,
  `TryGetSection<T>`), `IndexFileLayout` (the sections of the base cache and of each sidecar), `IndexFileDirectory` (the
  header and directory checks both readers share) and `IndexCacheLocator.GetSidecarPath` [2.1]. `MappedIndexFileWriter`
  (sections planned up front and filled in place through a read-write mapping of the temporary file, then hashed, header
  last, rename) and the sidecar semantics version (`IndexFileLayout.SemanticsVersion`, checked on open) [2.3].
  `IndexCacheKey` (the per-user key that signs and checks every index file's header region, ADR-036;
  `IndexCacheKeyOutcome` says where the worker's key came from) [3.7].
- **Capture.** Process discovery and elevation, `DumpSizeEstimator` (the disk preflight's estimate) [1.1]; `DumpCapturer` (full dumps written by the runtime) and `GcDumpCapturer`
  (the vendored heap snapshot over EventPipe, failures classified, its log lines sent to the HeapScope log) [1.8], both
  writing through `PartialSnapshotFile` (reserve, never overwrite, commit by rename, discard with retries) [1.9];
  `TraceRecorder` (EventPipe sessions to `.nettrace`, profiles cpu and gc) [1.5]; `CounterMonitor` (an EventPipe session
  on the target's `System.Runtime` EventCounters) and `CounterSampleBuilder` (counter events to one `CounterSample` per
  interval) [1.7].
- **Loaders.ClrMd.** `ClrMdDumpLoader` (truncated dumps refused or marked incomplete: heap creation failures, objects
  without a type, segments whose memory is missing, files shorter than their minidump directory) [1.2, 1.8];
  `ClrMdObjectReader` (opens the dump on first use, serialized, disposed with the snapshot) and `ClrMdDump` (opening
  shared with the loader, runtime creation failures wrapped with a next step) [1.5]; `MinidumpHeader` [1.2, 1.8];
  `CoreFileHeader` (a Linux or macOS core cut short: program headers or load commands, or a segment's file range, past
  the end of the file; such a core loses CompleteGraph and ExactCounts like a truncated minidump), a core's capture time
  from the file's write time marked `SnapshotFacts.CaptureTimeFromFile` with a note, loader version 3 [2.3.5];
  `UnixLocalPath` (the local path rule on Linux and macOS: the mount holding the path, links resolved) [2.3.5];
  `DacLocator` and `LocalOnlyFileLocator` (the matching DAC from local folders only, ADR-017, section 6.3) [0.1, 1.2];
  a DAC next to the dump only once trusted by its SHA-256 and loaded from a private copy (`BesideDumpDirectories`,
  `UntrustedDacException`, ADR-035) [3.6];
  the DAC of the system HeapScope runs on, matched on Linux and macOS by `ImageIdentity` (ELF GNU build id, Mach-O
  UUID) against the DAC itself or, when the dump archives it under the runtime's identity, against `libcoreclr` in the
  same folder; ClrMD reads a core file's runtime version as 0.0 at times, and the runtime's folder name names it then
  [2.3.5].
- **Loaders.GcDump.** `GcDumpLoader`, `RootClass` (pseudo node names to root kinds), `TypeNames` (names normalized to
  the full dump loader's form), `ObservedReadStream` (progress and cancellation while the vendored reader reads) [1.8].
- **ThirdParty.GcDump.** The vendored dotnet-gcdump code (PerfView's `MemoryGraph`, `GCHeapDump` and the EventPipe heap
  dumper), MIT, with its five changes listed in its `CHANGES.md` [1.8, 1.9, 3.7].
- **Contracts.** `IWorkerService`, `WorkerProtocol`, `HeapScopeErrorCode`, the wire enums and `CapabilityDescriptions`
  [0.3]; `DacTrustDetails`, the error data of `DacNotTrusted` [3.6]; `SnapshotFeatureIds`, `FeatureAvailability` (the
  descriptor's availability list) and `SnapshotAvailability` (`IsAvailable`, `WhyUnavailable`, failing closed) [3.8]; the DTOs, one file per area (`WorkerDtos`, `SnapshotDtos`, `SummaryDtos`, `ObjectDtos`, `InspectionDtos`)
  [1.9], and `SetDtos` (`ObjectSetSpec`, with its shape, address hash and cache key as extension members in
  `ObjectSetSpecs`, so they are never serialized) [3.1]; the Similar Retention DTOs in `RetentionDtos` [3.2]; `NameFilter`, the one filter rule set shared by the worker (matching) and the UI (highlighting): an all lower-case
  filter is a case-insensitive substring; a filter with capitals matches as typed, then as CamelHumps within one
  identifier (`SCE` finds `StaticCacheEntry`); `*` matches any run of characters [1.3]; terms separated by spaces must
  all match, `-term` leaves out, `module:` (or `m:`) tests the row's module, and a term still being typed filters nothing
  [2.7]. Summary and diff rows in Type grouping carry hidden `TypeName` and `Module` columns, the key that opens a row's
  instances [1.5]. `ResultColumn.IsHidden` marks such link columns and `HasValues` the columns that carry the target's
  data (instances' `Value`, a duplicate string's text), for exports (ADR-026) [2.7]. Referrers:
  `ReferrerColumns`, `TypeReferrerColumns`, `ReverseIndexInfo` (sidecar or built, mode, time, size, saved), the
  requests and results (with `CompleteGraph`), and the descriptor's `CacheIdentity`, which a reopen after a worker
  restart compares (review M10) [2.1]. `RetentionDtos.cs`: `RootKindDto`, `PathRootDto`, `RetentionNodeDto`,
  `PathStepDto`, `KeyPathDto`, the object and type paths requests and results [2.2]. `RetainedDtos.cs`:
  `DominatorIndexInfo`, `RetainedRequest`, `RetainedResult`; the `Retained` columns and `IncludeRetained` flags of type
  summaries and instances [2.3]. `DominatorDtos.cs`: the tree request and result, `DominatorTreeColumns`,
  `DominatorTreeKeys` (the row keys clients link with), the icicle request and segments, the set retention request,
  result and `RetainedBySetColumns`; `ResultColumn.Bound` with `ValueBound` and `ValueBoundTexts` [2.4].
- **Worker.** `Program` and `ParentProcessWatcher` (the worker exits with its front end) [0.3]; `Rpc/`: `WorkerRpcServer`, `ResultSetStore`, `ThrottledProgress` [0.3]; `WorkerService` in partial files
  by area: the worker's own calls and result pages (`WorkerService.cs`), snapshots, summaries and diffs, objects, and
  inspections [1.9]; `ContractMapping` (Core's enums on the wire, one place) [1.9]; the result sources:
  `TypeSummarySource` (groups, filters and sorts the cached aggregation per query, so a re-query costs microseconds per
  type) [1.3], `TypeDiffSource` (leases both snapshots and reuses their aggregations) [1.4], `InstancesSource` (object
  indices of the types with a key, filtered by address prefix, regular expression or text, sorted by any column, holding
  a snapshot lease, scoped by `InstanceScope`) [1.5, 1.6], `ReferencesSource` (graph order, named by field path or array
  index when a reader exists) [1.5], `InspectionSource` [1.6], `ReferrersSource` (an object's referrers, each once, their
  fields named for up to 1,000 referrers) and `TypeReferrersSource` (a type's referrers by type, sortable) with
  `WorkerService.Referrers.cs` [2.1]; `ColumnSort` (sort by column name, ties by name, unknown
  columns refused) shared by the row sources [1.9]; `ObjectFormatter` [1.6]; `InspectionAvailability` [1.6]; `FeatureRules`, one rule per optional
  feature, filling the descriptor's availability list and refusing with the listed reason [3.8].
  `Snapshots/`: `SnapshotStore` (open, cache, memory mode); `SnapshotKinds` (per kind: the loader and its version, the
  snapshot over a graph, the kind's notes) [1.9], and whether the kind holds garbage, what an unreached object means
  and why its objects cannot be matched by address [3.8]; `DerivedData`, per-snapshot derived data on the snapshot's entry,
  dropped in one step when it closes: each slot is one shared computation with its own lease; a caller that cancels
  only stops waiting, and the computation is canceled when the last waiter leaves before it finishes and nobody joins
  within a 2 s grace period (so a grid's re-query joins it), or when the snapshot closes (`DerivedWaiter`, after the
  2.3 review M13) [1.3]; the reverse references slot [2.1] and the
  dominators slot with the retained lower bounds per grouping [2.3], with `SharedProgress` sending the computation's
  progress to every caller waiting for it (review M7); the two reachable sets and the dominator tree's group tables
  per grouping (`SnapshotStore.Reachability.cs`), which Analysis computes and no longer caches [3.8]. `ReverseIndexBuilder`: the reverse index opened from its sidecar,
  or built straight into it [2.3] (in managed arrays when there is none), and `ChooseMode` (review M3); `SnapshotStore.ReverseReferences.cs` gives it the snapshot's cache path,
  identity, fingerprint, memory mode and forced mode, and attaches the result to the graph [2.1].
  `SnapshotStore.RootPaths.cs` (the root path tree slot), `WorkerService.Retention.cs` and `RetentionMapping` (roots,
  fields, dependent-handle and framework marks on the wire) [2.2]. `SnapshotStore.Dominators.cs` (the dominators slot and
  the group retention per grouping), `DominatorIndexBuilder` (the dominators opened from their sidecar or computed into
  it, with the fallbacks) and `MemoryReservations` (the worker-wide memory check of the large derived computations,
  with `Refusal`'s next steps) and `WorkerService.Retained.cs`; `TypeSummarySource` and `InstancesSource` add a retained column when
  asked [2.3]. `WorkerService.Dominators.cs` (the tree and the icicle) with `DominatorTreeSource`, and
  `WorkerService.SetRetention.cs` with `RetainedBySetSource` [2.4]. `WorkerService.Sets.cs` (resolving an
  `ObjectSetSpec` into its members, logged with its size; a set's dominators with `SetDominatorsSource`), `ObjectSetCache`
  (resolved sets and the inspection tables their rows come from, least recently used out, dropped with their snapshot;
  owned by `SnapshotStore` since 3.8, which forgets a closed snapshot's entries and refuses entries for one no longer open),
  `InstancesSource.Select` (a type's objects in a scope) and the set on instances, type summaries and set retention [3.1].
  `WorkerService.RetentionViews.cs` (Similar Retention, a retention group resolved as a set, and each snapshot's
  interfaces per type, read once from the dump through one object per type and dropped with the snapshot), the two new
  set kinds in `WorkerService.Sets.cs`, a set's key paths in `QueryTypePathsAsync`, and interface grouping in summaries
  and diffs [3.2]. `WorkerService.Comparison.cs` (a diff's extras: objects matched when both snapshots are of one process,
  both snapshots' group retention, kinds; the `Compared` set kind resolved under both leases; trends) and
  `TypeTrendSource` [3.3]. The `Unreachable` set kind (`ObjectSet.Unreachable`, the complement of
  `Reachability.ComputeFromPathRoots`; refused for gcdumps and incomplete dumps) [3.5].
- **Application.** `Workers/` (`WorkerHost`, `IWorkerClient`, exceptions, log forwarding), `Jobs/JobTracker`,
  `Logging/FileLoggerProvider`, `WorkerOperations` [0.3]; `Snapshots/`: `SnapshotSession` (summaries, diffs with both
  snapshots reopened after a restart through `UseBothAsync`, instances, objects, inspections, referrers [2.1], retention paths [2.2], retained sizes [2.3]) and
  `ResultSetHandle` (section 4.6) [1.3 to 1.6]; `Sessions/`: `SessionLibrary`, `CaptureService` (per-kind capture settings in one table)
  [1.1, 1.9] and `TraceService` [1.5]; `Snapshots/GrowthThreshold` (the `--fail-on-growth` CI gate's limit, a size or a
  percent) [1.4]; `Monitoring/`: `HeapSample` (what front ends see), `ICounterSource`
  (`EventPipeCounterSource`, and `SyntheticCounterSource` for the smoke run), `SnapshotRules.cs` (`EveryRule`,
  `AboveRule`, `GrowthRule`, `RearmMode`, `MonitorRules`, `SnapshotRuleEngine` with the GC pressure flag, on
  `HeapSample.RuleBytes`, the heap after the last GC) and `MonitorService` with `MonitorRun` (samples and captures as
  events, one capture at a time, the counters timeline in the session folder) [1.7]. Since 3.4 a run is recorded in
  its session (`SessionRun`) at its start and end, takes a snapshot now (`CaptureNowAsync`) or forces a collection
  (`ForceGcAsync`, marked on the timeline), records an allocation trace for its whole length when asked, and
  `TimelineFile` reads a timeline back; `CaptureService` keeps one capture or forced collection per process at a time
  (`CaptureFailure.Busy`) and forces collections through Capture's `GcTrigger` [3.4]. A run awaits every capture and
  forced collection at its end, refuses new ones once stopping, marks its session in use, logs a rule deferred by a busy
  process and re-arms one that lost the race, and stops its allocation trace at `MonitorOptions.TraceLimitBytes` (1 GB)
  or under 1 GB of free disk (`TraceStopped`) [3.4 review]. Core's `IndexCachePaths` names a snapshot's index cache, its
  sidecar kinds and the writer's temporary files, for Storage and for deletion [3.4]. `ICaptureService.StartCapture`
  records a source (`SnapshotSource.Timed` for rules) and takes a `SnapshotKindDto` [1.7, 1.8]. `Exports/CsvExport`:
  a result set or in-memory rows as CSV, page by page, without hidden columns and, unless opted in, without value
  columns (then a first line says so), a formula guard on text cells, UTF-8 with a BOM; the CLI shares its quoting [2.7].
  `Bookmarks/BookmarkStore`: object bookmarks (`Bookmark`: snapshot key, address, type, `BookmarkColor`) in
  `bookmarks.json` beside the session library, keyed by `SnapshotKey` (full path, size, write time) [2.7].
  `Presentation/`, what the app and the CLI share [3.8, P18]: `ResultColumns`, `Cells` and `ColumnMap` (a result's
  cells read by column name), `RetainedSizeTexts`, `ObjectIdentityTexts` and `SnapshotScopeTexts` (one wording, numbers
  in the front end's culture), and `MonitorRulesParser` with a `MonitorRuleGrammar` per front end (the CLI's 90s, 5m,
  1h in the invariant culture; the app's bare minutes in the current culture), each field refused with its own message.
  `Sessions/SessionDocumentFile`: the file protocol of `session.json` (`SessionRead`, `SessionUnreadableException`)
  [3.6]; `Session.FileInside` and `Session.IsLocal` for paths a received session names [3.7]. `Snapshots/TrustedDacs`: the DACs trusted to load from next to a dump, by SHA-256, in `trusted-dacs.json` beside
  the session library or for one run (ADR-035) [3.6].
  `ObjectSetQuery` (a spec and the baseline snapshot of an inspection row, bound to the worker's current ids at each call
  by `SnapshotSession.UseWithSetAsync`) and the session's set queries (`QueryRetainedBySetAsync`, `QuerySetDominatorsAsync`,
  and a set on type summaries and instances) [3.1]; `QuerySetPathsAsync` and `QuerySimilarRetentionAsync` [3.2]; a diff's `matchObjects` and `includeRetained`,
  `QueryTypeTrendAsync` over any number of snapshots (`UseManyAsync`), and `ObjectSetQuery.Baseline` as a compared set's
  other snapshot [3.3].
- **UI.ViewModels** (sprint 2.3.6, moved from the WPF app; no UI toolkit). `Services/`: `IUiDispatcher` (post to the UI
  thread, `YieldToRenderAsync`) with `ImmediateUiDispatcher` for tests, `IUserInteraction` (asynchronous dialogs; opening a file or showing it in its folder since the timeline row) with
  `AutomaticUserInteraction` for smoke runs. `ViewModels/`: the shell, pages and jobs [0.3]; `GridQueryViewModel<TRow>`
  (the query lifecycle shared by the grids: debounced filter, latest query wins, column-name sort, first page preloaded
  before the grid binds, release) and `TypeGridViewModel<TRow>` (grouping) over the generic `VirtualRowList<TRow>` (data
  virtualization) [1.4, 1.5]; a grid yields to its `UiDispatcher`, set by whoever creates it, before binding rows
  [2.3.6]; `TimelineLayout` and `ChartPoint` (the Live chart's geometry) and `ColumnHeaders` [2.3.6];
  `TypeSummaryViewModel` (only the latest query owns the view: `Show`, a re-query or closing the snapshot cancel the
  running query and the pending filter, and the replaced row list stops its page requests) [1.3]; `CompareViewModel`
  (two pickers, the earlier capture as default baseline, open order when a capture time is only a file's time, swap)
  [1.4, 2.3.6]; `ProcessesViewModel` and `LiveViewModel` show `ICaptureService.UnavailableReason` instead of a list
  where capture is not available [2.3.6]; `InstancesViewModel`, `ObjectViewModel` (field tree
  and references) and `NavigatorViewModel` (types, instances, object; back and forward, mouse buttons and Alt+arrows;
  the views are built once with the window and the navigator swaps their data) [1.5]; the object's references as
  Outgoing and Incoming tabs (incoming loaded when its tab is first shown), the instances view's Referenced by, and
  `TypeReferrersViewModel` (a type's referrer types, whose rows open that type's objects) in `ReferrersViewModels.cs`
  [2.1]; the object view's Retention tab (key paths and the merged tree, framework runs folded unless shown) and the
  instances view's Why alive (`TypePathsViewModel`) in `RetentionViewModels.cs` [2.2]; the Retained sizes switch on the
  types and instances grids (off by default, carried from the types grid into a type's objects) and the object's
  retained size with its immediate dominator as a link in the Retention tab [2.3]; `InspectionsViewModel` (tiles,
  fragmentation bars, rows as `CellRowItem`s, the generic row of any result set since 2.4) [1.6]; `LiveViewModel` and
  `SnapshotCard` [1.7]; `Navigation/` (`NavigationTarget`, `INavigator`, `INavigationSubject`, the tab enums: every link
  of the app is a target the Snapshots page routes), `RetainersViewModel` (the dominator tree grid with
  `DominatorRowItem`, the trail as `TrailItem`s, the icicle with `IcicleGeometry`), `RetainedBySetViewModel`,
  `ColumnGroups`, and `GridQueryViewModel.OnRowsApplied` (what refers to new rows runs after they are bound) [2.4].
  Sprint 2.7: `Navigation/AnalysisPath` (a snapshot's steps as a tree, `AnalysisStep`) behind `NavigatorViewModel`,
  which keeps one path per open snapshot (`Show`, `Forget`), records Retainers links as steps (`Record`) and raises
  `StepShown` so the page picks the tab; `Exports/GridExport.cs` (`IGridExport`, which `VirtualRowList` implements, and
  `GridExportService`: save picker, the values question, the write off the dispatcher, the status and the log);
  `BookmarksViewModel` and `BookmarkItem`; `Shortcuts` (the one shortcut table) and the shell's `ShowPage`,
  `ShowSnapshotTab`, `ShowHelp` and `RefreshPage`; `InspectionsViewModel.RefreshBaselines`.
  Sprint 3.1: `SetViewModel` (an object set as a subject: its `SetViews`, the Types view as a `TypeSummaryViewModel` over
  the set, Instances as an `InstancesViewModel` over the set, Generations from the Types totals, Dominators as a
  `SetDominatorsViewModel`), `NavigationKind.Set` with `NavigationTarget.ObjectSet` and `SetInstances`, and the entry
  points: the types and Compare grids' selections and group rows, inspection and detector rows, the bookmarks and the
  retained objects (`RetainedBySetViewModel` takes any set).
  Sprint 3.2: `SetViews.Paths` (a `TypePathsViewModel` over the set) and `SetViews.SimilarRetention`
  (`SimilarRetentionViewModel` with `RetentionGroupItem`; a group opens as a set), `KeyPathDiagram` (key paths merged
  into a layered graph, toolkit independent), the Paths subject's diagram switch and Similar Retention command,
  `NavigationTarget.SetView` (the view a set opens on), and `TypeGridViewModel.GroupNote` for the interface grouping.
  Sprint 3.3: Compare's New and Dead, retained and per-generation columns (`ColumnGroups.Identity`), a row's new, dead
  or survived objects as sets, and `TrendViewModel` (the ticked open snapshots, oldest first, as a dynamic-column grid).
- **UI** (Avalonia, sprint 2.3.6, ADR-025). `Program` (the desktop app, or `--headless` on Avalonia's headless platform
  for CI smoke runs), `App` (host start and bounded stop, last-chance handlers, power throttling off on Windows, theme)
  and `UiHost` (DI, `UiOptions`). `Views/`: `MainWindow` (the shell, the attribution strip as `AttributionText`,
  ADR-022; back and forward by mouse buttons and Alt+arrows) and `Pages/`, one user control per page and per Snapshots
  tab, compiled bindings throughout; `LazyContentControl` builds a page when first shown and keeps it (switching pages
  is a visibility change) [1.5]; `MessageWindow` (Avalonia has no message box). `Controls/`: `GridBehaviors` (rows
  through `VirtualCollectionView`, header clicks sent to the view model's sort command with the arrow from its
  `SortState`, open by double-click or Enter, column groups shown from the view model, columns from a result set's
  list), `VirtualCollectionView<TRow>` (the pass-through `IDataGridCollectionView`: the grid reads rows by index and
  never copies or walks the list; whole-list walks are counted in `VirtualCollectionViews.Enumerations`, which the UI
  tests keep at zero), `HighlightedText`, `ProportionalPanel` (the heap kinds bar), `TimelineChart`, converters.
  `Services/`: `AvaloniaUiDispatcher`, `AvaloniaUserInteraction` (the system file picker), `DispatcherWatchdog`,
  `ThemeChooser`, `NativeMethods` (Windows only). `Themes/`: `Light.axaml` and `Dark.axaml` (theme dictionaries with
  the same keys) and `Styles.axaml`. `Smoke/SmokeRunner`: the unattended smoke runs [1.9, 2.3.6]. Sprint 2.4: the Snapshot tab's subjects are views of their own (`Views/Subjects/`), the
  Retainers tab (`RetainersTab`), `Controls/IcicleChart` (drawn in one pass, geometry from the view models),
  `Controls/NoteText` (a status or note line that takes no space when empty), and `GridBehaviors`' `VisibleColumnGroups`,
  `ExpandCommand`, `CollapseCommand` and `ScrollToSelection`. Sprint 2.7: `Controls/GridExports` (every grid's
  `ExportName`, its "Export to CSV..." item, the export of in-memory rows through the columns' bindings, and the grid
  Command+E targets), `ListBehaviors` (Enter and double-click on a list), `IndentConverter`; the window matches keys
  against `Shortcuts` and hands export requests to the shell; the Snapshots page's left column holds the analysis path
  and the bookmarks; the About page lists the shortcuts; filter boxes are named `FilterBox` (Command+F).
- **Cli.** `Program` calls `CliApp.RunAsync` (parse, then the host from `CliHost`: the worker attach pipe, symbol options;
  a usage error exits 64 with no host), so `HeapScope.Cli.Tests` runs the CLI in process [3.8, P10]. `CliCommands.cs`
  keeps the command table and exit codes; `CliRun.cs` the run boundary, which hands each command a `CliContext`
  (services, output, error output, logger); `CliOptions.cs` the shared options; `CliProgress.cs` the progress lines and
  the one open with progress; `CaptureCommands` (`list`, `capture`), `OpenCommand`, `SummaryCommand`, `WorkerCommands`
  (`worker`, `synthetic`). Tables read cells by column name (`ColumnMap`). `DiffCommand` [1.4],
  `InspectCommands` (`instances`, `object`) [1.5], `InspectionCommand` (`inspect`) [1.6], `TraceCommand` [1.5],
  `MonitorCommand` [1.7], `ReferrersCommand` (`referrers --address | --type`) [2.1], `PathsCommand` (`paths --address | --type`) [2.2], `--retained` on `summary`, `instances` and `object` with `StderrProgress` [2.3],
  `RetainersCommand` (`retainers`, `retained-by`) [2.4]; `CliCommands.Output.cs` holds the output every command shares: fetching rows, CSV, JSON (one
  writer, rows keyed by column name with typed numbers) [1.9].
- **perf/HeapScope.Benchmarks.** The budget benchmarks, the developer tool `trace-report` [1.5] and `phase2-indices`,
  the Phase 2 index prototype (section 6.4) [1.9]; `reverse-index`, the reverse references index of a real index cache
  (build, sidecar write, reopen in both modes) [2.1]; `retention`, the root path tree, key paths and object searches of
  a real index cache [2.2]; `dominators`, the reverse index and dominators as the worker computes them, in either mode
  [2.3].
- **Leak detectors [2.6, ADR-020 amended].** Analysis: `Inspections/LeakDetectors.cs`: `GrowingStaticCollectionsDetector`
  (a static field's collection counted by its own fields; growth since a baseline, or large without one),
  `EventHandlerLeaksDetector` (subscribers of a publisher type's event field, through the incoming references and
  invocation lists), `TimerLeaksDetector` (active `TimerQueueTimer`s by their callback's target, through
  `System.Timers.Timer`), `DisposedButReferencedDetector` (reachable objects whose disposed flag is set),
  `HttpClientInstancesDetector`, `UndisposedServiceScopesDetector`, and since 3.5 `PendingAsyncMethodsDetector` (with
  `AsyncMethods.MethodOf`), `FinalizerBacklogDetector` and `TypeGrowthDetector` (ADR-034); `IInspection.Kind`, `UsesBaseline`,
  `UsesReferrers`, `UsesReachable` [3.8]; `InspectionBaseline` and `InspectionContext.Referrers` and `.Reachable`;
  `Retention/Reachability` (a bit per object from the retaining roots other than the finalizer queue,
  `Reachability.Compute`; the worker walks it once per snapshot and supplies it [3.8]) and the
  shared `ChunkedStack`; `InspectionScopes.Reachable`, the scope a detector row opens (Worker: `InstancesSource`). Worker: `WorkerService.Inspections` leases the baseline and passes the
  reverse index. Application: `RunInspectionAsync(..., baseline)`. UI.ViewModels: the Inspections tab's detector group
  and `BaselineChoice`. Cli: `inspect --baseline`. Fixtures: `DetectorLeaks.cs` (`TimerLeak`, `DisposedReferencedLeak`,
  `CleanWorkload`), LeakyApi's `Leaks:Detected` and `Clean:Enabled`, LeakyConsole's `--detected`.
- **Allocation traces [2.5, ADR-028].** Capture: `TraceProfile.Allocations` (GC events at verbose with loader and JIT
  events; the rundown on stop). Core: `Allocations/AllocationTrace` (frames, a deduplicated stack tree, types, and per
  sample its time, innermost stack node, type and bytes; `NativeFrame`, `NoStackFrame`). Loaders.Trace:
  `AllocationTraceReader` (the `Nettrace` signature checked first, TraceEvent's `.etlx` written to a private folder
  (section 12) and deleted, runs of native frames merged, cancellation). Analysis: `Allocations/AllocationProfile` (sample
  ranges by binary search, the timeline, types, totals, top stacks, a stack's frames) and `AllocationTreeRows` (the call
  tree and back traces flattened from open keys like ADR-027, with `AllocationRow`, `AllocationTreeView`,
  `AllocationFilter`). Worker: `Traces/TraceStore` (open traces, a memory reservation while reading,
  `ReadReservation`) and `WorkerService.Allocations` with `AllocationTypesSource` and `AllocationTreeSource`. Application:
  `Allocations/AllocationSession` (`TraceHandle`, reading again after a worker restart, `AllocationScope`, and
  `StartRecord`, which records into the current session's folder and opens the trace). UI.ViewModels:
  `AllocationsViewModel` (the page), `AllocationTraceViewModel` (a trace: interval, type, stacks), `AllocationTypesViewModel`,
  `AllocationTreeViewModel`, `AllocationRowItem`, `AllocationTimelineGeometry`, `TimeInterval`; Record allocations and Stop
  on `ProcessesViewModel`. UI: `Views/Pages/AllocationsPage` and `Controls/AllocationTimeline` (drag selects the
  interval). Cli: `trace --profile allocations` and `AllocationsCommand` (`allocations`). Benchmarks: `allocations`.
- **Fixtures.** The leak fixtures and known answers (ADR-013), `WasteFixtures` (LeakyConsole `--waste`) and `Waste.json`
  [1.6].
- **build/.** The worker deployment targets [0.1]; since 3.6 the scripts CI and the local gate share: `gate.sh` (the
  full gate, bash, replacing 1.9's `gate.ps1`), `test.sh` (a test tier: `fast` or `integration`, projects found rather
  than listed, TRX results), `fixture-dump.sh` (start LeakyConsole, dump it), `publish.sh` (self-contained builds, 2.3.6),
  `collect-licenses.cs` (the license files of HeapScope and of every shipped package into the build) and
  `check-package.sh` (a build holds them, and not the unreviewed TraceEvent natives) (DEVELOPER.md 2 and 7).
