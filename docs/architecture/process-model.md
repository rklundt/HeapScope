# HeapScope architecture: Process model and IPC

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 4" still name the right section.

## 4. Process model and IPC

### 4.1 Processes
- **UI process** (Avalonia; AnyCPU, so 64-bit on x64 and arm64): shell, view models, Application services, capture.
- **CLI process** (AnyCPU, 64-bit): same Application services, console output.
- **Worker process** (64-bit, the machine's architecture; `HeapScope.Worker`): one per UI or CLI instance. Holds every loaded snapshot
  of the session (a diff needs both snapshots in one address space). Uses Server GC, non-concurrent
  (section 9), and runs at below-normal process priority so its ordinary threads yield to the UI and the profiled
  application. This does not cover Server GC threads, which the runtime creates at highest thread priority (they
  end up at normal priority in a below-normal process); taming GC threads (job object CPU rate, EcoQoS, heap
  affinity) is a backlog item to be decided with a trace.

### 4.2 Lifecycle
1. Application's `WorkerHost` starts the worker with `--pipe <name> --parent-pid <pid>`. The pipe name is
   `HeapScope.W.<24 random hex digits>` (sprint 2.3.5; off Windows a pipe is a Unix domain socket in the temp folder,
   whose path macOS limits to 104 characters); the pipe is created with `PipeOptions.CurrentUserOnly`. The worker's
   executable is `HeapScope.Worker.exe` on Windows and `HeapScope.Worker` elsewhere.
2. The worker creates the pipe server and waits up to 30 s for the client; the client connects with a
   timeout and calls `GetInfoAsync` as a handshake, which also checks the protocol version.
3. The worker exits when the pipe disconnects or when the parent process exits (it watches the parent's
   process handle), so a crashed UI never leaves orphaned workers holding gigabytes.
4. On orderly shutdown (the host stopping, since `WorkerHost` is a hosted service) the client calls
   `ShutdownAsync`, then waits up to 5 s before killing the process.
5. For debugging, a worker can be started by hand with `--pipe <name>` and a client attached to it with the
   `Worker:AttachPipeName` option (CLI: `--pipe <name>`); an attached worker is disconnected, not shut down.
6. Test hooks (`CrashForTestingAsync`, `HangForTestingAsync`) exist only when the worker is started with
   `--enable-test-hooks` (`Worker:EnableTestHooks`); otherwise they fail with `TestHooksDisabled`.

### 4.3 Crash handling
- The client watches both `Process.Exited` and `JsonRpc.Disconnected`. Either one moves the worker to
  `Crashed`, faults every in-flight call with `WorkerCrashedException` (exit code included), and raises a
  `WorkerStateChanged` event the UI shows as a banner.
- The next request restarts the worker transparently. Every worker has an `InstanceId` (in `WorkerInfo`) and
  `IWorkerClient.Generation` increases with each connected worker, so callers can tell that worker-owned ids
  (result sets, snapshots) belong to a dead worker. Application's `SnapshotSession` owns a stable `SnapshotHandle` per
  opened file (keyed by the path that was opened, never by the path recorded in the cache), maps it to the worker's
  snapshot id for one worker generation and reopens it lazily (`UseAsync`: before a call when the generation changed,
  and once more when the worker answers `SnapshotNotLoaded`); a stale id fails with `SnapshotNotLoaded` (sprint 1.2).
  A reopen asks for the index cache only (`OpenSnapshotRequest.RequireCache`) and keeps the mode the user chose: it
  is cheap and silent, never an unrequested dump load, and a changed or uncached file fails with `SnapshotChanged` so
  the user opens it again. A closed handle is never reopened; a reopen that finishes after a close closes its worker
  snapshot again.
- Error contract of `IWorkerClient`: calls fail only with `WorkerStartException`, `WorkerCrashedException`,
  `WorkerOperationException` or `OperationCanceledException`. A stopped or disposed host refuses to start a
  worker. The handshake is bounded by the connect timeout. In the worker, `WorkerErrors.GuardAsync` maps domain
  exceptions to codes in one place (`CapabilityNotSupported`, `InsufficientMemory`, `InvalidArgument`,
  `RuntimeComponentMissing`, `SnapshotFormat`, `SnapshotUnavailable` for I/O and access failures with a next step,
  `SnapshotChanged`, and since 3.6 `DacNotTrusted` with its error data) and logs every outcome once with the operation and the request (section 11): an answer with what
  was answered, an expected refusal (capability, invalid argument, changed file, unknown snapshot or result set, missing
  or untrusted DAC, memory) at Information without a stack, other coded failures as warnings with their stack, unexpected ones as
  errors; errors the worker creates as codes are logged too (sprint 2.3.7). The client's message stays plain and the
  log keeps the details. The front ends catch `WorkerException`, the base of the three worker exception types. The UI
  adds last-chance handlers (dispatcher, app domain, unobserved tasks) that log and show a plain message; the
  CLI command boundary prints a plain message and logs the stack trace.
- Worker stderr is captured by the client and forwarded into the client's log (category `Worker`), so the
  last log lines before a crash are never lost. The worker writes single-line records
  (`HH:mm:ss.fff level: Category[id] => scopes message`); the forwarder prefixes `w<pid> g<generation>`, keeps the level
  and logs lines without one (for example a stack trace) as warnings. A worker that exits logs a last-chance line with
  its peak working set; the client explains known exit codes in the banner and the log (137: killed, usually by the
  Linux out-of-memory killer; an unhandled .NET exception; a stack overflow; sprint 2.3.7).
- Transport failures (`ConnectionLostException`, broken pipe, disposed connection) become
  `WorkerCrashedException`; worker errors become `WorkerOperationException` with the `HeapScopeErrorCode`;
  startup failures (missing executable, no pipe within 30 s, exit during startup) become `WorkerStartException`.

### 4.4 Transport and contracts
- StreamJsonRpc over the named pipe, with `HeaderDelimitedMessageHandler` and the default JSON formatter.
  The default formatter is used because it supports `IProgress<T>` and `CancellationToken` marshaling with no
  extra configuration. The formatter can be swapped later without changing service interfaces.
- `HeapScope.Contracts` defines the worker's contracts, one per feature area since sprint 3.8 (peer review P5):
  `IWorkerControl`, `IWorkerTestHooks`, `ISnapshotService`, `IResultSetService`, `IHeapQueryService`,
  `IRetentionService`, `IInspectionService` and `IAllocationTraceService`, listed in `WorkerContracts.All`. The worker
  serves all of them on its one JSON-RPC connection, each by its own handler; JSON-RPC carries only method names, so the
  split changed nothing on the wire, and a test keeps method names unique across contracts and every method
  cancellable. `IWorkerService` aggregates them for clients. The contracts and the DTOs: DTOs are records of
  primitive types, strings, arrays and Contracts enums; Contracts references no solution project, so no Core type
  can cross the pipe (ADR-016). The worker maps Core values (for example `SnapshotCapabilities`) to their
  Contracts counterparts (`SnapshotFeatures`); a test keeps the numeric values of mirrored enums identical.
- `WorkerProtocol.Version` is a constant in Contracts; a mismatch at handshake is a hard error with a clear
  message. Version 2 (sprint 1.2) added `ProbeSnapshotAsync` (file size, index cache state from its header and
  fingerprint, open or not), `OpenSnapshotAsync`, `CloseSnapshotAsync`, `GetTypeSummaryAsync`, the snapshot DTOs
  (`OpenSnapshotRequest` with `RequireCache`, `SnapshotDescriptor`, `SnapshotMemoryMode`, `SnapshotProbe`,
  `SnapshotCacheState`) and the codes `RuntimeComponentMissing`, `SnapshotUnavailable` and `SnapshotChanged`.
  Version 3 (sprint 1.3) added `QueryTypeSummaryAsync` (`TypeSummaryRequest`: group by, filter, sort and
  direction, validated; `TypeSummaryResult`: a result set, totals per heap kind (Unknown listed only when present),
  whether generations exist, and the rows before the filter), the wire enums `TypeGrouping`, `TypeSummarySort` and
  `HeapKindDto` (mirrors `HeapKind`), the column kinds `Percent`, `Address` and `SignedInteger`, and the key flag on
  `ResultColumn`. `NameFilter` lives in Contracts because it defines what the `Filter` field means to worker and UI
  alike; changing it changes the protocol's meaning.
  Version 4 (sprint 1.4) added `QueryTypeDiffAsync` (`TypeDiffRequest` with a sort column named by
  `TypeDiffColumns`, validated by the worker; `TypeDiffResult` with both sides' totals, new and gone row counts, the
  matching rows' baseline bytes and delta, and `ExactCounts`).
  Version 5 (sprint 1.5) added `QueryInstancesAsync` (`InstancesRequest`: snapshot, type name, module file name,
  filter, sort column; `InstancesResult`), `GetObjectAsync` (`ObjectDetails` with `ObjectFieldDto` trees or the
  capability explanation), `QueryReferencesAsync` (`ReferenceColumns`), the hidden key columns `TypeName` and `Module`
  on summary and diff rows, and the type summary's sort by column name (`TypeSummaryColumns`; `TypeSummarySort`
  retired).
  Version 6 (sprint 1.6) added `ListInspectionsAsync` (`InspectionInfo` with the reason an inspection is unavailable)
  and `RunInspectionAsync` (`InspectionRequest`; `InspectionResultDto` with count, bytes, wasted bytes, a note and the
  rows; hidden `InspectionColumns` say what a row opens), `InstanceScope` and `SameValueAs` on `InstancesRequest`
  (all, large object heap, finalizable, strings equal to one string), and progress on `GetObjectAsync` and
  `QueryReferencesAsync`.
  Version 7 (sprint 2.1) added `QueryReferrersAsync` (`ReferrersRequest`; `ReferrersResult` with the reference count,
  why fields are not named, and `ReverseIndexInfo`; `ReferrerColumns`), `QueryTypeReferrersAsync`
  (`TypeReferrersRequest` with a sort column named by `TypeReferrerColumns`; `TypeReferrersResult` with the matched
  types, their objects and the references into them; no index when no type matches) and
  `SnapshotDescriptor.CacheIdentity`. Both results say whether the graph is complete (`CompleteGraph`), since a sampled
  gcdump or a truncated dump can miss referrers.
  Version 8 (sprint 2.2) added `QueryObjectPathsAsync` (`ObjectPathsRequest` with up to 50 roots; `ObjectPathsResult`:
  the merged tree as `RetentionNodeDto`s with fields, dependent-handle and framework marks and `PathRootDto`s in plain
  language, the roots, the key paths among them, whether the search stopped early) and `QueryTypePathsAsync`
  (`TypePathsRequest` with up to 100 paths; `TypePathsResult`: `KeyPathDto`s with objects, bytes and an example, the
  unreachable objects, the paths beyond the limit summed), and `RootKindDto` (mirrors `RootKind`). After the 2.2 review:
  `RetentionNodeDto.Folded` (objects of one type folded into a node, or hidden behind a gap node),
  `ObjectPathsResult.Truncated`, and `UnreachableNote` on both results (what unreachable means for the snapshot's
  kind).
  Version 9 (sprint 2.3) added `QueryRetainedAsync` (`RetainedRequest`; `RetainedResult`: the object's retained bytes,
  its immediate dominator's address and type or that only the roots dominate it, the unreachable note,
  `DominatorIndexInfo` and `CompleteGraph`), `IncludeRetained` on `TypeSummaryRequest` and `InstancesRequest` (a
  `Retained` column; a type summary row's value is a lower bound, ADR-024) and `Dominators` (a `DominatorIndexInfo`) on
  both results. The first retained query of a snapshot computes its dominators with progress; dominators that would not
  fit the worker's budget fail with `InsufficientMemory`.
  Version 10 (sprint 2.3.6) added `CaptureTimeFromFile` at the end of `SnapshotDescriptor`: the capture time is the
  file's write time (Linux and macOS core dumps record none), so Compare does not order snapshots by it.
  Version 11 (sprint 2.3.7) added `FilterNote` at the end of `InstancesResult` (what a value filter could not test:
  unreadable strings, regular expression timeouts); both ends set StreamJsonRpc's `ActivityTracingStrategy`, so each
  request carries the client's W3C trace context and the worker's log lines carry its trace id.
  Version 12 (sprint 2.4) added `QueryDominatorTreeAsync` (`DominatorTreeRequest`: expanded row keys, grouping, focus
  address, children per node, the key of the row to reveal; `DominatorTreeResult`: the rows as a result set in
  `DominatorTreeColumns` (kinds in `DominatorRowKinds`, keys formed by `DominatorTreeKeys`), the focus key and its
  ancestors (at most the top 3 and the nearest 20, with `AncestorDepth` counting all), the focus's and the reachable
  heap's bytes, the top-level count, whether the rows were cut, `DominatorIndexInfo`, and `RevealIndex`; limits in
  `DominatorTreeLimits`), `QueryIcicleAsync` (`IcicleRequest`: focus, levels, minimum share; `IcicleResult`: segments with
  level, offset, width, key, type and address) and `QueryRetainedBySetAsync` (`RetainedBySetRequest`: type name and
  module; `RetainedBySetResult`: the retained types as a result set, the set's objects and bytes, what it retains
  exactly, the group's lower bound for comparison, the reachable heap's bytes, the seconds taken, and `AllObjects`, the
  members no root reaches included; an unknown type is refused), and
  `ResultColumn.Bound` (`ValueBound.Exact` or `LowerBound`; the type summary's retained column is a lower bound, every
  other retained column exact), whose texts are in `ValueBoundTexts` (with the set action's one name and how the
  dominators were obtained) so the CLI and the UI say it the same way.
  Version 13 (sprint 2.5) added allocation traces (ADR-028): `OpenTraceAsync` (`OpenTraceRequest`, `TraceDescriptor`:
  process, start, duration, samples, sampled bytes, types, lost events, samples without a stack, read time),
  `CloseTraceAsync`, `QueryAllocationTimelineAsync` (sampled bytes per stretch), `QueryAllocationTypesAsync` (a result set
  in `AllocationTypeColumns`), `QueryAllocationStacksAsync` (the top stacks with their frames, the allocating method
  first) and `QueryAllocationTreeAsync` (`AllocationTreeRequest`: call tree or back traces, open keys, interval, type,
  children per row, the row to reveal; rows in `AllocationTreeColumns`, kinds in `AllocationRowKinds`, keys formed as
  `AllocationTreeKeys` describes, limits in `AllocationTreeLimits`). Intervals are seconds since the trace started; no
  end means its end. `ValueBound.Estimate` marks sampled bytes (texts in `AllocationTexts`), and
  `HeapScopeErrorCode.TraceNotLoaded` (1012) a trace the worker no longer has; a file that is not a trace, or a trace
  without allocation samples, is `SnapshotFormat`. After the 2.5 review: `TraceDescriptor` also carries the file's size
  and write time (its identity; a re-read of a changed file is refused with `TraceChanged`, 1013), samples with
  unresolved frames, whether the rundown arrived, and TraceEvent's conversion problems (`AllocationTexts.NotesFor` says
  what they mean); the types answer has `LOH bytes` and `POH bytes` columns; a timeline has at most
  `AllocationTreeLimits.MaxBuckets` (10,000) stretches; `AllocationTreeKeys.Node` and `BackTrace` form the tree keys.
  Version 14 (sprint 2.6) added the leak detectors: `InspectionInfo.Kind` (`InspectionKinds.Waste` or `Leak`) and
  `UsesBaseline`, `InspectionRequest.BaselineSnapshotId` (another open snapshot; used by the inspections that measure
  growth, refused when it is the snapshot itself or lacks what the inspection reads) and
  `InspectionResultDto.BaselineSnapshotId` (the baseline the answer used), and `InstanceScope.Reachable` (the objects a
  root reaches, what a detector row opens).
  Version 15 (sprint 2.7): `ResultColumn.IsHidden` (link columns no front end shows: raw type names, modules, tree keys,
  child counts) and `HasValues` (columns of the target's data), replacing the naming convention of 1.5 and 1.6
  (`InspectionColumn.HasValues` marks them in Analysis).
  Version 16 (sprint 3.1, ADR-030): `ObjectSetSpec` (kinds `Types`, `Namespace`, `Assembly`, `Objects`, `InspectionRow`,
  `RetainedBy`, with `ObjectSetLimits`), `Set` on `InstancesRequest`, `TypeSummaryRequest` and `RetainedBySetRequest`,
  `TypeSummaryResult.Set` (`ObjectSetInfo`), and `QuerySetDominatorsAsync` (`SetDominatorsRequest`, `SetDominatorsResult`,
  `SetDominatorColumns`).
  Version 17 (sprint 3.2, ADR-031): `QuerySimilarRetentionAsync` (`SimilarRetentionRequest`, `SimilarRetentionResult`,
  `RetentionGroupDto`, `SimilarRetentionLimits`), `TypePathsRequest.Set`, `TypeGrouping.Interface` with
  `TypeSummaryColumns.NoInterface`, and set kinds `Interface` and `RetentionGroup`; since 3.5 `Unreachable`.
  Version 18 (sprint 3.3, ADR-032): `TypeDiffRequest.MatchObjects` and `IncludeRetained`, the New, Dead, retained and
  per-kind delta columns, `TypeDiffResult`'s identity, kind deltas and dominators, the set kind `Compared` with
  `ObjectChange` (the spec's `Baseline` is now "the other snapshot"), and `QueryTypeTrendAsync` (`TypeTrendRequest`,
  `TypeTrendColumns`, `TypeTrendResult`).
  Version 19 (sprint 3.5, ADR-034): the set kind `Unreachable` (`ObjectSetSpec.OfUnreachable`), and the detectors
  `pending-async-methods`, `finalizer-backlog` and `type-growth` in the catalog.
  Version 20 (sprint 3.6, ADR-035): `OpenSnapshotRequest.TrustedDacs`, the error code `DacNotTrusted` (1014) with
  `DacTrustDetails` as its error data.
  Version 21 (sprint 3.8, peer review P7 and S6): `SnapshotDescriptor.Availability`, a `FeatureAvailability` per
  optional feature (an id from `SnapshotFeatureIds`: `unreachable-set`, `interface-grouping`, `field-values`,
  `string-values`, `generations`, `finalizable-scope`) with null when the snapshot offers it or the reason it does not.
  The worker's `FeatureRules` fill the list when a snapshot opens and refuse the feature with the same reason (the same
  error code as before); front ends read it through `SnapshotAvailability` (`IsAvailable`, `WhyUnavailable`), which
  counts an id the worker did not list as unavailable. `ISnapshotService.GetTypeSummaryAsync` is removed:
  `QueryTypeSummaryAsync` with its defaults gives the same rows.
- When to bump `WorkerProtocol.Version`: for a removed or renamed method, or a changed DTO shape (a field added, removed,
  renamed or retyped, a changed enum value). Not for new catalog values (an inspection, a detector) or new feature ids:
  front ends read those from the worker's own lists (`ListInspectionsAsync`, the availability list), and a feature id
  the worker does not list is unavailable.
- Error data (sprint 3.6): a refusal the front end must act on carries a record typed in Contracts as its JSON-RPC error
  data, one record per code (`DacNotTrusted`: `DacTrustDetails`); every other code carries none. The client reads it
  back through the same formatter (`WorkerHost.DacTrustOf`, tested in `DacTrustRpcTests`). `DacNotTrusted` is an
  expected refusal like `RuntimeComponentMissing`: logged at Information with its message, which lists the trusted
  folders searched.
- `HeapScopeErrorCode` values start at 1000: JSON-RPC and StreamJsonRpc reserve -32768 to -32000 (an early
  version used that range and StreamJsonRpc reinterpreted -32003 as a serialization failure).

### 4.5 Progress and cancellation
- Every long-running worker method takes `IProgress<JobProgress>` and `CancellationToken` as its last two
  parameters. StreamJsonRpc turns cancellation into a `$/cancelRequest` message and progress into
  notifications.
- `JobProgress` carries `Phase`, `Completed`, `Total` (nullable when unknown) and `Message`. The worker
  throttles reports to at most 10 per second per call.
- Worker loops check the token at least every 64K items and pass it into every nested call.
- Application's `JobTracker` gives each job an id, keeps its status (Running, Completed, Failed, Canceled), latest
  progress and a `Cancel()` handle, and raises `JobAdded` and `JobChanged` on any thread. The UI marshals them
  through `IUiDispatcher`; the CLI prints them to stderr. `WorkerOperations` is the shared use-case facade for UI
  and CLI.

### 4.6 Large result sets
Large results are never sent whole. A query creates a result set in the worker and returns a
`ResultSetInfo { Id, TotalCount, Columns }`. Clients then call `GetRowsAsync(id, offset, count)` with
`count` capped at 10,000, and `ReleaseResultSetAsync(id)` when done. Sorting and filtering happen in the
worker, producing a new result set. The grids use data virtualization and fetch pages on scroll. Result
sets are typed sources (`IResultSource`: compact arrays such as indices, counts and sizes) formatted to strings only
for the requested page, never stored as one string per cell. The store evicts least recently used sets beyond 32
sets or an estimated 256 MB, whichever comes first (the newest set is always kept). A set can carry an owner, for
example a snapshot lease, disposed on release or eviction. Producers must stay compact (indices, counts, sizes),
because the newest set is kept even when it alone exceeds the byte budget. Sources that read the graph or the dump
while formatting (instances, references) are safe against a release or eviction during a page: the store counts the
pages being formatted from each set, and a set removed meanwhile has its owner (the snapshot lease) disposed by the
last of them (sprint 1.5 review H2). Paging has the worker's error contract, because instance pages read string
values from the dump. Cells cross the pipe as invariant-culture strings with
typed column descriptors (`ResultColumn`: Text, Integer, Bytes, Percent, Address, SignedInteger; one column may be
flagged as the row key), which keeps paging generic. Any cell may be null, numeric ones included (sprint 2.3: the
retained bytes of an object no root reaches); consumers show it blank and write it as JSON null. Unknown, released or evicted sets fail with `ResultSetNotFound`.
Application's `ResultSetHandle` keeps the query that produced a set and re-runs it once when paging fails that way
(eviction, or a worker restart through the snapshot session's reopen), so front ends page without knowing about
eviction. A released handle is finished: paging throws and never re-runs the query, and a re-query in progress at the
release has its new set released too, so no set is orphaned. The grids (types, compare, instances, an object's
references and referrers, a type's referrers) fetch pages of 200 rows as they scroll; each row object is filled with one property change
notification, which keeps a page under the dispatcher budget.

Paging cost with the default JSON formatter (sprint 1.3, Release): a 10,000-row page (the maximum, used by the CLI's
`--all`) arrives in 70 to 80 ms (median) with 7.6 MB allocated in the client; deserializing a page of ten-column type
summary rows alone takes 58 ms and 8.2 MB; the grid's 200-row page takes 1.5 ms and 163 KB. The threshold for
changing the formatter was 50 MB or 200 ms per 10,000-row page, so it stays (tests
`A_full_page_costs_little_to_receive_in_the_client_process` and `ResultPageJsonCostTests`).
