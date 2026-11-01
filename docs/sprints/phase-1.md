# HeapScope sprints: Phase 1 (quick wins)

Closed sprints moved here from [SPRINTS.md](../SPRINTS.md) in sprint 3.8; each keeps its goal, scope,
acceptance criteria, decisions, tasks and review summary as they stood when it closed. Reviews are in
[docs/reviews/](../reviews/).

## Sprint 1.1: Process discovery and capture

**Goal**: find local .NET processes and capture full dumps safely into a session snapshot library.

**Scope**: list .NET processes (pid, name, command line, runtime version, elevation, working set); capture
full dumps via the diagnostics IPC; elevation detection with guidance; available RAM and disk checks; session
snapshot library (folder per session, metadata JSON); CLI `list` and `capture`; UI process list and capture.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): a process list with an elevation hint (instead of a "show all processes" switch); the session library as the workspace, with a recent sessions list (name, created, last opened, size).

**Out of scope**: gcdump (1.8), counters (1.7).

**Acceptance criteria**
- Lists LeakyApi when running; shows runtime version and whether it is elevated.
- Capturing LeakyApi writes a dump into the session folder and adds it to the library with metadata.
- Capturing an elevated process from a non-elevated HeapScope gives an explicit "run as administrator"
  message (ADR-014); integration test uses a fake elevation probe.
- Insufficient disk space (dump estimate vs free space) is detected before capture.
- CLI `list` and `capture --pid <id> [--out <path>]` work and return non-zero on failure.

**Dependencies**: 0.3, 0.4.

**Risks and open questions**: dump size estimate before capture is approximate.

**Decisions** (decided autonomously)
1. Capture runs in the UI or CLI process (ARCHITECTURE.md 2): `HeapScope.Capture` holds discovery, the elevation probe,
   the dump size estimate and the capture itself; Application holds the session library and the capture use case;
   UI and CLI call Application only.
2. Discovery: `DiagnosticsClient.GetPublishedProcesses()` (processes with a diagnostics port), minus HeapScope's own
   processes. Per process: name, working set and private bytes (`Process`), command line (`NtQueryInformationProcess`,
   limited query rights, so it works for elevated targets too), runtime version (the file version of the loaded
   `coreclr.dll`, when its modules can be read), and the elevation state. Details that cannot be read stay empty and
   never fail the list.
3. Elevation (ADR-014): `IElevationProbe` answers "is HeapScope elevated" and "can this process be dumped": a target
   whose token cannot be opened for query, or that is elevated, needs an elevated HeapScope. Capture refuses with
   `ElevationRequiredException` and the message "run HeapScope as administrator" before calling the runtime; a
   failing dump with access denied maps to the same message. Tests use a fake probe; the real probe is checked
   manually by the product owner (optional).
4. Disk check: the dump estimate is measured from the target's committed readable pages (`VirtualQueryEx`), with a
   fallback from private bytes plus images and mapped files (revised in the review); free space must cover it plus a
   1 GB reserve, otherwise `InsufficientDiskSpaceException` names both numbers and the folder. `IDiskSpace` is faked in
   tests.
5. RAM: capture itself needs little memory; the memory check is the 1.2 open warning, shown when the captured dump is
   opened. The UI offers to open the dump right after capture, which runs that check.
6. Session library: sessions under `%LOCALAPPDATA%\HeapScope\sessions` (configurable `Sessions:Root`), one folder per
   session named `yyyyMMdd-HHmmss` plus an optional name, with `session.json` (format version 1: name, created, last
   opened, snapshots with file, process name and id, runtime version, capture time, size and kind). One current
   session per UI or CLI run, created on the first capture. Recent sessions list: name, created, last opened, size
   (dotMemory's workspace list). Dumps are written into the session folder unless `--out` gives a path.
7. Capture is a tracked job (progress phases, cancellation; a canceled or failed capture deletes its partial file).
   Every capture states once that a dump can contain secrets and where it was written (section 12).
8. CLI: `list` (pid, name, runtime, elevation, working set, command line, exit 0) and
   `capture --pid <id> [--out <path>] [--open]` (exit 1 on any failure, 130 canceled).
9. UI: a Processes page (refreshable list with elevation hints, a Capture button, the captured dump offered for
   opening) and recent sessions on the Home page. The process list is loaded off the dispatcher.
10. (While implementing) The development machine runs elevated .NET services (as SYSTEM), so the real elevation path
    is tested too: `A_real_elevated_process_gives_the_administrator_message` runs instead of skipping. HeapScope's
    front ends started by tests use a temporary session root (`Sessions__Root`), so tests never write into the user's
    session library.

**Task breakdown**
1. [x] Capture: `DotNetProcess` and `ProcessDiscovery` (published processes, details, own processes excluded). Tests:
   integration test finds a running LeakyApi with its runtime version, command line and "not elevated".
2. [x] Capture: `IElevationProbe` (Windows token query) and `ElevationRequiredException`. Tests: the current process
   reports its own elevation; fake probe used by the capture tests.
3. [x] Capture: dump size estimate, `IDiskSpace`, `InsufficientDiskSpaceException`, `DumpCapturer` (preflight, write
   with `WriteDumpAsync`, cancellation, partial file removal, access denied mapped to elevation). Tests with fakes:
   elevated target refused with the administrator message, small disk refused with both numbers.
4. [x] Application: `SessionLibrary` (create, add snapshot, last opened, list recent with sizes, tolerant reading).
   Tests: round trip, ordering, damaged `session.json` skipped.
5. [x] Application: `CaptureService` (list processes, start a capture job into the current session, add to the
   library). Tests: integration capture of LeakyApi lands in the session folder with metadata; fake elevation
   probe gives the administrator message through the service.
6. [x] CLI `list` and `capture`. Tests: exit codes and output against LeakyApi; failure for a non-existent pid.
7. [x] UI: Processes page, capture with progress and the secrets note, open after capture, recent sessions on Home.
   Tests: view model tests with fakes.
8. [x] Docs: ARCHITECTURE.md (Capture, sessions, ADR-014 implementation), DEVELOPER.md, CLAUDE.md.

**Review summary** (`docs/reviews/sprint-1.1.md`): 1 High, 15 Moderate, 21 Low after dedupe. Fixed High (raised by all
three reviewers, confirmed by measurement): a canceled capture left a complete dump behind (the runtime's createdump
keeps writing); now the capture waits for the runtime, deletes the file, writes to a `.partial` name until success,
and removes the empty session. Moderate, decided autonomously: 13 fixed now (specific capture failures with next
steps, session concurrency with a lock file and exclusive folder claims, session model with ids, labels, sources and
forward-compatible JSON, `Open` and `--session`, sessions created only after the checks, `ICaptureService`, measured
dump estimate via `VirtualQueryEx`, a 1 GB free-space reserve, the target's DAC copied next to the dump, a pause
warning, `GetDiskFreeSpaceEx`, `capture --open` cancel and progress, capture logging, more tests), 1 partly fixed
(service account write access: the error names it; the full fix is in the backlog), 1 deferred (deleting snapshots and
sessions, retention). The real elevation path was tested against SYSTEM services on the development machine.

---

## Sprint 1.2: Full dump loader

**Goal**: load a full dump into the compact graph with progress and cancellation, and reopen it from the cache.

**Scope**: `HeapScope.Loaders.ClrMd`: two-pass heap walk, types, roots with details, heap kinds; writes the
index cache; worker `OpenSnapshotAsync`; memory warning before open; UI and CLI open. From the 0.1 review:
`StaticField` roots built from static field enumeration (ARCHITECTURE.md 6.1), dependent handles as edges, ClrMD
cache capped, local-only DAC lookup (ADR-017), compacting GC and measurement of the build-to-mapped transition.
From the 0.3 review: Application-owned stable snapshot handles mapped to worker snapshot ids per worker generation,
reopened lazily after a restart; `SnapshotNotLoaded` for stale ids; snapshot leases as result set owners. From
the 0.3 confirmations: sources that read graph columns take a lease per page; one shared memory budget (6.5)
replaces the synthetic job's own check. From the 0.5 review: measure a cold-cache reopen before signing
off the budgets; depend only on `IndexCacheReader.Open` and `Identity` from Storage.
From the 0.2 review: watch the by-address reference resolution time (46 s worst case at 50M), `TypeTableBuilder`
lookup by method table without a name, and keep static storage handle roots retaining while ranking
`StaticField` roots first in paths.

**Acceptance criteria**
- Object counts per type for each LeakyConsole dump match the known answers exactly.
- Root kinds and static field root details match the known answers.
- Small dump first load under 20 s; Medium under 3 min on a 16 GB laptop; reopen within the 0.5 budgets.
- Worker peak memory on Medium within 1.5x the managed heap size in in-memory mode.
- Cancellation stops a load within 2 s and leaves no cache file.

**Dependencies**: 0.2, 0.3, 0.4, 0.5.

**Risks and open questions**: ClrMD memory use during heap walks on Medium dumps.

**Product owner decisions** (2026-10-23, after Phase 0)
1. Sprint 1.2 runs before 1.1: the loader carries the budget risk, the sample dumps exist already, and opening a
   real dump is worth more than capturing one that cannot be read yet. The status table lists 1.2 first.
2. ADR-017 confirmed: no symbol or DAC downloads by default, with a per-user opt-in.
3. UI cold start: precompile the UI (ReadyToRun) in this sprint; one second is not urgent, less is welcome. The
   intermittent completion lag stays in the backlog and is re-measured with real loads.
4. ADR-004 amendment and ADR-015 stand as written. Commits carry no co-author trailer.
5. Phase 1 runs autonomously under the Phase 0 rules; the lead pauses and asks when human input is clearly better.
6. Market research: the UI should look and feel like dotMemory without 1:1 parity
   (`docs/research/dotmemory-ux.md`).

**Decisions** (decided autonomously)
1. DAC lookup (ADR-017): the loader finds the DAC itself and passes its path to `CreateRuntime(path)`. Candidates,
   in order: the dumped runtime's own folder when it exists on this machine, the matching locally installed
   runtime (`dotnet\shared\Microsoft.NETCore.App\<version>`), the dump's folder, and a user folder
   (`Symbols:DacFolder`). A candidate is accepted only when its PE timestamp and image size match the dump's
   index. ClrMD's own file locator is replaced by a local-only one, so nothing reaches the network. When nothing
   matches, the load fails with `RuntimeComponentMissing` naming the runtime version and where to put the file.
   The opt-in `Symbols:AllowMicrosoftSymbolServer` (default off) restores ClrMD's Microsoft symbol server lookup.
2. Object pass per segment (`ClrSegment.EnumerateObjects`), so the heap kind comes from the segment (generation per
   object through `GetGeneration`, LOH, POH, frozen). Types are interned by method table on first sight.
   `HasFinalizer` comes from `EnumerateFinalizableObjects` minus `ClrType.IsFinalizeSuppressed` (the header bit
   0x40000000).
3. Roots: `ClrHeap.EnumerateRoots` (strong handles, stacks, finalizer queue, .NET 9 and 10 additional static and
   thread static roots) for retaining roots, plus weak handles from `EnumerateHandles` as non-retaining roots.
   `StaticVar` additional roots (the static storage bucket) map to `StrongHandle` with the detail "static storage"
   and are not added twice for the same object; `ThreadStaticVar` maps to `Other` with the thread as detail;
   `SizedRef` handles map to `Other`, retaining. Dependent handles become edges (`AddDependentHandle`), never roots.
4. Static field roots: for every module of every app domain, `ClrModule.EnumerateTypesWithStaticFields`, every
   initialized reference static, `ClrStaticField.ReadObject`; detail `Namespace.Type.field`; retaining. Statics of
   generic instantiations that are not in a module's type map are not reported (their storage stays reachable
   through the storage handle); the limit is documented.
5. Data quality: modules compiled as debuggable code (`ClrModule.DebuggingMode` disables optimizations) are
   recorded in the cache metadata and shown as a note, because stack roots in their frames can be missing
   (ARCHITECTURE.md 6.1). Dropped references and roots are shown the same way.
6. The worker gets a snapshot store: `OpenSnapshotAsync(path, options)` reuses a valid index cache or loads the
   dump, writes the cache and returns a descriptor (id, info, features, counts, mode, cache state, timings, notes);
   `CloseSnapshotAsync(id)`; `GetTypeSummaryAsync(id)` holds a snapshot lease while it builds a self-contained result
   set (the minimal query 1.3 extends). Loads are serialized (one load at a time). Protocol version 2; new error code
   `RuntimeComponentMissing` (1009).
7. Memory mode after a first load (ADR-015): the graph is built in memory and written to the cache; when the budget
   says mapped, the built graph is dropped, one compacting collection runs (LOH compaction once) and the cache is
   reopened mapped. The transition is measured.
8. Memory warning: Application estimates the first-load peak from the dump size before asking the worker
   (constants calibrated on the sample dumps in this sprint) and warns when it exceeds available physical memory;
   the UI asks to continue, the CLI prints the warning and continues unless `--no-overcommit` is given.
9. Snapshot handles (from the 0.3 review): Application keeps a stable handle per opened file (full path) mapped to
   the worker's snapshot id for the current worker generation; after a worker restart the next use reopens it
   lazily from the index cache. A stale id fails with `SnapshotNotLoaded`.
10. A dense real-application dump is needed for the budget sign-off: the sample dumps have 1.1 to 1.2 references
    per object. The lead captures a dump of a real .NET application running on this machine (kept in the git-ignored
    `artifacts/dumps`, never shared) and records its density and load numbers next to the samples.
11. UI: a Snapshots page (open a dump with a file dialog, list open snapshots with their figures and notes, and
    a top types table from `GetTypeSummaryAsync`), following the dotMemory research where it costs little; the
    full grid is 1.3.
12. (While implementing) ClrMD reads the dump through its lock-free memory-mapped reader: on the Medium dump the
    first load fell from 36.6 s to 15.8 s and the peak from 4.2 GB to 2.4 GB, so the 16 GB first-load budget holds.
13. (While implementing) The loader runs one aggressive, compacting collection after freezing the objects: the
    phase-one chunks are garbage then and otherwise stay committed through the reference pass (Medium: 3.2 GB down
    to 1.7 GB when the reference pass starts).
14. (While implementing) ReadyToRun on publish only (`dotnet publish -r win-x64`); `Worker.Deploy.targets` also
    publishes the worker. Measured first window render: 640 to 785 ms plain, 580 to 630 ms precompiled; the first
    run after a build takes 2.3 to 2.6 s, most likely antivirus scanning new files. Most of the remaining cost is
    the runtime and WPF themselves, so the backlog item drops to Low.
15. (While implementing) Dense heaps: the only real application running here was the Roslyn compiler server (423 MB,
    0.35M objects, 2.01 references per object), far below Medium size. At the product owner's request a dense fixture
    was added: DenseHeap grows a heap of real Roslyn compiler objects (syntax trees, symbols, compilations) to a
    target size, and `New-DenseDump.ps1` captures it and always kills the fixture (finally block plus parent watch).
    Its 4.4 GB dump (54.1M objects, 3.03 references per object) loads in 64 s with a 2.6 GB peak, inside both
    budgets. `DenseHeapTests` (small heap) checks every object and reference against ClrMD, the density, and that
    the fixture is killed on dispose and exits when its parent exits.
16. (While implementing) `--smoke-open <dump>` runs the UI smoke checks while opening a real dump; the log records
    the first window render time on every run.

**Task breakdown**
1. [x] Reorder the plan (1.2 before 1.1), record the product owner decisions, research document in `docs/research`.
2. [x] Loader: DAC locator (local only, opt-in server), `RuntimeComponentMissing`. Tests: locator candidates and
   PE matching with fake folders; the missing DAC message names the version.
3. [x] Loader: object pass, types, heap kinds, finalizer flags; reference pass by address; dependent handles; roots
   with details; static field roots; debuggable modules; progress and cancellation every 64K objects. Tests:
   known answers on the shared LeakyConsole dump (per-type counts exact, census lower bounds, static field roots
   with details, runtime root kind and container, dependent handle edges and no dependent roots, heap kinds,
   finalizer flags, late garbage present), cancellation within 2 s.
4. [x] Storage metadata: debuggable modules and load notes (optional JSON fields, no format change). Tests: round
   trip and old metadata without the fields.
5. [x] Worker: snapshot store, open (cache reuse or load, write, mapped transition), close, type summary, serialized
   loads, protocol 2, error codes. Tests: open twice uses the cache with the same identity; cancel leaves no cache
   file; close then query fails with `SnapshotNotLoaded`; summary totals equal graph totals.
6. [x] Application: snapshot handles per worker generation with lazy reopen; memory estimate and warning;
   `SnapshotSession.StartOpen`. Tests: restart reopens from the cache; estimate thresholds.
7. [x] CLI `open <dump> [--rebuild] [--mode auto|memory|mapped] [--top N] [--no-overcommit]`. Tests: exit codes,
   output on a fixture dump.
8. [x] UI: Snapshots page, open command with the memory warning, snapshot figures and notes, top types. Tests:
   view model tests with fakes; the smoke run stays within the dispatcher budget.
9. [x] UI ReadyToRun: precompile the UI and measure the first window render against the plain build.
10. [x] Benchmark `load-dump`: first load (object pass, reference pass, roots, build, write), peak private bytes,
    mapped transition, reopen in both modes; Small, Medium and a dense real-application dump; cold cache when the
    product owner's elevated run is available.
11. [x] Docs: ARCHITECTURE.md (loader, snapshot store, protocol 2, measurements), DEVELOPER.md, CLAUDE.md.

**Review summary** (`docs/reviews/sprint-1.2.md`): 4 High, 19 Moderate, 20 Low after dedupe; all High confirmed by
their reviewers. Fixed High: snapshots described with the opened path (a dump moved with its cache kept its old path
and broke reopens); DAC lookup and ClrMD's file locator limited to local fixed-drive paths, the dumped runtime folder
only inside installed runtimes (paths from an untrusted dump could reach a network share); capability explanations
aware of the snapshot kind and 5.1 corrected (dump-backed services planned for 1.5); minidump header check (no heap
memory refused) and segment completeness (a truncated dump loses `CompleteGraph` and `ExactCounts`). Moderate,
decided autonomously: 16 fixed now (session lifecycle with cache-only reopens and `SnapshotChanged`,
`ProbeSnapshotAsync` and one shared memory warning, `SnapshotUnavailable` and logged failures, worker options from its
command line only with a privacy warning, cancellation checks and a 2 s bound measured at every phase, CLI DAC
options, mode chosen after collecting, DAC version match without an index, `load-dump` budget checks, synthetic job on
the shared budget, raw interior pointers, frozen objects rule, collectible statics not retaining, graph released before
the mapped transition, collections only where they free something, more known-answer assertions), 2 deferred (cache
identity to 2.1, loader contract to 1.8), 1 won't fix (the estimate holds on the dense dump). Measured: Medium fixture
15.8 s and 2.4 GB; DenseHeap (54M objects, 3.03 references per object) 58.9 s and 2.6 GB; reopen 0.6 to 1.0 s;
cancellation 0.2 to 1.1 s; UI smoke open of the dense dump, longest dispatcher operation 44 ms.

---

## Sprint 1.3: Heap summary by type

**Goal**: show what is on the heap by type, fast.

**Scope**: per type count, shallow size, generation breakdown, LOH/POH; paged result sets; sortable,
filterable virtualized grid; CLI `summary` (table, CSV, JSON). From the 0.3 review: result column kinds
for addresses, signed deltas and percentages plus a key column flag (protocol version bump); an Application
result-set handle that re-runs its query on `ResultSetNotFound` (eviction or restart); measure JSON paging
allocations in the UI process for a 10k-row page and switch formatter if significant.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): the types grid is the core view (plain, namespace and assembly grouping; type-to-filter with match highlighting, CamelHumps and `*`); a generations sub-view (Gen0, Gen1, Gen2, LOH, POH, frozen); a fixed color per heap kind defined as theme resources, light and dark themes from the start; dense grids, right-aligned numbers, exact bytes in grids and humanized sizes in tiles; the snapshot overview grows into a tile page whose rows link into views.

**Acceptance criteria**
- Summary totals equal the sum over all objects; per-type counts match known answers.
- Medium summary under 5 s; UI stays responsive while it runs.
- Generation columns are hidden with an explanation when `Generations` is missing.

**Dependencies**: 1.2.

**Decisions** (decided autonomously)
1. Aggregation once per snapshot: the worker computes per-type count, shallow bytes and per-heap-kind bytes in one
   pass over the objects (parallel by object range, merged per type) the first time a summary is asked for, and
   keeps these small per-type arrays with the snapshot. Grouping, filtering and sorting then work on types only
   (thousands of rows), so every re-query is cheap.
2. One query DTO, `TypeSummaryRequest` (snapshot, group by type, namespace or assembly, filter text, sort column and
   direction), answered with `TypeSummaryResult`: a result set plus totals (objects, bytes, per heap kind) and whether
   generations are available. Sorting and filtering happen in the worker and produce a new result set (ARCHITECTURE.md
   4.6). `GetTypeSummaryAsync(snapshotId)` from 1.2 stays as the default query.
3. Columns: name (key), objects, bytes, percent of all bytes, then bytes per heap kind (Gen0, Gen1, Gen2, LOH, POH,
   frozen) only when the snapshot has `Generations`. New result column kinds `Percent`, `Address` and
   `SignedInteger` and a key flag on `ResultColumn`; protocol version 3.
4. Filter (smart case, revised while implementing): an all lower-case filter is a case-insensitive substring; a filter
   with capitals is matched as typed, then as CamelHumps (`SCE` matches `StaticCacheEntry`); `*` matches any run of
   characters (case-insensitive). The first rule set ("SCE" as a case-insensitive substring) matched 244 types on the
   Small dump. Matching is done in the worker on type
   names; the UI highlights the matched characters.
5. Application `ResultSetHandle`: keeps the query that produced a result set and re-runs it once when paging fails with
   `ResultSetNotFound` (eviction or worker restart, through the snapshot session's reopen).
6. JSON paging cost: measured in the UI process for a 10,000-row page (allocations and time); the formatter changes
   only when it is significant (over 50 MB or 200 ms per page). The result is recorded either way.
7. CLI `summary <dump> [--group type|namespace|assembly] [--filter text] [--sort bytes|count|name] [--top N | --all]
   [--format table|csv|json]`.
8. UI: the Snapshots page's overview keeps its tiles and gains a heap kinds bar; the full types grid replaces the top
   types table: data virtualization (rows fetched in pages as the grid scrolls), sorting by column header (re-queried
   in the worker), a debounced type-to-filter box with match highlighting, and a group-by selector. Generation columns
   are hidden, with the capability explanation shown, when `Generations` is missing.
9. Themes: light and dark resource dictionaries (following the Windows app theme at startup) with one fixed color
   per heap kind; the window's hard-coded colors move into them.

**Task breakdown**
1. [x] Contracts: `TypeSummaryRequest`, `TypeSummaryResult`, grouping and sort enums, new column kinds and key flag,
   protocol 3. Tests: contract mirror and protocol tests updated.
2. [x] Worker: per-snapshot type aggregation (parallel), grouping, smart filter, sort, typed result source.
   Tests: unit tests on synthetic graphs (totals equal the sum over objects, grouping, filter rules, sort orders);
   aggregation timing on the Medium and dense dumps through the worker (under 5 s).
3. [x] Application: `ResultSetHandle` with re-query on `ResultSetNotFound`; `SnapshotSession.QueryTypeSummaryAsync`.
   Tests: eviction and worker restart re-run the query.
4. [x] Known answers through the worker: per-type counts of the shared fixture dump match the known answers, and
   summary totals equal the snapshot's.
5. [x] CLI `summary` (table, CSV, JSON). Tests: output formats and exit codes.
6. [x] UI: virtualized types grid with sorting, filter with highlighting, grouping, generation columns or the
   explanation, heap kinds bar. Tests: view model tests with fakes; smoke open stays within the dispatcher budget.
7. [x] Themes: light and dark dictionaries, heap kind colors, window colors moved. Tests: both dictionaries define
   the same keys.
8. [x] Measure JSON paging for a 10,000-row page in the UI process; record and decide.
   Result: 7.6 MB and 70 to 80 ms per 10,000-row page over the pipe, 1.5 ms per 200-row grid page; under the
   thresholds, so the default formatter stays (ARCHITECTURE.md 4.6).
9. [x] Docs: ARCHITECTURE.md, DEVELOPER.md, CLAUDE.md.

**Review summary**: 1 High, 19 Moderate (two found while verifying fixes), 18 Low (`docs/reviews/sprint-1.3.md`).
- High H1 fixed and confirmed by gc-specialist: same-named types (one assembly in two load contexts) were merged into one
  row; type rows are now one per method table, colliding names get the module file name and a number.
- Moderates fixed: result set release and stale pages (no orphaned sets), `Show(null)` and superseded queries,
  logging and retry for failed pages, header arrows from the view model, a shared per-snapshot aggregation on
  `DerivedData`, enum validation and the `GroupKey` mirror test, smart-case wording, "unreachable included" labels,
  the Unknown heap kind, a parallelism cap, unfiltered row counts, enumeration without fetching, rows mapped by column
  name, many new unit tests and a gated UI fake; the worker's GC heaps capped at half the logical processors (M18) and
  unexpected worker start failures wrapped (M19).
- Deferred: a generic row list, a sort key for deltas and a row key for drill-down (1.4); a bounded page window (1.5);
  a synchronization context and `TimeProvider` in view model tests; the remaining intermittent lag right after a
  snapshot opens (High backlog item, with new evidence).
- Measured (Release, 24 logical cores, 32 GB): type aggregation 0.02 to 0.05 s on Medium (57.5M objects) and 0.02 to
  0.03 s on DenseHeap (54.1M); CLI `summary` from the index cache 1.9 s (Medium) and 2.0 s (DenseHeap) end to end;
  10,000-row page 70 to 80 ms and 7.6 MB, 200-row grid page 1.5 ms; Medium smoke open 8 of 8 within the gates after the
  GC heap cap (longest operation 41 to 60 ms), DenseHeap and Small 4 of 6 overall.

---

## Sprint 1.4: Snapshot diff

**Goal**: show what grew between two snapshots.

**Scope**: diff by type (count and size deltas, new types, vanished types), sort by growth; CLI `diff` with
`--fail-on-growth <bytes|percent>` returning a non-zero exit code.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): one row per type with new, dead and survived (count and bytes) plus signed deltas, the earlier snapshot as baseline; clicking a count opens those objects in the types view; two pickers instead of drag and drop.

**Acceptance criteria**
- Diff of two LeakyApi snapshots reports, for every leak, each exact census type of its known answer with count
  growth exactly items times perItem, and the leak's `types[0]` first by count growth among fixture types.
  (Sorting by size growth puts `System.Byte[]` first, because payloads dominate bytes.)
- Exit code contract tested: 0 below threshold, 2 above, 1 on errors.
- Medium diff under 5 s.

**Dependencies**: 1.3.

**Decisions** (decided autonomously)
1. Per-type deltas, no object identity: two dumps cannot tell survived, new and dead objects apart (compaction moves
   objects, addresses are reused), so the diff shows per row the baseline and current objects and bytes and their
   signed deltas, plus whether the type is new or gone. dotMemory's survived, new and dead columns need allocation
   tracking (a 2.x live session at the earliest). Recorded as ADR-018.
2. Rows are matched across snapshots by type name plus module file name (never by the display name, whose `#n` follows
   each dump's own type order); method tables sharing a key inside one snapshot are summed. A name that appears with
   more than one module shows the module, as in the types view. Namespace and assembly grouping are supported.
3. Built on the cached per-snapshot aggregations (1.3): a diff merges two small per-type arrays, so it costs about
   the same as a type summary. Both snapshots must be open in the same worker.
4. Contract (protocol 4): `TypeDiffRequest` (baseline, current, group by, filter, sort column, direction) answered by
   `TypeDiffResult` (result set plus totals: objects and bytes of each side and their deltas, new and gone type
   counts, the delta of the filtered rows). Sorting uses a column name validated by the worker, the generic sort key
   deferred from 1.3; the type summary keeps its enum for now (backlog). Default sort: bytes delta, descending.
5. CLI `diff <baseline> <current> [--group] [--filter] [--sort column] [--top N | --all] [--format table|csv|json]
   [--fail-on-growth <size|percent>]`: the growth checked is the bytes delta of the rows that match the filter (the
   whole heap without one), a size like `50MB` or a percent of the matching baseline bytes like `10%`. Exit codes:
   0 at or below the threshold, 2 above it, 1 on errors, 130 canceled. Usage errors move from 2 to 64 for every
   command (`EX_USAGE`), so 2 always means "grew too much" for CI.
6. UI: the Snapshots page gets a Compare tab next to Types: two pickers over the open snapshots (the earlier capture
   is the default baseline, with a swap button), the same grid features as Types (virtualized, sort by header, filter
   with highlighting, grouping), delta columns with a sign and color, and totals. Clicking a count to open those objects
   comes with 1.5's object lists.
7. `VirtualRowList` becomes generic over a row type and factory (deferred from 1.3), shared by Types and Compare.
8. Medium diff budget (under 5 s) measured with the Medium and DenseHeap dumps open from their index caches.

**Task breakdown**
1. [x] ADR-018 (diffs compare per-type totals; no object identity across dumps).
2. [x] Analysis: `TypeDiff` merging two aggregations by type key (name plus module file name) with grouping.
   Tests: synthetic graphs (growth, new and gone types, duplicate keys summed, namespace grouping, empty side).
3. [x] Contracts and worker: `TypeDiffRequest`, `TypeDiffResult`, column-name sort validated by the worker,
   `TypeDiffSource`, `QueryTypeDiffAsync`, protocol 4. Tests: contract mirror, worker unit tests on sort, filter and
   columns; an unknown sort column fails with `InvalidArgument`; both snapshots must be loaded.
4. [x] Application: `SnapshotSession.QueryTypeDiffAsync` with re-query through `ResultSetHandle` (either snapshot
   reopened after a restart). Tests: integration re-query after a worker restart.
5. [x] Known answers: two LeakyApi snapshots; every leak's exact census types grow by items times perItem, and each
   leak's `types[0]` is first among its fixture types by count growth; by bytes delta `System.Byte[]` is first.
6. [x] CLI `diff` with formats and `--fail-on-growth`; usage errors exit 64. Tests: exit code contract (0 below,
   2 above, 1 errors, 64 usage) and output formats.
7. [x] UI: generic `VirtualRowList`; Compare tab with pickers, swap, grid, totals. Tests: view model tests with the
   gated fake; smoke open unaffected.
8. [x] Measure Medium diff (Medium and DenseHeap from cache); record.
   Result: CLI `diff` end to end 2.8 to 2.9 s warm (both opens from cache included), aggregations 0.05 to 0.08 s;
   20.9 s on the first run after a build, spent starting the freshly built CLI and worker.
9. [x] Docs: ARCHITECTURE.md, DEVELOPER.md, CLAUDE.md.

**Review summary**: 0 High, 13 Moderate, 27 Low (`docs/reviews/sprint-1.4.md`).
- Fixed: reloaded copies of a type visible ("copies", type counts per side); the CI gate refuses a filter that matches
  nothing, has unit-tested thresholds (non-finite and overflowing values refused, zero baseline documented) and prints
  each side's notes; `ExactCounts` on diff results; the empty Compare picker refills; Compare race tests; the
  two-snapshot reopen and unknown snapshot tests; `summary` on the shared CLI helpers and validators; constant-time
  row lookup in the grids.
- Deferred: the drill-down key, the split of the grid base and the bounded page window to 1.5 (recorded in its scope);
  growth by generation, a per-row gate, induced-GC capture and a 16 GB two-snapshot first load to the backlog; the cold
  first show of the Compare tab to the UI cold start item.
- Measured (Release, 24 logical cores, 32 GB): CLI `diff` of Medium against DenseHeap 2.8 to 2.9 s end to end from the
  index caches; aggregations 0.05 to 0.08 s each; Compare tab warm re-query longest dispatcher operation 64 to 74 ms,
  cold first show 97 to 238 ms; known answers: every exact census type grows by exactly items times perItem.

---

## Sprint 1.5: Object search and inspection

**Goal**: inspect individual objects and walk outgoing references.

**Scope**: instances of a type (paged), field values including nested structs, arrays, strings; follow
outgoing references; back and forward navigation.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): an instances grid (address, bytes, later retained bytes); filters by string value, regex and address; outgoing references as fields plus a references list; one drill-down action everywhere (double-click or Enter opens the row as a new subject); back and forward buttons (and mouse buttons).

From the 1.4 review: a stable drill-down key on summary and diff rows (type name plus module file name, or the rows'
type indices per snapshot) consumed by the instances query; the shared grid base split into a query-lifecycle core and a
type-grid layer, with column-name sorting in the core; a bounded window of loaded pages in `VirtualRowList` (millions of
instance rows).

From the 1.2 review: the full-dump `ISnapshot` that reopens the dump with ClrMD for `FieldValues`, `StringContents` and `ThreadStacks` (`IFieldValueReader` and the string and thread stack services), so full dumps stop reporting them as missing; ClrMD runtime creation failures wrapped with a next step.

**Acceptance criteria**
- Field values of a known LeakyConsole object match the values the fixture set.
- Nested struct fields are shown as nested rows.
- Field inspection is disabled with an explanation for snapshots without `FieldValues`.

**Dependencies**: 1.3.

**Decisions** (decided autonomously)
1. Field values and string contents come from the dump, reopened with ClrMD on first use: a Core service interface
   `IObjectReader` (fields as a tree, string text, array elements), implemented in Loaders.ClrMd, held by a full-dump
   snapshot (`DumpSnapshot`) next to its graph, serialized by a lock and disposed with the snapshot. Snapshots opened
   from the index cache get it too while the dump file is present and unchanged. Full dumps then report `FieldValues`
   and `StringContents`; `ThreadStacks` stays missing (no feature uses it yet; backlog). ClrMD runtime creation
   failures are wrapped with a next step (from the 1.2 review).
2. Drill-down key (from the 1.4 review): summary and diff rows in Type grouping carry hidden `TypeName` and `Module`
   columns with the raw values; the instances query takes a snapshot, a type name and a module file name and lists the
   objects of every type with that key (ADR-018), so two loaded copies of a type are listed together, each object with
   its own type.
3. Instances (dotMemory research 7): a result set of object indices with columns Address (key), Type, Bytes,
   Generation and Value (string text or array length), sortable by any column, largest first by default. One filter
   box: `0x...` matches addresses by prefix, `/pattern/` is a regular expression on the value, anything else is a
   case-insensitive substring of the value. Value filters on strings need `StringContents`.
4. Object view: address, type, size, generation; a field tree (nested structs as children, references as links,
   primitive array elements as children, the first 100 with a "more" row) when the snapshot has `FieldValues`, the
   capability explanation otherwise; the string text (first 10,000 characters); and outgoing references as a paged
   result set (field path or array index when `FieldValues`, reference order otherwise) built from the graph.
5. Navigation: the Snapshot tab becomes a navigator whose subjects are the types grid, an instances grid and an object
   view; double-click or Enter opens the row as a new subject (types and compare rows open instances, instances and
   references open objects); Back and Forward buttons and the mouse's back and forward buttons. Opening a compare row
   shows that type's instances in the current snapshot. Selecting another snapshot starts a new history.
6. Grid base split (from the 1.4 review): `GridQueryViewModel<TRow>` keeps the query lifecycle, filter debounce and
   column-name sorting; `TypeGridViewModel<TRow>` adds grouping. The type summary sorts by column name too, retiring
   `TypeSummarySort` (protocol 5).
7. `VirtualRowList` keeps at most 50 pages (10,000 rows) loaded and drops the pages farthest from the last one asked
   for (from the 1.3 and 1.4 reviews).
8. Known answers: a new semantic fixture `Inspection` (LeakyConsole `--inspection`) holds one object with fields of
   every kind (primitives, enum, string, null, nested struct inside a struct, arrays); `fixtures/KnownAnswers/Inspection.json`
   gains an optional `fields` section (path and expected text), format version 1 stays (additive).
9. Protocol 5: `QueryInstancesAsync`, `GetObjectAsync`, `QueryReferencesAsync`, the summary's column-name sort and
   the hidden key columns.
10. Trace recording (product owner, added mid-sprint): EventPipe traces recorded by HeapScope itself through
   `Microsoft.Diagnostics.NETCore.Client` (already a dependency, ADR-010), so nobody installs dotnet-trace: a recorder in
   Capture behind an Application service, CLI `trace --pid <id> [--duration] [--profile cpu|gc] [--out]`, and the UI's
   `--smoke-trace <file>` recording HeapScope's own UI during a smoke run (to diagnose the drill-down lag on 16M-object
   types). Reading traces stays a developer tool (TraceEvent in perf/, not shipped); analysis in the product is a
   Phase 2 sprint (allocation tracking between snapshots).
11. Responsiveness (found in task 6, decided autonomously): the UI opts out of power throttling (ADR-019); grids preload
   their first page and bind after a background-priority yield; generation columns are behind a "Generations" checkbox
   (off by default, the dotMemory research's sub-view); pages and navigator views are built once and kept.
12. CLI inspection (task 7, decided autonomously): `instances` finds the module itself when `--module` is left out
   (the only module with that exact type name; several modules ask for `--module`; none exits 1). `object` on a
   snapshot without field values prints what the graph knows plus the explanation on stderr and exits 0: the object
   exists and everything the snapshot holds is printed; exit 1 stays for errors such as no object at the address.
   Both add `--format json`.

**Task breakdown**
1. [x] Fixture and known answer: `InspectionSample` with known values, LeakyConsole `--inspection`, `Inspection.json`
   with `fields`, README. Tests: ClrMD known-answer test reads every listed field.
2. [x] Core `IObjectReader` and records; Loaders.ClrMd `ClrMdObjectReader` (fields tree, strings, arrays, field
   names of references), wrapped runtime creation failures. Tests: integration against the Inspection dump.
3. [x] Worker: `DumpSnapshot` with the reader (lazy, disposed on close, both first load and cache reopen),
   capabilities `FieldValues` and `StringContents`. Tests: descriptor features; reader disposed on close.
4. [x] Contracts and worker queries: instances (key, filter kinds, sort), object details, references; hidden key
   columns on summary and diff; summary sort by column name; protocol 5. Tests: worker unit tests on synthetic graphs
   (instances, filters by address, references order), integration on the Inspection dump (field values, nested structs,
   references with field paths), capability refusal without `FieldValues`.
5. [x] Application: session queries with re-query and reopen. Tests: integration after a worker restart.
6. [x] UI: grid base split, bounded page window, instances grid, object view (field tree, references), navigator with
   back and forward, drill-down from types and compare rows, capability explanation. Tests: view model tests with
   fakes; smoke open drills into an object and stays within the dispatcher budget.
7. [x] CLI: `instances <dump> --type <name> [--module] [--filter] [--top]` and `object <dump> <address>`. Tests:
   output and exit codes.
8. [x] Docs: ARCHITECTURE.md (5.1, 4.4, code map), DEVELOPER.md, CLAUDE.md, the known answers README.
9. [x] Trace recording: recorder and Application service, CLI `trace`, UI `--smoke-trace`, developer `trace-report`.
   Tests: a trace of a fixture process is written and readable; cancel stops it; the UI lag diagnosed from a trace.

**Review summary**: 5 High, 15 Moderate, 21 Low (`docs/reviews/sprint-1.5.md`).
- Fixed (High, each confirmed by its reviewer): reference names kept to three and a count (was quadratic); the result
  set store keeps a set's snapshot lease until pages being formatted finish; instances count into exact arrays, filter
  values one at a time and keep text only for a sort by value (at most 250,000 strings), with key sorts, progress and
  cancellation; multi-dimensional arrays read by indices; the dump's fingerprint checked when the reader opens it.
- Fixed (Moderate): progress on the object and references calls (protocol 5 unreleased); paging guarded; value filters on strings without text refused; arrays sort by length; ADR-019 wording; gone diff rows
  open in the baseline (ADR-018); row lookup from highlighted text; trace partial files deleted and the stream wait
  bounded; smoke runs never show a dialog and always stop their trace; invalid references and unreadable strings shown
  as such; new fixture fields (`Grid`, `Pairs`, `Repeated`) and tests.
- Deferred: per-copy drill-down from a types row and slowing the idle watchdog probe to the backlog; a typed reader API
  and the remaining known-answer cases to 1.6; the missing-capability tests (needing a gcdump) to 1.8.
- Measured: string filter over Medium's 6.6M strings 3.8 s (text) and 4.5 s (regex) end to end against 1.9 s to open;
  drill on Medium 36 to 63 ms longest operation; with the full suite running, Small and DenseHeap smoke runs pass
  (longest operation 43 to 53 ms).

---

## Sprint 1.6: Simple inspections

**Goal**: one-click findings for common waste.

**Scope**: inspection plugin model in Analysis; duplicate strings (with wasted bytes), LOH contents, empty and
oversized collections, large arrays; UI list of inspections; CLI `inspect <name>`. Fixtures and known answers (format
version 1, kind `inspection`): repeated strings, empty and oversized collections, deliberate LOH arrays.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): inspections as overview tiles: count, wasted bytes, one-line cause and fix, a link to the objects; add sparse arrays, finalizable objects and a fragmentation bar (pinned, unpinned, free) as cheap wins.

From the 1.5 review (M6, M15): inspections are the first consumers of typed reads, so this sprint adds them to the
object reader (a field by path, array elements, strings in bulk) and moves display formatting into the worker. The
Inspection known answer gains a boxed value in an `object` field, a `[Flags]` combination, strings longer than the 100
and 1,000 character cuts, and an enum array.

**Acceptance criteria**
- Each inspection has synthetic-graph tests with known answers and a fixture integration test.
- Duplicate strings requires `StringContents` and is disabled otherwise.

**Dependencies**: 1.5.

**Decisions** (decided autonomously)
1. Inspections in this sprint: LOH contents (by type), large arrays (85,000 bytes or more, the LOH threshold, largest
   first), sparse arrays (reference arrays of at least 16 slots with at least half of them null), finalizable objects
   (by type), empty collections, oversized collections, duplicate strings; plus a fragmentation summary (free bytes
   per heap kind, pinned objects and bytes) shown as a bar, not a list.
2. An inspection is an Analysis plugin (`IInspection`: name, title, required capabilities, run over the graph and an
   optional typed reader, result = count, wasted bytes, one-line cause and fix, and rows). Graph-only inspections need
   no dump; duplicate strings needs `StringContents`; collections need `FieldValues`. A missing capability makes the
   inspection unavailable with the capability explanation (never silently empty). Recorded as ADR-020.
3. Duplicate strings group by full content: a 64-bit XxHash3 of the UTF-16 text plus the length as the key (no string
   kept per object; a collision would merge two values, odds negligible at heap sizes, documented). Wasted bytes per
   value are (count - 1) times the size of one copy. Only values with two or more copies are rows.
4. Collections covered: `List<T>`, `Dictionary<TKey,TValue>`, `HashSet<T>`, `Queue<T>`, `Stack<T>` (BCL generic field
   layouts read through the typed reader; other collection types are ignored, not guessed). Empty: count 0 (wasted:
   the backing array, if any). Oversized: capacity at least 2 times count plus 16, wasted = unused slots times the
   element size.
5. Inspections run on demand per snapshot (the Inspections tab or CLI `inspect`); results are result sets like any
   query, not persisted in the index cache.
6. Free bytes per heap kind are recorded by the loader (free objects are already walked) and stored in the index cache
   metadata as an optional field (additive, format unchanged); a cache written before 1.6 shows "free space unknown,
   reopen with Rebuild". Pinned objects come from pinned and async pinned handle roots and the pinned object heap.
7. Typed reader API (from the 1.5 review M6): Core's reader returns typed values (`ObjectValue`: kind, integer or
   floating value, character, boolean, address, text) instead of display text, adds a field by path and string text in
   bulk; display formatting moves into the worker (unit-testable without a dump).
8. Drill-down from inspection rows: `InstancesRequest` gains a scope (all, LOH only, finalizable only) and
   `SameValueAs` (strings equal to the string at an address); rows naming one object open that object. Protocol 6.
9. UI: a third tab "Inspections" on the Snapshots page: tiles (title, count, wasted bytes, cause, fix, first rows),
   the fragmentation bar, and the selected inspection's full grid; double-click opens the row on the Snapshot tab.
10. CLI: `inspect <dump> [<name>] [--top N | --all] [--format table|csv|json]`; without a name it prints every
    inspection's summary line; an unavailable inspection prints why and exits 1 when named, is listed as unavailable
    otherwise.
11. Fixture: LeakyConsole `--waste` (`WasteFixtures`: repeated strings, empty and oversized collections of each covered
    kind, LOH arrays, sparse arrays, finalizable objects, a pinned buffer), known answer `Waste.json` with kind
    `inspection` and a `findings` section (inspection name, row key, minimum count or exact count). The Inspection known
    answer gains the 1.5 review M15 cases.
12. (While implementing) Reference arrays are marked in the type table (`TypeKind.ReferenceArray`, set by the loader):
    a reference array's non-null slots are its outgoing references, so sparse arrays need no dump reads. Caches
    written before 1.6 lack the flag and the free space; the inspections say so ("reopen with Rebuild").
13. (While implementing) The fragmentation bar uses the fragmentation inspection's rows (per heap kind: live, free,
    pinned), so the snapshot descriptor does not change.
14. (From the review) Loader facts are explicit: `SnapshotFacts` on `SnapshotInfo` (free space, reference-array marks)
    and `IInspection.RequiresFacts`; an inspection missing a fact is unavailable with its own reason. Finalizable counts
    objects ready for the finalizer thread too; string text for hashing is streamed, never built.

**Task breakdown**
1. [x] Fixtures and known answers: `WasteFixtures`, LeakyConsole `--waste`, `Waste.json` (`findings`), the M15 fields in
   `InspectionFixtures` and `Inspection.json`, README rows. Tests: `KnownAnswerTests` verifies every finding and field
   with ClrMD directly.
2. [x] Typed reader: Core `ObjectValue`, reader returns typed fields, field by path, strings in bulk; worker
   formatting (`ObjectFormatter`). Tests: formatter unit tests (every kind, culture invariant, truncation); reader
   integration tests on the Inspection dump (typed values, paths, bulk strings).
3. [x] Loader free space and pinning: free bytes per heap kind in the load result and the index cache metadata
   (optional); pinned summary from roots and POH. Tests: loader test (free bytes present, per kind sums within the
   segments); cache round trip; an old cache without it.
4. [x] Analysis inspections: `IInspection`, `InspectionResult`, the seven inspections and the fragmentation summary.
   Tests: synthetic graph known answers for the graph-only ones; fake typed reader for duplicates and collections;
   capability requirements.
5. [x] Contracts and worker, protocol 6: list inspections (availability and reason), run one (summary plus rows as a
   result set, progress, cancellation), heap layout on the descriptor, `InstancesRequest` scope and `SameValueAs`.
   Tests: worker source tests; IPC round trip; unavailable inspection refused with `CapabilityNotSupported`.
6. [x] Application: session queries with re-query after eviction and reopen. Tests: integration after a worker
   restart.
7. [x] UI: Inspections tab (tiles, fragmentation bar, grid, drill-down). Tests: view model tests with fakes; the smoke
   open shows the inspections within the dispatcher budget.
8. [x] CLI `inspect`. Tests: output and exit codes against the waste dump.
9. [x] Integration: every inspection against the waste dump matches `Waste.json`. Docs: ARCHITECTURE (code map, 4.4,
   5.1, ADR-020, measurements: inspections on Medium), DEVELOPER, CLAUDE, the known answers README.

**Review summary**: 3 High, 18 Moderate, 19 Low (`docs/reviews/sprint-1.6.md`).
- Fixed (High, each confirmed by its reviewer): finalizable objects include those ready for the finalizer thread (a
  stuck finalizer fixture); duplicate strings and the equal-strings scope hash text streamed from the dump in pieces
  (no string built, sorted hashes per size group); availability is one tested rule (capabilities and loader facts).
- Fixed (Moderate): loader facts (`SnapshotFacts`); the wire vocabulary in Contracts with mirror tests; the Inspections
  tab ignores late results of a canceled run, logs failures, refreshes a tile selected while running, has blank
  placeholder cells; stack pins counted; collection layouts skip arrays and nested types; per-type totals in arrays;
  sparse null slots clamped with a note; the reader lock released after 16M characters; fixture and test gaps closed.
- Deferred: keeping inspection results across eviction, dependent-handle edges in sparse arrays, async pinned buffers,
  bulk collection reads, a hidden flag on result columns, to the backlog; the end-to-end gcdump checks to 1.8.
- Measured: `inspect` on Medium 7 s and DenseHeap 15 s (all eight, Release, from the index cache); the Small smoke
  run's inspections phase 2.4 s with a longest dispatcher operation of 23 ms.

---

## Sprint 1.7: Live counters and timed snapshots

**Goal**: support the "run load, snapshot, run more load, snapshot again" workflow.

**Scope**: EventPipe counters (heap size, GC counts per generation, allocation rate) in a live chart; automatic
snapshots every N minutes or when the heap exceeds a threshold; session timeline.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): a stacked area chart per heap kind (the 1.3 colors) with snapshot markers, snapshot cards with a total bar; snapshot rules: MB or percent over baseline, absolute MB, every N minutes, a maximum count; a high GC pressure badge from counters.

**Acceptance criteria**
- Counters for LeakyApi under LoadGen show a rising heap while a leak is active.
- A threshold rule captures exactly one snapshot when crossed, then re-arms per configuration.
- Chart updates never block the dispatcher over 100 ms.

**Dependencies**: 1.1.

**Decisions** (decided autonomously)
1. Counters come from the runtime's `System.Runtime` EventCounters over an EventPipe session (the diagnostics client
   HeapScope already uses for capture and traces, ADR-010), once per interval (default 1 s): GC heap size, the size of
   each generation, LOH and POH, GC counts per generation, allocation rate, time in GC and working set. Monitoring runs
   in the front end's process, like capture; the worker is not involved (ADR-021).
2. Rules: every N minutes; heap above an absolute size; growth over the baseline (MB or percent; the baseline is the
   first sample's GC heap size); at most N snapshots per run. Size rules fire once when crossed and re-arm per rule:
   `once` (never again), `below` (after the heap falls back under the threshold, the default) or `step` (the threshold
   rises by its own amount, for every further step). The rule metric is the heap after the last GC (changed by the
   review, H2: the GC heap size includes garbage); `below` re-arms under 90 percent of the threshold.
3. A rule capture is a normal capture into the current session, recorded with `SnapshotSource.Timed` and a label naming
   the rule ("above 500 MB", "every 5 min"); at most one capture runs at a time and rules wait while it runs (the target
   is paused while it is written).
4. Session timeline: each monitoring run writes `counters-<pid>-<time>.csv` into the session folder (one row per sample, plus
   capture markers), so a run can be charted later; the Live page shows the current run. Reopening an older run's
   timeline in the UI is a backlog item.
5. GC pressure badge: time in GC averaging 10 percent or more over the last 10 intervals that had a GC (the counter is
   the last GC's share, review M10).
6. UI: a new "Live" page (navigation) with a process picker, interval and rules, Start and Stop, a stacked area chart per
   heap kind (the fixed heap kind colors) over the last 30 minutes with snapshot markers, the latest values, snapshot
   cards (time, rule, size) and the badge. The chart is one lightweight element drawn once per sample.
7. CLI: `monitor --pid <id> [--interval 1] [--duration N] [--every 5m] [--above 500MB] [--growth 100MB|50%]
   [--rearm once|below|step] [--max N] [--format table|csv]`: one line per sample and per capture; Ctrl+C stops.
8. (While implementing) Front ends see Application's `HeapSample`, not Capture's counter type (UI.Wpf references
   Application and Contracts only); a run that cannot monitor its process faults with `CaptureFailedException`.
   `MonitorService.Start` attaches handlers before sampling begins, so no sample is missed.
9. (From the review) The runtime's megabytes are decimal; rule captures that are canceled or fail are recorded and never
   end the run; no rule fires after Stop; the app stops monitoring before it cancels jobs on close.

**Task breakdown**
1. [x] Capture `CounterMonitor`: EventPipe counter session, payload parsing into `CounterSample` (testable without a
   process). Tests: parsing unit tests; integration on LeakyApi: samples arrive each interval and the GC heap rises
   while leaking work is posted.
2. [x] Application `SnapshotRules`: rules, baseline, re-arm modes, maximum count, GC pressure. Tests: unit tests with
   sample sequences and a fake clock (fires once, re-arms below, steps, every N minutes, max count, waits while a
   capture runs).
3. [x] Application `MonitorService`: start and stop, samples to listeners, rules to captures (`SnapshotSource.Timed`),
   counters CSV with markers. Tests: integration on LeakyApi: a size rule captures exactly one dump and re-arms per
   configuration; the CSV holds the samples and the marker.
4. [x] CLI `monitor`. Tests: output and exit codes; a capture by `--above`.
5. [x] UI Live page: view model, `TimelineChart`, cards, badge. Tests: view model tests with a fake monitor; chart
   mapping tests; a smoke run (`--smoke-live`) feeding an hour of samples keeps every dispatcher operation within the
   budget.
6. [x] Docs: ARCHITECTURE (code map, ADR-021, 8, 10 measurements, 12), DEVELOPER, CLAUDE.

**Review summary**: 3 High, 16 Moderate, 17 Low (`docs/reviews/sprint-1.7.md`).
- Fixed (High, each confirmed by its reviewer): decimal megabytes; size rules on the heap after the last GC (no firing
  or re-arming on garbage, 90 percent re-arm, GC pressure only from intervals with a GC, the chart topped by what was
  allocated since the last GC); canceled or failing rule captures recorded without breaking the run.
- Fixed (Moderate): no rules after Stop; incomplete last interval dropped; monitoring stopped before jobs at close;
  timeline files created new, pinned to the run's session, written until the first failure; the EventPipe stop bounded;
  zero thresholds refused; captures started outside the lock; new tests (gated, canceled, throwing listener).
- Deferred: one capture per target across pages, the timeline in `session.json`, a missing-counter warning, a
  Meter-based counter source for .NET 9 and later, to the backlog.
- Measured: counters arrive one interval after the session starts; the Live smoke run charts 1,800 samples with a
  longest dispatcher operation of 18 to 26 ms; a growth rule on LeakyApi captures exactly one dump.

---

## Sprint 1.8: gcdump support

**Goal**: capture and load gcdumps behind the same snapshot abstraction.

**Scope**: gcdump capture; `HeapScope.Loaders.GcDump`; capability flags drive the UI; diffs across gcdumps and
mixed kinds.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): views a gcdump cannot answer stay visible but disabled, with the capability reason.

From the 1.5 review (M4, M12): tests on a snapshot without field values and string text: the worker's
`GetObjectAsync` returns no fields and the explanation; CLI `object` prints the note and exits 0 (decision 12 of 1.5);
an instances value filter on strings is refused with `CapabilityNotSupported` and CLI `instances` exits 1.

From the 1.2 review: a loader contract in Core (`LoadResult`, `LoadProgress`, `LoadDiagnostics`) shared by both loaders, and the snapshot store picking the loader by file signature (minidump `MDMP` or gcdump).

**Acceptance criteria**
- gcdump of LeakyConsole: type counts match known answers when not sampled.
- Field inspection and duplicate strings are disabled with an explanation.
- Diff of two gcdumps ranks the leaking type first.

**Dependencies**: 1.2, 1.4.

**Task breakdown**
1. [x] Truncated dumps are refused, not crashed on: ClrMD's `InvalidDataException` when the runtime's heap cannot be
   created becomes `SnapshotFormatException` with a next step. Tests: the truncated dump test at 30, 60 and 90 percent
   of the file, each either refused or incomplete.
2. [x] Vendor the gcdump code: `src/HeapScope.ThirdParty.GcDump` (dotnet/diagnostics `b1a4548`, PerfView `76dc28af`,
   MIT, original headers kept), `CHANGES.md` listing every change, `THIRD-PARTY-NOTICES.md` entry, layering table and
   architecture test rules (Capture and Loaders.GcDump may reference it; it references only TraceEvent and the
   diagnostics client). Tests: architecture tests (layering, license headers skip vendored code).
3. [x] Loader contract in Core: `ISnapshotLoader`, `LoadResult`, `LoadProgress`, `LoadTimings`, `LoadDiagnostics` move
   from Loaders.ClrMd; `SnapshotFileSignature.Detect` tells a minidump (`MDMP`) from a gcdump (FastSerialization
   header). Tests: Core tests for both signatures, an unknown file and an empty one; the ClrMD loader tests unchanged.
4. [x] gcdump capture in Capture (`GcDumpCapturer`): a heap snapshot over EventPipe written as `.gcdump` through a
   partial file, timeout and cancellation, partial file removed on failure. Tests: integration capture of LeakyConsole
   (file starts with the gcdump signature, partial gone); cancellation leaves no file.
5. [x] `HeapScope.Loaders.GcDump`: gcdump to `HeapGraph`. Objects are the nodes with addresses; the root tree's
   pseudo nodes become root kinds (statics with their field name, locals, strong, pinned, finalizer, ref-counted);
   capabilities `ObjectGraph | ObjectAddresses`, plus `CompleteGraph | ExactCounts` when not sampled; strings and
   arrays marked by name. Tests: known answers on a LeakyConsole gcdump (type counts of every leak, static roots by
   field name), capabilities, a sampled gcdump loses complete and exact (synthetic file written by the vendored writer).
6. [x] Worker: the snapshot store picks the loader by file signature; a gcdump is a `GraphSnapshot` and gets an index
   cache like a dump. Tests: open a gcdump through the worker (descriptor kind and features), reopen from the cache,
   an unknown file refused with a message.
7. [x] Capture kind end to end: a `SnapshotKindDto` (full dump, gcdump) on `StartCapture`, sessions record it;
   CLI `capture --kind gcdump` and `monitor --kind gcdump`; UI capture choice on the Processes and Live pages. Tests: CLI
   capture of a gcdump; view model tests for the kind; monitor rule captures pass the kind.
8. [x] Capability-driven views on a gcdump (deferred tests of 1.5 and 1.6): `GetObjectAsync` returns no fields and the
   explanation; CLI `object` prints the note and exits 0; an instances value filter is refused with
   `CapabilityNotSupported` and CLI `instances` exits 1; unavailable inspections are refused end to end with the
   gcdump text; the generations bar and field tree show the reason in the UI.
9. [x] Diffs across gcdumps and mixed kinds: type rows match across kinds (module by file name). Tests: diff of two
   LeakyConsole gcdumps ranks the leaking type first; a mixed diff (dump against gcdump) carries the intersection of
   capabilities and matches the leak type.
10. [x] Docs: ARCHITECTURE (layout, loader contract, gcdump mapping, ADR-010 amendment), DEVELOPER (vendored code),
   CLAUDE.md commands, CURRENT-CONTEXT.

**Decisions** (all decided autonomously, Phase 1 autonomy)
1. The vendored code is its own project, `HeapScope.ThirdParty.GcDump`, because both Capture (writing) and
   Loaders.GcDump (reading) need it and the layer rules forbid one referencing the other. Its files keep the MIT
   headers; the project builds without analyzers or a nullable context.
2. Two changes to the vendored code, both in `EventPipeDotNetHeapDumper.cs` and listed in `CHANGES.md`: sessions connect
   by process id only (the diagnostic port path used types internal to the published diagnostics client), and the
   rundown keyword is a local constant (internal in the package).
3. The capture follows dotnet-gcdump: one forced full GC, the heap snapshot keywords, a 30 second default timeout. On
   .NET 10 the runtime has no lossless buffering for this session (that needs .NET 11), so a very large heap can lose
   events; the capture writes the vendored code's lines to the log (review M1) and fails on the writer's 10 million
   node cap (review H3). A lost-events check is a backlog item.
4. gcdumps have no generations (section 5.2): every object is `HeapKind.Unknown`, the generations view shows the
   reason. Segment ranges in the file could place objects later (backlog).
5. Root details are partial: statics carry their field name and the value's type as the root detail (the runtime
   names statics by the value's type, review M12), other roots their kind only; `RootDetails` stays off. Weak references
   are not in a gcdump.
6. Sampled gcdumps (written by PerfView) are loaded as they are, without scaling; they lose `CompleteGraph` and
   `ExactCounts` and carry the note. dotnet-gcdump and HeapScope never sample.
7. No reference-array marks or free space from a gcdump (`SnapshotFacts.None`): sparse arrays and fragmentation explain
   why they are unavailable.
8. The loader is chosen by the file's first bytes, never by its extension.
9. A gcdump's dependent handle values are recorded as retaining other roots (changed in review M11): the file drops
   the handle's key but writes the value only when the key was alive, and every gcdump object survived its collection,
   so a non-retaining root would make them look like garbage.
10. gcdump type names are normalized to the full dump loader's form where the writer's changes can be undone (generic
   argument spacing, `[]c` for `<>c`, size-bucket nodes merged into their type). Nested types lose their declaring type
   in the file, so a diff of a full dump and a gcdump matches the full dump's names in the gcdump's form
   (`TypeNameForms.AsGcDumpName`); each row keeps each side's own name to open its objects (review H2).
11. Found while widening the truncated dump test (task 1): an object without a readable type in a truncated heap threw
   from ClrMD's size; the loader now counts it as unreadable before reading its size, and a load that claims a complete
   graph must equal the untruncated load. The full run then found cuts that remove the GC's own region list, so the
   heap looked complete with objects missing; the loader now also probes each segment's memory in the file and checks
   the minidump's directory against the file length (`MinidumpHeader.IsTruncated`), and a truncated file never claims a
   complete graph.

**Review summary** (`docs/reviews/sprint-1.8.md`): 5 High, 14 Moderate, 19 Low after dedupe. Fixed and confirmed:
H1 a canceled gcdump capture hung (vendored change 3, bounded wait), H2 mixed diff rows opened an empty list (each side
keeps its own names), H3 the vendored writer's silent 10 million node cap (the capture now fails), H4 COM wrappers loaded
as objects, H5 finalizable objects showed zero on gcdumps (a loader fact). All 14 Moderates fixed now (capture failure
reasons and logging, multi-dimensional names, heap dump truncation, capture tests, partial gcdump sweep, no copies
marker across kinds, loader version in index caches, Compare wording, gcdump read progress and cancellation, sizes of 2
to 4 GB, retaining dependent handle values, static root details, lost type events, a COM loader test). Deferred to the
backlog: raising the 10 million node cap with a Medium measurement, a lost-events check, loader allocations, generations
for gcdumps, a FastSerialization safety check. Tests: 712 (from 620 at 1.7), all passing.

---

## Sprint 1.9: Phase 1 hardening

**Goal**: pay down the Phase 1 debt where Phase 2 builds next, and settle the open questions Phase 2 depends on.

**Scope** (from the Phase 1 peer review, approved by the product owner on 2026-10-24): snapshot kind logic in one
place per kind (worker and capture); the worker's RPC layer split by feature area with one shared column sort; one
mapping between Core and Contracts enums; the main window split per page and the smoke harness out of `App`; a
prototype measurement of the Phase 2 indices (reverse references, dominators) against the memory budgets; where gcdump
capture runs; whether the gcdump reader refuses unknown types in a crafted file; a local full test gate; and, as they
fit, a shared CLI output writer, the code map reorganized by project, and small review leftovers.

**Acceptance criteria**
- No behavior change: every Phase 1 test passes unchanged except where a test moves with the code it covers.
- A measurement of reverse references and dominators on the synthetic 50M graph, with peak memory against the budgets,
  recorded in ARCHITECTURE 6.4 and 10, and a decision on whether the Phase 2 indices need mapped mode.
- A decision record on where gcdump capture runs, backed by a measurement of the front end's memory during a capture.
- A test showing the gcdump reader's behavior on a crafted file naming an unknown type.

**Dependencies**: 1.8.

**Task breakdown**
1. [x] Snapshot kinds in one place in the worker: `Snapshots/SnapshotKinds` holds, per kind, the loader, its version,
   how an opened graph becomes an `ISnapshot` (with or without a reader) and the kind's data quality notes;
   `SnapshotStore` keeps no kind checks. Tests: `SnapshotKindsTests` (each kind's loader version and snapshot type, an
   unknown kind refused); the Phase 1 worker and integration tests unchanged.
2. [x] Capture in one place: `PartialSnapshotFile` (reserve a path, refuse to overwrite, commit by rename, discard with a
   retried delete) shared by `DumpCapturer` and `GcDumpCapturer`; `CaptureService` takes per-kind settings (extension,
   disk preflight, DAC copy, capturer) from one table. Tests: `PartialSnapshotFileTests`; capture tests unchanged.
3. [x] One mapping between Core and Contracts enums (`ContractMapping`), replacing the casts in the worker. Tests: a
   mapping test over every value.
4. [x] The worker's RPC layer by feature area: `WorkerService` in partial files (snapshots, summaries and diffs,
   instances and objects, inspections), `WorkerDtos.cs` split per area (types unchanged, no protocol change), and one
   `ColumnSort` for the result sources' sort-by-column tables. Tests: `ColumnSortTests`; worker tests unchanged.
5. [x] UI structure: one resource dictionary per page instead of one 780 line `MainWindow.xaml`; the smoke harness moves
   from `App.xaml.cs` into `Smoke/SmokeRunner`. Tests: UI tests and `AttributionTests` unchanged; smoke runs
   (`--smoke-open` on the Small dump, `--smoke-live`) pass.
6. [x] Phase 2 index prototype (perf): reverse references and dominators with retained sizes on the synthetic 50M graph
   (and the fixture Medium dump when present), time and peak private bytes against the budgets; results in ARCHITECTURE
   6.4 and 10 and a decision on mapped mode for the new indices.
7. [x] Where gcdump capture runs: measure the front end's peak memory while capturing a large gcdump; decision record
   (ADR-023).
8. [x] Crafted gcdump safety: a test feeding the gcdump reader a file that names a type it does not know, proving it is
   refused without creating that type; a mitigation if it is not.
9. [x] Local full gate: `build/gate.ps1` (Debug and Release builds, the full suite, run time, failures appended to a
   history file under `artifacts/`). Tests: one run recorded.
10. [x] Shared CLI output writer (table, csv, json) used by `summary`, `diff`, `instances` and `inspect`. Tests: the CLI
   integration tests unchanged.
11. [x] Leftovers: one shared repo-root helper for the test projects; the gcdump "sampled" text names lost events and the
   writer's limit; the vendored lossy-buffering warning goes to the log (vendored change 4).
12. [x] Docs: the ARCHITECTURE code map by project instead of by sprint; DEVELOPER and CLAUDE.md for the gate and the
   new files.

**Decisions** (the product owner approved the whole scope on 2026-10-24: "I agree on all parts. Make it so"; the
defaults below were not asked separately)
1. Refactors change no behavior and no wire contract: DTO types keep their names and namespaces, the protocol version
   stays the same.
2. The index prototype is a benchmark in `perf/`, not product code. It started with the iterative Cooper-Harvey-Kennedy
   dominator algorithm; the review (H1) showed it needs a pass per node on lists reachable from both ends, so Phase 2
   uses semi-NCA, which the prototype now measures next to it (the two agree where Cooper-Harvey-Kennedy converges).
3. gcdump capture moves to the worker in Phase 2 if the front end's peak for a gcdump of about 10 million nodes is above
   1 GB; otherwise it stays in the front end and the decision is recorded.
4. If the gcdump reader would create unknown types, the loader restricts it before Phase 2 (a vendored change if
   needed).
5. The gate is a local PowerShell script (no remote or CI yet); its history file is git-ignored.
6. The sprint runs through wrap and close like Phase 1 (Moderate findings decided and logged), then stops and reports.
7. Outcomes recorded during the sprint:
   - Task 3's mapping test is the architecture project's existing mirror tests (every value of each mirrored enum),
     plus the lowest-flag theory, which moved with the mapping.
   - Task 6 (after the review): semi-NCA computes the Phase 2 indices in 9.5 s on the Medium fixture (peak 3,514 MB)
     and 12.9 s on DenseHeap (peak 4,119 MB in memory, 2,360 MB with the graph mapped); computation follows the memory
     mode, the kept indices are cache sections, and the time budget is 60 s on a Medium-scale real heap (ARCHITECTURE
     6.4).
   - Task 7: the capturing front end peaked at 1,191 MB near the gcdump node cap, so gcdump capture moves to the
     worker in Phase 2 (ADR-023).
   - Task 8: TraceEvent 4.0 creates only registered types; a crafted gcdump naming another type is refused and the
     type is never created; no mitigation needed.
   - Task 10 also fixed the CLI's JSON: `summary` and `diff` report the snapshot kinds, and `IncludesUnreachable` is
     true only for full dumps (it was always true).
   - Smoke runs: the Medium open and compare smoke trips the 200 ms lag gate at times with the old and the new main
     window alike (one run each at 565 and 602 ms, others passing): the known High backlog item, not a regression.

**Review summary** (`docs/reviews/sprint-1.9.md`): 1 High, 7 Moderate, 24 Low after dedupe. H1 (fixed and confirmed): the
prototyped Cooper-Harvey-Kennedy dominators need a pass per node on lists reachable from both ends, so Phase 2 uses
semi-NCA, measured next to it. All 7 Moderates fixed now: per-phase memory measurement, DenseHeap and mapped mode
measured (DenseHeap 12.9 s, peak 4,119 MB in memory, 2,360 MB mapped; Medium 9.5 s, 3,514 MB), frozen objects as roots,
ADR-023 consequences, per-side CLI JSON flags with a test, the remaining kind checks moved onto the kind handlers, and
stronger kind tests. The refactors changed no behavior: the Phase 1 tests pass unchanged, the DTO split moved
declarations only (protocol 6), and the smoke runs match the old window. Tests: 731 (from 712 at 1.8), all passing in
the gate.
