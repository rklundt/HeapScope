# HeapScope backlog

Everything not planned into a sprint, by priority, moved here from [SPRINTS.md](SPRINTS.md) in sprint 3.8. Each item
names where it came from. When a sprint builds an item, the sprint removes it here and lists it under "Built since
listed" with its sprint.

## Built since listed

Swept in sprint 3.8:
- Trend analysis across more than two snapshots: built in 3.3 (`trend`, the Trend view).
- One capture per target at a time: built in 3.4.
- Monitoring timelines in `session.json` and reopening a past run: built in 3.4.
- Diff growth by generation (`--growth-of`) and the per-row gate (`--fail-on-growth-per-row`): built in 3.3.
- Deleting snapshots and sessions with their caches, and cleaning up old sessions: built in 3.4.
- Retained bytes in snapshot diffs: built in 3.3 (`diff --retained`).
- A type growth detector across two snapshots: built in 3.5.

## Open

**High**
- SkiaSharp's native library without Adobe's DNG SDK (from the 3.8 owner actions, 2026-10-30; for sprint 3.9
  Packaging, before the first commercial release). The native SkiaSharp 3.119.4 library on Windows, Linux and macOS
  carries Skia's raw camera image decoder: Adobe's DNG SDK and Google's piex, which HeapScope never uses. The DNG SDK
  License (in SkiaSharp's `THIRD-PARTY-NOTICES.txt`, shipped under `licenses/`) asks, in section 5, anyone who
  distributes it in a commercial product to defend and indemnify Adobe. Either a legal review accepts that clause, or
  HeapScope builds libSkiaSharp itself from SkiaSharp's public build scripts with Skia's `skia_use_dng_sdk` and
  `skia_use_piex` off, per runtime identifier, ships it in place of the NuGet native assets, has
  `build/check-package.sh` refuse a build with DNG code in it, and rebuilds it on every SkiaSharp update Avalonia takes.
  Replacing Skia is not an option: Avalonia draws only through it.
- A contribution policy before any outside contribution is accepted (from ADR-022; 2026-10-30, the repository is
  public). A contribution stays its author's, licensed to HeapScope under the AGPL only, which would end commercial
  licensing of any file it touches. Until a contributor agreement exists (a CLA that grants the owner the right to
  license contributions commercially, or a copyright assignment; a lawyer's review), a `CONTRIBUTING.md` says outside
  contributions are not accepted, and no outside pull request is merged: code, tests or docs.
- Intermittent 105 to 130 ms dispatcher lag at job completion under heavy machine load (from 0.3): find the
  cause (likely CPU contention with the worker) and bring the smoke gate back to 100 ms. Capture a WPR or PerfView
  trace (CPU sampling, context switches, ready thread) of the smoke run and log the UI thread's CPU time per lag.
- Worker Server GC threads run at normal priority despite the below-normal process (from 0.3): evaluate a job
  object CPU rate, EcoQoS or GC heap affinity once a trace shows them preempting the UI.
- CI leak checks using the CLI (the remote, `develop` and the CI pipeline exist since 2.3 and 2.3.5; CI already runs the
  CLI on a fixture dump on Linux and macOS). Making CI a required check is the product owner's call (2.3.5 decision 8).
- Objects finalized between two snapshots: the baseline's finalizable objects whose finalizer has not run, matched by
  address (ADR-032) against the later dump (from 3.5's scope). By address it is an upper bound outside gen2, LOH and POH
  (moved objects count as dead), and it needs a bit per object of each snapshot: add a memory reservation callback to
  `InspectionContext` first (3.5 review M3). Plain `Task` objects that never complete, without an async method waiting
  on them (3.5 names the async methods).
- From the 3.5 review (decided autonomously): type growth with a column of old-generation (gen2, LOH, POH) growth, and
  required, so load alone cannot look like growth (M13); the finalizer queue counted into a bit set and a memory
  reservation for detector runs (M14); the finalizer thread's state read from the dump (its top frames) to back a
  backlog (M11); timings and memory of the three detectors on Medium in section 10 (M6, L8); finalizer backlog's own
  gcdump refusal text (L5).
- From the 3.6 review (decided autonomously): a planted-DAC test through a real worker (refusal, trusted open, a field
  read, a reopen after a restart), which needs a test-only worker switch for its installed runtime folders (M8); the
  private DAC copy folder from the worker's options, cancellable hashing with a size cap (L10); the symbol server tried
  before an untrusted DAC beside the dump is refused when downloads are on (L8); `VerifyDacOnWindows` set explicitly and
  tested with an unsigned DAC (L12); the image match repeated on the private copy (L13); pruning private copies whose hash
  is no longer trusted (L9); the gate reading counts and failed names from the TRX files (L5); the cost of the DAC check
  on every full-dump cache open measured on Medium, and skipped when the cache's recorded DAC still comes from a trusted
  folder (re-review); an option for the trust file's folder, so front ends started by tests keep it in their own root
  (L30).
- From the 3.7 review (decided autonomously): a cache next to the dump that is untrusted or cannot be replaced written to
  and read from the user's cache folder instead (M4); checks behind the checksums on cache open (monotonic edge starts,
  index ranges, sorted addresses, sidecars; M9); gcdump counts checked against the bytes left with each COM record's
  real minimum (M8); locked restores for publishes and release builds, runtime identifiers for the CLI and the worker, a
  fixed SDK feature band, a lock file drift check after publish (M12); one ownership check for HeapScope's data folders
  with a next step (M3); SBOMs per runtime identifier from the published output (M7); tests: a cache fuzz mode that
  recomputes checksums and signs, an untrusted sidecar per kind, deterministic gcdump count tests with an allocation
  bound, a builder error through `Load` and the worker's error path, the capture's not-private warning reaching
  `CaptureResult`, the budget tripped by the root walk alone (M10, Lows); the root walk queuing a pseudo node once
  (gc-specialist Low); monitoring cards showing a capture's warning (support Low).

**Medium**
- From 3.8 (decided autonomously): the Live page view model split into live monitoring, past runs and timeline actions
  with a shared chart state (peer review P11, its view model half); an analysis workspace per snapshot, extracted from
  the Snapshots page, with a design for the worker's result sets of hidden snapshots (P21, Phase 4); the Object view,
  the inspections page, the allocation trace page and the icicle loading through the query runner (P8's second wave);
  `OfTypeKeys` and `TypeGroups.MatchTypes` as one matcher (S5, both normalize the module since 3.8).
- From the 3.8 review (decided autonomously): view models reading cells by column name and refusing a missing required
  column, as the CLI does (M6); CLI refusals written through a `CliContext` helper that logs which check refused (M10);
  `SnapshotHandle`'s descriptor and generation swapped as one record; the grid base's load through `QueryRunner`; the
  inspections page and page reads logging their refusals, with the error code on "shown as" lines; the Unreachable
  objects button disabled with its reason rather than hidden; the large object heap scope gated by generations; the
  `ConcurrentDictionary` field names in `RuntimeLayouts`; the frozen scan's span hoisted and the type growth fill
  without per-member hashing; `ObjectSetCache.Evicted` raised outside its lock; the derived slots' linked cancellation
  sources disposed; the `recorded` and `attach` callbacks guarded; a per-user, per-run test data folder; the contract,
  static state and doc link tests tightened; the type summaries and touched files brought to the S8 and S9 rules; a
  separate-process live tail with stack folding measured (ADR-037's gate for Phase 4).
- From the 3.4 review (decided autonomously): `session.json` runs read one by one, an unreadable run kept as its JSON
  so the session still opens and nothing is lost on save, with the rule that new run members are optional (M8); a seam
  in front of `DumpCapturer` so the real per-process capture gate is tested deterministically (one pid refused, two pids
  together, released on failure and cancel) (M13); serializing the Allocations page's recording with a gcdump of the
  same process, with an integration test counting induced collections across a gcdump and a trace's start and stop on
  .NET 8 and 10 (M5); the allocation trace's size per minute and the target's slowdown measured on LeakyApi and a
  high-allocation target, added to section 10 (L17); a reopened run downsampled over its whole length so every marker
  shows, read as a stream (L1).
- From the 3.3 review (decided autonomously): the address match kept per snapshot pair with the snapshots (and its
  reservation held as long), and a Medium measurement of a matched and retained diff against the budgets (M12); the CLI
  trend aggregating each dump and closing it before the next, measured with 20 dumps (M13); the other snapshot bound per
  spec node, so an inspection row inside a compared set keeps its own baseline (M9); a type's retained bound per key,
  not summed per method table (M10, L20); a Frozen delta column (L1); `ObjectChange` in the contract mirror tests (L11);
  the New and Dead switch disabled from the descriptors with the reason (L13); the Trend view's interface grouping and
  group note (L14); CLI validator refusals logged (L15); ADR-029 reworded (L12); a `TypeTrendRequest` round trip (L19);
  process start time compared when a dump records it.
- From the 3.2 review (decided autonomously): which grouping needs which snapshot feature declared in Contracts, so
  every front end disables it the same way (M6); the reverse index info on `SimilarRetentionResult` (M12); a test that a
  weak-handle-only referrer is not a retainer (M8); a reservation around `KeyPaths.ForTypes` and `ForMembers` (from 2.2);
  interfaces kept in a sidecar so a reopen does not read them again (L16); a dependent-handle marker on path steps (L1);
  interface rows keyed by name and module (L3); the diagram cached, its back edges inside its bounds and redrawn on a
  theme change (L13, L14); a request with both a type and a set refused (L15); the `MaxReferrers` constants tied by a
  test (L12); diagram toggles logged (L5); from the re-review: an exact back pointer test (the member dominates the
  referrer, or entry and exit numbers over the shortest-path tree) instead of the 256-step climb (M13); module metadata
  released after the interface table is built (L20); the (group, type) set in the reservation (L22); a cheap check
  before fingerprinting every pattern (L23); Compare's matching total hidden for interface rows (L24).
- From the 3.1 review (decided autonomously): the memory reservations evicting the object set cache before they
  refuse (M2); the baseline of a set generalized into "the other snapshot" for 3.3's dead objects and 3.5's growth
  (M6, do with 3.3); a view model test of a set replayed after a worker restart, a ClrMD check of a set's Generations
  and Dominators views, and a test that a set's worker lines carry the UI's trace id (M15); the set dominators of a few
  objects by climbing their dominators instead of walking the tree, and one retention pass shared by the Retained view
  and "open as set" (L2); a set's frozen members said apart in its Retained view (L3); the CLI logging `--address` like
  the worker (L12); an `IOpensAsSet` interface for the shortcut, one type key builder, and one session query helper (L18); from the re-review:
  a concurrency test of the shared set resolution and a worker test of `MaxTypes + 1` (M15); keeping only the mask of a set
  too large to cache (L20); an event row's bytes counting a shared target once (L21); the cache add and a snapshot's
  close made one step (L22); progress and a log line for a query that joins a running resolution (L23); exact arrays
  for the detectors' member lists (L24); the path step id in the set's log line (L25).
- From the 2.3.5 review (decided autonomously, deferred; the Compare baseline and the Windows CI dump step were done in
  2.3.6):
  - Unix file semantics stated by tests: replacing or deleting a mapped index cache on Linux and macOS succeeds and
    the open graph keeps reading the old file; the Windows-only tests skip there today. Capture.Tests join the Linux CI
    job with the Linux capture item.
- gcdump survivors without a recorded root as path and dominator sources (from the 2.3 review M6; agreed with the
  product owner on 2026-10-25 to defer the fix). Today they retain nothing, and the note beside retained sizes gives their
  count and bytes. The fix marks them "root not recorded" sources in `PathRoots.Sources` (so paths and dominators keep
  agreeing, ADR-024), bumps `PathRoots.SemanticsVersion` so saved dominators are rebuilt, and needs a product decision on
  how such roots are shown in paths. **Revisit when:** the "gcdumps at scale" item below is scheduled (do them together:
  sampled gcdumps are where these survivors appear), or as soon as a real gcdump shows a note with more than a few MB.
- Confirm the mapped dominators on a real 16 GB machine (from the 2.3 review M14; a capped run on 2026-10-25 showed no
  thrashing, see ARCHITECTURE.md 6.4, but trimmed pages there came back as soft faults, not disk reads). **Revisit
  when:** a 16 GB machine is available, or before ADR-004's 16 GB baseline is claimed for Phase 2 in a release.
- Reconsider a live in-memory graph's memory mode when an optional index is computed (from 2.3 decision 4, the 1.9
  decision): today dominators that do not fit next to an in-memory graph are refused with a suggestion to reopen
  mapped; switching the open snapshot to mapped (graph and indices) would compute them without asking.
- gcdumps at scale (from 1.8, ADR-023 in 1.9, amended by ADR-037 in 3.8: capture stays in the front end until a
  measurement says otherwise): measure moving gcdump capture into the worker (the capturing front end peaked at
  1,191 MB near the 10 million node cap), then raise the cap (a vendored change) after measuring a gcdump capture and
  load of the Medium dump against the budgets (section 10). Do the "gcdump survivors without a recorded root" item
  above with it.
- gcdump lost-events check (from 1.8): count the EventPipe session's lost events and fail or mark the capture.
- gcdump loader memory (from 1.8 review): presize from `NodeIndexLimit`, add references by index rather than address,
  release the MemoryGraph before the build.
- Generations for gcdumps (from 1.8): place objects by the GC's generation ranges when a writer records them.
- A warning when an expected runtime counter never arrives (1.7 M11).
- A `System.Runtime` Meter counter source for .NET 9 and later targets (`dotnet.gc.last_collection.heap.size`,
  `dotnet.gc.pause.time`), EventCounters kept for .NET 6 to 8 (1.7 M12).
- Inspection results kept per snapshot (compact rows) so an evicted set re-query rebuilds only the result set, with
  progress shown (from the 1.6 review M3).
- Sparse arrays: count dependent-handle edges apart from fields; a fixture array that is a weak table key (1.6 M12).
- Pinning: expand async pinned handles to their user buffers; overlapped I/O and stack pin fixtures (1.6 M13, M14).
- Collection inspections: field offsets resolved once per type, bulk reads under one lock, shapes shared by the empty
  and oversized inspections; a single string larger than the reader's lock budget read in several acquisitions (1.6
  M15, M18).
- A types row opens only its own method table's objects (from the 1.5 review M3): a hidden snapshot-local type index
  on summary rows and an optional index on `InstancesRequest`; diff rows keep the name and module key.
- Slow the dispatcher watchdog's 25 ms probe while the window is idle or in the background outside smoke runs (from
  the 1.5 review M7, ADR-019).
- Before or in 1.4 (from the 1.3 review): a generic `VirtualRowList` over a row factory for the diff's rows; a sort key
  that covers delta columns (per-view enum or a column name validated by the worker); a hidden row key mapping a
  summary row to its type indices, for opening a row's objects.
- The type summary's sort as a column name like the diff's (1.4), retiring `TypeSummarySort`.
- Capture with an induced full GC first (for CI gates), where the target allows it (from 1.4).
- A diff on the 16 GB profile with neither index cache present: the second first load next to the first snapshot,
  peak and chosen modes recorded (from 1.4).
- View model tests on a single-threaded synchronization context, and a `TimeProvider` for the filter debounce
  (from 1.3).
- Capturing service targets whose account cannot write the user's session folder: write into a folder that grants the
  target's account write access, then move the dump into the session (from 1.1; today the error names the cause and
  suggests `--out`).
- Process list details for single-file apps (runtime version from the diagnostics IPC) and x86 or NativeAOT targets
  marked as not supported before capture (from 1.1).
- A LeakyConsole fixture with a collectible `AssemblyLoadContext` holding statics, to test that collectible statics
  are recorded without `Retaining` (and whether .NET 9 and 10 also report them through the static storage root) (from
  1.2).
- Per-object generation assertions in the known answers (an object promoted to gen2, a fresh gen0 object) (from 1.2).
- A configurable memory budget fraction (`Worker:MemoryBudget`) (from 1.2).
- Heap completeness summed over all segments too, so a truncation that removes many small regions is caught; a
  truncation test cut point that keeps the runtime structures and asserts incomplete segments (from 1.2).
- Antivirus scanning of a freshly written index cache, and the 1.2 numbers on a 16 GB machine if available (from
  0.5; the cold page cache reopen was measured in 1.2: mapped 1.0 s, in memory 0.8 s).
- Log when a snapshot is disposed while leases are outstanding, so a leaked lease is visible (from 0.5).
- Storage-owned metadata DTO instead of serializing Core's `SnapshotInfo` directly, so Core can evolve without
  silently changing the cache format (from 0.5).
- Large scale tier (8 GB+, 100M+ objects): 64-bit edge offsets, chunked disk-backed building.
- Fragmentation and pinned object analysis.
- String content search and object set queries.
- Shared analysis export (ADR-026, from 2.3.5; replaces "HTML report export"): one export model, analysis only by
  default, written as HTML, JSON or CSV where the user asks; opening a JSON export in HeapScope without the dump. Sprint
  2.7 wrote the first form, a grid as CSV; the export of a whole analysis (several views, the snapshot's metadata) and
  JSON and HTML remain.
- Analysis tabs (from 2.7, decided autonomously): several analyses of a snapshot side by side, each with its own path;
  the branching analysis path covers coming back to an earlier analysis for now.
- From the 2.7 review (decided autonomously): a column projection on page requests, so an export without values never
  reads or sends them (M2, a protocol change); releasing the worker result sets of snapshots not shown, and measuring
  front end and worker memory with three Medium snapshots open, each with a full analysis path, and trimming the row
  lists of subjects not shown (M3); export marks for a gcdump's sampled counts (L17).
- Capture on Linux and macOS (from 2.3.5, ADR-025): process discovery through `/proc` and the diagnostics client, full
  dumps through the runtime's createdump (the diagnostics client's `WriteDump`), gcdumps through the vendored code;
  elevation and ptrace rules per system. Until then capture refuses there and points to `dotnet-dump`.
- Linux and macOS packaging (from 2.3.5; the self-contained builds per runtime identifier, arm64 for Apple silicon, came in
  2.3.6): a macOS app bundle with signing and notarization (needs an Apple developer account, a paid one), Linux
  packages (a zipped folder from CI today; .deb, .rpm or Flatpak to decide), smaller builds (the app and the worker each carry the
  runtime: about 356 MB for win-x64 with ReadyToRun).
- .NET Framework target support.

- Set retention of a namespace or an assembly (from 2.4): "objects retained by this set" takes one type; a scope
  of several types is the same pass with more types marked.
- Sets narrower than a type (from the 2.4 review M14): "objects retained by this set" for an inspection's scoped row
  (the empty collections, one duplicate string's copies); it is off for those rows until then.
- Place a type's objects in the dominator tree (from the 2.4 review M1): "Show in dominator tree" from a type row opens
  its top-level group, which a leak's item type rarely has; group its objects by their top-level dominator instead.
- `ValueBound.Approximate` and caveat codes (from the 2.4 review M3, an ADR-024 amendment): a result on an incomplete
  graph or a gcdump says its retained values may be overstated through the column, not only in free text.
- One type entry among several with one name and module (from the 2.4 review M12): the set and the tree's Type grouping
  take every entry of the name; naming one entry (an unloaded `AssemblyLoadContext`'s stale copy) is an additive field.
- Reveal an icicle bar's row by opening its dominator path (from the 2.4 review M13).
- The navigator's history at the Snapshots page level, keyed by `NavigationTarget`, so Back returns from the Retainers
  tab (from the 2.4 review L14; with 2.7's history).
- Extract the type grid toolbar (2.3.7 M14 rest, from 2.4): group by, filter and totals are repeated on the types
  grid and the Compare tab; extract it when a third grid needs it.
- Typed fake rows for the older fakes (2.3.7 M15 rest, from 2.4): the type, instance and reference fakes still take
  positional rows; the 2.4 fakes are typed.

- From 2.5 (decided autonomously at /start-sprint and while building): Compare's "allocated between the two
  snapshots" from a trace recorded between them, which needs traces in `session.json` on the snapshots' clock (with the
  1.7 timeline item), and Record allocations on the Live page while monitoring; .NET 10's randomized allocation sampling
  as a second source (better statistics on runtimes that have it); a streaming trace reader for long, busy traces (TraceEvent's
  conversion cannot be canceled and holds about 2 bytes per file byte while it runs; 72 MB read in 1.9 s);
  choosing a timeline interval from the keyboard (2.7's keyboard work). Compare's link also needs each tick's type id
  and module (types are joined to diff rows by name and module; a trace keeps names only, ADR-028).
- From the 2.5 review (decided autonomously): extract one flattened-tree builder (children order, batches, "more"
  rows, the row limit) and one set of tree keys and limits in Contracts from the dominator and allocation trees when a
  third tree arrives (2.6 or Compare's link), with their view models' shared parts (M5); progress on the timeline,
  types and stacks queries (a protocol change; milliseconds on the traces measured) (M4); a tick's type id and module
  for two types of one name (M6); the smoke step's interval and type checks (L17); the client logging which answers it
  applied (L27).

- From 2.6 (decided autonomously): WPF leak detectors (binding to a non-INotifyPropertyChanged source, collection
  bindings, dependency property descriptors, x:Name) with a WPF fixture app; leak detectors on gcdumps where the graph
  alone suffices (timers and events need field values); measuring the detectors on a Medium-sized dump of the current runtime
  (the sample dumps need a runtime no longer installed).
- From the 2.6 review (decided autonomously): the growth detector counting `ConcurrentQueue` (its segments),
  `ConcurrentBag`, `ConcurrentStack`, `LinkedList`, sorted collections and `Collection<T>`, with one collection layout
  table shared with the oversized collections inspection and `InspectionRows.ByType` reused (M5, M17); leaky HttpClient
  and undisposed scope fixtures with known answers (M9); leaving out scopes a request or a Blazor circuit holds, and a
  row for the root scope's tracked disposables (M7); unit tests for growth exactly at the thresholds and a field missing
  from the baseline (M10); an `InspectionNeeds` description with a cached derived-data accessor instead of one flag per
  input (M16); the rule version and parameters on `InspectionInfo` and Kind, elapsed time and baseline in the CLI
  summary output (L6, L7); detector rows opening exactly the objects they counted, with the object set queries (M24).

**Low**
- UI cold start: first window render about 0.6 s after ReadyToRun (1.2), dominated by runtime and WPF start; first
  job row 130 to 190 ms once per run; first showing of the Compare tab with rows 97 to 238 ms (1.4). Template preloading and deferring startup work remain options (from 0.3).
- WPF and WinForms specific leak inspections.
- Unmanaged/native memory overview.
- Reading a dump on another system or architecture than the one that took it (cross-system DACs), and a Linux arm64
  worker (from the 2.3.5 review; ADR-025: a dump is read by a worker of its own system and architecture).
- Attach to processes in containers.
