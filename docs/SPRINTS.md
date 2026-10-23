# HeapScope Sprints

Phases: **0.x** architecture, framework and skeleton; **1.x** quick wins; **2.x** heavy hitters; **Backlog**
everything else.

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
| 1.1 | Process discovery and capture | Planned | | |
| 1.3 | Heap summary by type | Planned | | |
| 1.4 | Snapshot diff | Planned | | |
| 1.5 | Object search and inspection | Planned | | |
| 1.6 | Simple inspections | Planned | | |
| 1.7 | Live counters and timed snapshots | Planned | | |
| 1.8 | gcdump support | Planned | | |
| 2.1 | Reverse references | Planned | | |
| 2.2 | Retention paths | Planned | | |
| 2.3 | Dominator tree and retained size | Planned | | |
| 2.4 | Retention explorer UI | Planned | | |
| 2.5 | Allocation tracking | Planned | | |
| 2.6 | Smart leak detectors | Planned | | |
| 2.7 | Navigation and polish | Planned | | |

---

## Sprint 0.0: Project charter, architecture and process

Documentation only: CLAUDE.md, ARCHITECTURE.md, DEVELOPER.md, SPRINTS.md, the three sprint skills, the three
reviewer subagents, `.gitignore`, initial commit and tag `sprint-0.0`. No review file (no code).

**Decisions**
- ADR-004 amended by the product owner during this sprint: 16 GB developer laptops are the baseline; flex up
  when more RAM is available. ADR-015 (adaptive memory modes) added as a consequence.
- Extra projects added to the kickoff layout (Application.Tests, UI.Wpf.Tests, Architecture.Tests,
  perf/HeapScope.Benchmarks, fixtures/KnownAnswers, build/); reasons in ARCHITECTURE.md section 3.

---

## Sprint 0.1: Solution skeleton

**Goal**: a building, testable solution with every project, enforced layering and the shared build hygiene.

**Scope**
- `HeapScope.sln` with all `src/`, `tests/` and `perf/` projects from ARCHITECTURE.md section 3 (except
  Loaders.GcDump, created in 1.8, and fixtures, created in 0.4).
- `global.json`, `Directory.Build.props` (net10.0, nullable, implicit usings, warnings as errors, analyzers,
  deterministic builds), `Directory.Packages.props` (Central Package Management), `.editorconfig`.
- Latest stable versions of all packages verified and recorded in ARCHITECTURE.md section 14.
- Generic host and DI in UI, worker and CLI.
- Empty WPF shell window hosted through DI.
- CLI `--version`.
- Worker that starts, logs and exits cleanly (no IPC yet).
- `build/Worker.Deploy.targets` deploying the worker beside UI and CLI.
- Architecture test enforcing the dependency table.

**Out of scope**: IPC, graph types, fixtures, any analysis.

**Acceptance criteria**
- `dotnet build HeapScope.sln` succeeds with zero warnings in Debug and Release.
- `dotnet test --solution HeapScope.sln` runs every test project, all pass (decision 1).
- Adding a forbidden reference (for example UI.Wpf -> Analysis) makes the architecture test fail; verified by a
  test that runs the rule checker against a deliberately bad sample.
- `heapscope-cli --version` prints the product version and exits 0.
- The WPF app starts, shows a titled empty shell window and exits cleanly when closed.
- The worker starts with `--pipe x`, logs startup, and exits with code 0 when told to stop (Ctrl+C) or when
  `--parent-pid` names a process that exits.
- After building UI and CLI, `worker/HeapScope.Worker.exe` exists in their output folders.

**Dependencies**: none.

**Risks and open questions**
- WPF projects require `net10.0-windows`; shared props must allow both TFMs.
- Exe-to-exe deployment through MSBuild can be fragile; the targets file is covered by an integration test.

**Decisions** (all decided autonomously, Phase 0 autonomy granted by the product owner)
1. Test framework: xUnit v3 (`xunit.v3` 4.x) on Microsoft.Testing.Platform, opted in through `global.json`
   (the VSTest path is no longer supported by xunit.v3 4.x on the .NET 10 SDK). Commands use
   `dotnet test --solution HeapScope.sln`.
2. CLI parser: System.CommandLine 2.0 (stable, MIT). The CLI assembly is named `heapscope-cli`
   (`heapscope-cli.exe`); a plain `heapscope` collided with the UI assembly `HeapScope` on case-insensitive file
   systems (found by the architecture test).
3. Architecture test: own rule checker (csproj parsing plus compiled assembly references), no third party
   library; it lives in `HeapScope.Architecture.Tests`.
4. Solution file: classic `HeapScope.sln` (the kickoff name), not `.slnx`.
5. Analyzers: `AnalysisLevel=latest` with the default rule set plus `EnforceCodeStyleInBuild`; stricter rule
   sets can be raised later without code churn now.
6. All projects compile as x64 (`PlatformTarget=x64`), matching ADR-001.
7. Worker deployment: `build/Worker.Deploy.targets` adds a build-order-only reference and copies the worker's
   output into `worker/` after build.
8. Commits carry no co-author trailer, matching the product owner's sole-authorship preference in their other
   repositories (flagged for confirmation).
9. The worker's Ctrl+C stop relies on the generic host's standard console lifetime; the automated test covers
   the parent-exit path, since sending Ctrl+C to a child process is not reliable on Windows.
10. `ProductInfo` lives in Contracts (not Core) because UI.Wpf may not reference Core directly.

**Task breakdown**
1. [x] Repo build files: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`,
   `tests/Directory.Build.props`. Test: solution builds with zero warnings.
2. [x] All src projects with references per ARCHITECTURE.md 3.1; placeholder types where a project would be
   empty. Test: build.
3. [x] Worker host: options (`--pipe`, `--parent-pid`), parent watcher, startup log, clean exit. Tests:
   integration tests for parent exit and for a missing `--pipe` (exit code 2).
4. [x] CLI host with System.CommandLine, `--version`. Test: integration test runs `heapscope-cli --version` and compares with the product version.
5. [x] WPF shell through the generic host, empty titled window. Tests: view model unit test; integration
   smoke test starts and closes the app.
6. [x] `build/Worker.Deploy.targets` imported by UI, CLI and Integration.Tests. Test: worker exists in each
   output folder.
7. [x] Architecture test (rule checker, csproj scan, assembly scan, negative sample). Tests: in
   `HeapScope.Architecture.Tests`.
8. [x] Test projects and benchmark project scaffolding. Test: `dotnet test` runs every test project.
9. [x] Package versions recorded in ARCHITECTURE.md section 14.

**Review summary**: 1 High, 12 Moderate, 14 Low (docs/reviews/sprint-0.1.md). The High (dependent handles
modelled as roots) was a design defect, fixed in ARCHITECTURE.md 6.1 and confirmed. All 12 Moderate were fixed now
(decided autonomously): graph semantics and memory design (M1 to M5, implemented in 0.2, 0.4 and 1.2 scopes), CLI
version, worker option validation, UI and CLI host lifecycle, CA2007 enforcement, ADR-016 (Contracts owns the wire
vocabulary), an evaluated-graph architecture checker (which caught a CLI and UI assembly name collision) and
ADR-017 (no symbol or DAC downloads by default). Lows recorded only.

---

## Sprint 0.2: Snapshot abstraction and compact graph

**Goal**: the domain model and compact graph that every later feature builds on, measured against the budget.

**Scope**
- `ISnapshot`, `SnapshotInfo`, `SnapshotKind`, `SnapshotCapabilities`, `CapabilityNotSupportedException`
  (user-facing capability descriptions live in Contracts, sprint 0.3, ADR-016).
- `Column<T>` with array backing (mapped backing arrives in 0.5).
- `TypeTable`, `StringTable`, `RootTable`, `GcRoot`, `RootKind`, `HeapKind`, `TypeKind`.
- `HeapGraph` (struct-of-arrays, CSR) and `HeapGraphBuilder` (two-phase, chunked buffers, final columns from an
  `IColumnAllocator`; managed allocator only in this sprint).
- Root semantics from ARCHITECTURE.md 6.1: `RootFlags.Retaining`, `Interior` floor search, `DroppedRootCount`,
  has-finalizer object flag; dependent handles as edges.
- `SyntheticGraphBuilder` for tests (deterministic shapes: chains, trees, cycles, diamonds, random graphs with
  a seed, scalable to 50M objects).
- Benchmark: build a synthetic 50M-object graph; record build time, bytes per object and peak memory.

**Out of scope**: loaders, persistence, analysis algorithms beyond trivial graph queries.

**Acceptance criteria**
- Core has no package or project references (architecture test).
- Builder resolves references by address, drops references to non-objects and counts them, and rejects
  duplicate object addresses with a clear exception.
- Objects of 4 GiB or more round-trip their size through the large-size side table.
- The builder throws when references are added for a source index that is not strictly increasing.
- Interior roots resolve by floor search; roots that resolve to nothing are counted in `DroppedRootCount`.
- Unit tests cover: empty graph, single object, self reference, cycles, unsorted input, dropped references,
  roots of every kind (retaining and non-retaining), a dependent-handle edge, type interning, capability checks.
- 50M-object synthetic benchmark: graph memory within the ARCHITECTURE.md estimate (30 to 38 bytes per object
  at 3 references per object, measured), build time recorded, peak managed memory recorded.
- ARCHITECTURE.md section 10 updated with the measured numbers.

**Dependencies**: 0.1.

**Risks and open questions**
- Builder peak memory during the final copy; chunking keeps it near 2x the largest single array.

**Decisions** (decided autonomously)
1. `Column<T>` wraps `ReadOnlyMemory<T>`; a native backing in 0.5 is a `MemoryManager<T>` (ARCHITECTURE.md 6.5).
2. The synthetic graph builders live in Core (`HeapScope.Core.Synthetic`), not in a test project, because the
   worker's diagnostic job (0.3), the benchmarks and several test projects all use them.
3. Dependent handles are registered on the builder (`AddDependentHandle`) after objects are frozen; the builder
   merges them into the primary's CSR row during the reference pass. This is the only entry point for them: the
   `AddReferences` contract says its targets are the object's own references and exclude dependent-handle
   targets (the builder cannot detect a loader that passes them both ways; sprint 1.2 tests it).
8. Address lookups in the builder use a bucketed `AddressIndex` (added after the review benchmark showed 217 s
   for 150M random by-address lookups with plain binary search; now 46 s).
4. Free objects are never added to the graph; fragmentation is an aggregate (adopts review note L1 of 0.1).
5. Final columns are allocated uninitialized (`GC.AllocateUninitializedArray<T>`) and chunk buffers are released
   column by column (adopts L2 of 0.1, as the natural implementation).
6. Root order in the root table: by object index, then kind.
7. Benchmarks stay a plain console app; `graph-build` reports elapsed time, column bytes per object, managed
   heap after build and peak working set.

**Task breakdown**
1. [x] Vocabulary types: `SnapshotKind`, `SnapshotCapabilities` (+ `Require`), `CapabilityNotSupportedException`,
   `SnapshotInfo`, `ISnapshot`, `HeapKind`, `TypeKind`, `RootKind`, `RootFlags`, `ObjectFlags`, `GcRoot`,
   `LargeObjectSize`. Tests: enum value stability, capability checks.
2. [x] `Column<T>`, `IColumnAllocator`, `ManagedColumnAllocator`, internal `ChunkedBuffer<T>`. Tests: chunk
   boundaries, copy.
3. [x] `StringTable`/builder, `TypeTable`/builder (interning by method table, or by name when it is zero).
   Tests: interning.
4. [x] `RootTable` with per-object lookup. Tests: lookup, ordering.
5. [x] `HeapGraph`: sizes with large side table, references, address lookups (exact and floor), totals,
   footprint, `FromColumns` validation. Tests in graph tests.
6. [x] `HeapGraphBuilder`: freeze with sort and duplicate detection, reference pass with ordering check, dropped
   references and roots, interior roots, dependent handles. Tests: every acceptance bullet.
7. [x] `SyntheticGraphBuilder` and `SyntheticGraphs` (chain, tree, cycle, diamond, random with seed, scalable).
   Tests: shapes have the expected counts and edges; random graphs are deterministic per seed.
8. [x] `GraphSnapshot` (an `ISnapshot` over a graph). Tests: capabilities and dispose.
9. [x] Benchmark `graph-build` and the 50M measurement recorded in ARCHITECTURE.md section 10.

**Review summary**: 0 High, 11 Moderate, 14 Low (docs/reviews/sprint-0.2.md). All Moderate fixed now (decided
autonomously). The most important: the by-address benchmark mode exposed 217 s for 150M random reference lookups;
a bucketed `AddressIndex` brought it to 46 s. Also: `FrozenAddresses` for loaders, in-place permutation (unsorted
peak about 2.7 GB instead of 3.15 GB), Faulted phase and disposal in the builder, weak handles never retaining,
`GraphSnapshot` never claiming service-backed capabilities, eager `TotalSize`, `HasFinalizer` defined precisely,
and the missing dependent-handle merge tests. Measured: 34.0 bytes per object at 50M objects.

---

## Sprint 0.3: Worker process and IPC

**Goal**: UI and CLI drive a separate worker over named pipes with progress, cancellation, paging and crash
recovery.

**Scope**
- Contracts: `IWorkerService`, DTOs, `ProtocolVersion`, error codes, `JobProgress`, `ResultSetInfo`, wire enums
  mirroring Core vocabulary with a test that their values match (ADR-016).
- Worker: pipe server, StreamJsonRpc, parent watch, stderr logging, Server GC settings, result set store with
  LRU limit, a diagnostic job (`RunSyntheticJobAsync`) that builds a synthetic graph with progress and
  cancellation and returns a paged result, test hooks (crash, hang) enabled only by a command-line switch.
- Application: `WorkerHost` (launch, handshake, crash detection, restart, stderr forwarding), `JobTracker`,
  file logging under `%LOCALAPPDATA%\HeapScope\logs`.
- UI: shell with navigation (Home, Jobs, About), jobs panel with progress bars and cancel, worker status and
  crash banner.
- CLI: `worker ping`, `worker info`, `synthetic --objects N`, with console progress and Ctrl+C cancellation.

**Out of scope**: real snapshot loading, capture.

**Acceptance criteria**
- Integration test: client starts worker, handshake succeeds, protocol version mismatch produces a clear error.
- Integration test: synthetic job of 5M objects reports progress at least 3 times and completes.
- Integration test: cancellation stops the job within 1 second and the worker stays usable.
- Integration test: killing the worker during a job faults the call with `WorkerCrashedException`, and the next
  call transparently restarts the worker.
- Integration test: the worker exits within 2 seconds after its parent process exits.
- Result paging: `GetRowsAsync` honors offset and count, caps count at 10,000, and released or evicted result
  sets return a clear error.
- View model tests: starting a job adds it to the jobs panel; progress updates; cancel calls through;
  crash banner appears on worker crash.
- No UI dispatcher work item over 100 ms while a 5M synthetic job runs (measured by a dispatcher watchdog in
  Debug builds, logged).

**Dependencies**: 0.1, 0.2.

**Risks and open questions**
- StreamJsonRpc formatter choice (default JSON is used; see ARCHITECTURE.md 4.4).

**Decisions** (decided autonomously)
1. Default StreamJsonRpc JSON formatter with `HeaderDelimitedMessageHandler`; `IProgress<T>` and
   `CancellationToken` marshaling come for free.
2. One worker per UI or CLI instance, owned by Application's `WorkerHost` (the only place that launches it).
3. Result set rows cross the pipe as string cells with typed column descriptors (`ResultColumn`), so paging is
   generic and stable; clients parse numbers by column kind.
4. Test hooks (`CrashForTestingAsync`, `HangForTestingAsync`) exist only when the worker starts with
   `--enable-test-hooks`; otherwise they fail with `TestHooksDisabled`.
5. The worker logs single-line records to stderr; `WorkerHost` forwards each line into the client log under the
   `Worker` category (adopts 0.1 note L11, needed for forwarding).
6. Application stays UI-agnostic: `JobTracker` raises events on any thread; the UI marshals through an
   `IUiDispatcher` abstraction (immediate in tests).
7. The responsiveness criterion is checked by a dispatcher watchdog (always on, logs lags over 100 ms) and by an
   automated UI smoke test: `HeapScope.exe --smoke-synthetic <objects>` runs one synthetic job, records the
   maximum dispatcher lag, and exits with code 0 (ok), 3 (over the gates of decision 10) or 4 (job failed).
8. The CLI attaches to an already running worker with `--pipe <name>` (debugging), otherwise launches its own.
9. File logs: `%LOCALAPPDATA%\HeapScope\logs\heapscope-<yyyyMMdd>.log`, overridable with `Logging:File:Directory`.
10. The smoke run checks the budget's subject hard: no single dispatcher operation over 100 ms (exit 3). Probe lag,
    which also contains OS scheduling noise, is gated at 2x the budget (200 ms). Steady state measured 2 to 16 ms;
    under the full integration suite a single 105 to 130 ms probe lag appears intermittently at job completion with
    no UI GC and no long dispatcher operation (cause not proven; candidates: Server GC threads of the worker, CPU
    contention). The budget is unchanged and every breach is logged. Deferred to backlog, decided autonomously;
    flagged for the product owner to confirm the deferral.
11. The worker runs at below-normal process priority, so its ordinary threads yield to the UI and the profiled app.
    This does not cover its Server GC threads (created at highest thread priority by the runtime); see backlog.
12. `HeapScopeErrorCode` values start at 1000; the first version used -32000 and up, which StreamJsonRpc reserves
    (found by the paging test: -32003 came back as a serialization error).
13. One-time UI cold costs (first window render about 1 s, first job row about 130 to 190 ms) are outside the
    smoke measurement (a warm-up job runs first) and recorded as a backlog item (ReadyToRun, template preload).

**Task breakdown**
1. [x] Contracts: `IWorkerService`, `WorkerProtocol`, DTOs, `HeapScopeErrorCode`, wire enums
   (`SnapshotFeatures`, `SnapshotKindDto`) and `CapabilityDescriptions`. Tests: mirror values test in
   Architecture.Tests.
2. [x] Worker: pipe server hosted service, `WorkerService`, `ResultSetStore` (LRU), throttled progress,
   synthetic job, test hooks, single-line stderr logging. Tests: integration.
3. [x] Application: `WorkerHost` (launch or attach, handshake, crash detection, restart, stderr forwarding,
   shutdown), `WorkerCrashedException`, `WorkerOperationException`, `JobTracker`, `FileLoggerProvider`, DI
   registration. Tests: JobTracker and file logger unit tests; WorkerHost integration tests.
4. [x] UI: shell with navigation, Home (synthetic job), Jobs, About pages, jobs panel, worker status, crash
   banner, `IUiDispatcher`, dispatcher watchdog, smoke mode. Tests: view model tests; UI smoke integration test.
5. [x] CLI: `worker ping`, `worker info`, `synthetic`, Ctrl+C cancellation, `--pipe` attach. Tests: integration.
6. [x] Integration tests for every acceptance bullet; docs.

**Review summary**: 2 High, 12 Moderate, 13 Low (docs/reviews/sprint-0.3.md). High fixed and confirmed: result
sets became typed, page-formatted sources with a 256 MB byte budget (H1), and the worker client error contract is
closed with last-chance handlers in UI and CLI (H2). Moderate fixed now: worker instance id and generation,
hard per-operation smoke check, compacting GC after large jobs, synthetic memory projection and serialization,
stop and dispose races, bounded stop and handshake, CLI catch-all, real handshake mismatch test, central error
mapping, priority wording. Deferred (decided autonomously, flagged): intermittent completion lag, UI cold start,
GC thread priority, richer result kinds and re-query handles (1.3). Bugs found while building: StreamJsonRpc
reserved error codes, a probe counted across a watchdog reset.

---

## Sprint 0.4: Fixtures and test harness

**Goal**: deliberately leaky apps with known, scalable leaks and a way to produce real Small and Medium dumps.

**Scope**
- `fixtures/LeakyApi` (ASP.NET Core minimal API) with five switchable leaks, each with a configurable target
  size: growing static cache, event handler subscription leak, captured closure in a long-lived delegate,
  undisposed objects held in a list, unbounded `ConcurrentDictionary`.
- `fixtures/LeakyConsole`: the same leak implementations in a console app, sized by arguments, then waits for a
  dump.
- Shared leak implementations in `fixtures/Leaks` so both apps leak identically.
- `fixtures/LoadGen`: drives LeakyApi at a given rate and duration; optional `--dump <path>` captures a full
  dump of the target when done (via Microsoft.Diagnostics.NETCore.Client).
- `fixtures/scripts/New-SampleDump.ps1 -Tier Small|Medium`.
- `fixtures/KnownAnswers/<leak>.json` per leak: leaking type(s), expected instance count formula, expected
  minimum retained bytes, root kind and root detail, retention path shape.
- Two extra semantic fixtures (from the 0.1 review): a `ConditionalWeakTable` whose key dies (its values must not
  appear retained) and a class holding a struct holding a reference plus a `KeyValuePair<,>[]` (embedded value
  type references must be edges).
- Integration test harness: start a fixture, wait until ready, capture a dump, clean up processes and files.

**Out of scope**: HeapScope's own loader (1.2); the harness validates known answers with ClrMD directly.

**Acceptance criteria**
- Each leak can be enabled independently and sized; disabled leaks allocate nothing.
- Integration test per leak: run LeakyConsole with a small size, capture a dump, verify with ClrMD that the
  known-answers file is correct (instance counts and root kinds).
- The Small script produces a dump of about 500 MB (300 to 900 MB accepted) with all leaks active.
- The Medium script produces a dump of about 4 GB (3 to 6 GB accepted); run once, timing recorded.
- Dumps land in `artifacts/dumps/` which is git-ignored.
- Fixture processes are always cleaned up, even when a test fails.

**Dependencies**: 0.1.

**Risks and open questions**
- Dump capture of a child process requires same user and no elevation; the harness checks and explains.

**Decisions** (decided autonomously)
1. Fixture projects: `fixtures/Leaks` (class library, namespace `Fixtures.Leaks`, shared by both apps),
   `fixtures/LeakyConsole`, `fixtures/LeakyApi` (ASP.NET Core minimal API), `fixtures/LoadGen`. They live outside
   `src/`, so the layering rules do not apply; they may use diagnostics packages directly.
2. Every leak is rooted by a static field of its leak class, so the known answers name the root precisely. Each
   item creates a small object cluster (about 4 to 5 objects, about 200 bytes) so dumps reach high object counts
   at moderate size; the payload size is configurable.
3. The event handler leak spreads subscribers over 16,384 long-lived publishers: one multicast delegate with
   millions of targets would make every subscription copy the whole invocation list (quadratic).
4. LeakyApi grows every enabled leak by `Leaks:ItemsPerRequest` on each `POST /work`; `Leaks:Enabled` lists the
   leaks (`all` for every leak); disabled leaks are never touched.
5. LoadGen captures dumps with Microsoft.Diagnostics.NETCore.Client (`--dump <path> --pid <pid>`), full dumps by
   default; integration tests use heap-only dumps (`WithHeap`) to keep them small and fast.
6. Integration tests share one LeakyConsole dump per test class (all leaks plus the semantic fixtures) and verify
   the known answers with ClrMD directly (HeapScope's loader arrives in 1.2). Path shapes are recorded now and
   verified when retention paths exist (2.2).
7. Sample dumps go to `artifacts/dumps/` (git-ignored). The Medium dump is produced once on this machine and kept
   there for sprint 1.2.
8. LoadGen drives a request count at a concurrency, not a rate and duration: the fixtures need a known item
   count, which a count gives exactly.
9. (From the review) Known answers use format version 1 with a path shape grammar, retained bytes anchored on the
   item instance, a per-item census, runtime root and growing collection, all verified with ClrMD now rather than
   in 2.2 and 2.3.
10. (From the review) Fixtures are always compiled optimized: in debuggable code ClrMD reports no stack roots for
    some frames (measured). Fixtures exit when their parent process exits.
11. (From the review) Fixtures for 1.6 and 2.6 (duplicate strings, timers, disposed-but-referenced objects, a
    non-leaking LeakyApi workload for the clean run) are added by those sprints; recorded in their scopes.

**Task breakdown**
1. [x] `fixtures/Leaks`: five leaks (static cache, event handler, captured closure, undisposed list, unbounded
   concurrent dictionary) and the semantic fixtures (conditional weak table, embedded struct references), sized by
   item count and payload. Tests: integration (known answers).
2. [x] `fixtures/LeakyConsole` and `fixtures/LeakyApi`. Tests: integration (console); API exercised by the
   Small script run.
3. [x] `fixtures/LoadGen` with dump capture. Tests: Small script run.
4. [x] `fixtures/KnownAnswers/*.json` for every leak and semantic fixture. Tests: parse and verify with ClrMD.
5. [x] Integration harness (`FixtureDump`): start, wait for ready, capture, clean up processes and files.
6. [x] `fixtures/scripts/New-SampleDump.ps1` for Small and Medium; both run once, sizes and timings recorded.

**Review summary**: 1 High, 12 Moderate, 12 Low (docs/reviews/sprint-0.4.md). High fixed and confirmed:
retained bytes are anchored on the item instance, two bounds corrected, all verified with ClrMD. Moderate, mostly
fixed now: known answers format version 1 (path grammar, census, runtime root, multiple roots), multicast event
paths, exclusivity and runtime root tests, a reusable harness with LeakyApi and two-snapshot growth tests, late
garbage and runtime kind fixtures, benchmark on private bytes with a cache cap, script hardening, self-ending
fixtures. Findings that changed the design: static storage is held by strong handles on .NET 10;
SuppressFinalize leaves objects in the finalization queue (header bit); ClrMD misses stack roots of debuggable
frames; ConcurrentDictionary nodes are recreated on resize; a 512 MB ClrMD cache cap keeps the Medium walk at 2.1
GB private bytes with no slowdown. Deferred to 1.6 and 2.6: their fixtures.

---

## Sprint 0.5: Index cache format

**Goal**: persist and reopen the compact graph quickly, safely and in both memory modes.

**Scope**
- Storage: `IndexCacheWriter`, `IndexCacheReader`, header, section directory, page-aligned sections,
  XxHash3 checksums, fingerprint, atomic write, fallback cache location.
- `Column<T>` mapped backing (read-only memory-mapped views); `HeapGraph` opened in-memory or mapped.
- Memory mode selection policy (`MemoryBudget`), configurable.
- Benchmark: write and reopen the synthetic 50M-object graph in both modes.
- From the 0.2 review: lease shape on `ISnapshot` (no-op for array-backed graphs), and an explicit reserved field
  in `LargeObjectSize` so persisted bytes are deterministic.

**Out of scope**: reverse reference and dominator sections (2.x), loader integration (1.2).

**Acceptance criteria**
- Round-trip tests: every array, type, root and string survives write and read, in both modes, for empty,
  small and random graphs.
- A changed snapshot (size or timestamp or content fingerprint), a different format version or a corrupted
  section all make the cache invalid, with a reason; never a crash or wrong data.
- A crash during write (simulated) leaves no file that is accepted as valid.
- Reopen of the synthetic 50M graph: in-memory under 20 s, mapped under 2 s (measured, recorded).
- Mapped mode worker private memory for the 50M graph stays under 1 GB after open (measured, recorded).
- Fallback location used when the snapshot folder is read-only.

**Dependencies**: 0.2.

**Risks and open questions**
- Antivirus scanning of large new files can slow the first reopen; measured and documented.

**Decisions** (decided autonomously)
1. File layout: a 4096 byte header region (fixed header plus section directory with a checksum over both), then
   page-aligned sections in the in-memory element layout. The header is written last, after all sections, so a
   file interrupted at any point has no valid magic and is rejected.
2. Metadata (`SnapshotInfo`, capabilities, writer version, dropped counts) is a small UTF-8 JSON section; strings
   are one section (count, offsets, UTF-8 bytes) and are always loaded into managed strings.
3. Every section's XxHash3 checksum is verified at open in both modes. In mapped mode this reads the file through
   the OS page cache (file-backed pages, not private memory), which keeps the private memory budget; the cost is
   measured against the 2 s mapped reopen budget.
4. In mapped mode the per-object columns, edges and roots are views of the file; the small tables (types, large
   sizes, strings) are copied. A mapped file stays open (and cannot be replaced) while the graph is open.
5. Memory mode: `MemoryBudget` = fraction (default 0.5) of total physical memory, capped by available memory;
   in-memory when the projected column bytes fit, mapped otherwise, or forced by configuration.
6. Leases: `ISnapshot.AcquireLease()` returns a lease; disposing a snapshot waits for outstanding leases before
   releasing its memory (ARCHITECTURE.md 6.5). `GraphSnapshot` implements it with a shared `LeaseTracker`.
7. Fallback location: the cache sits next to the snapshot when that folder accepts a probe file; otherwise under
   `%LOCALAPPDATA%\HeapScope\cache\<sha256 of the full snapshot path>.hsidx` (root configurable).
8. Writes go to a temporary file in the target folder, are flushed to disk, then renamed over the target.
9. (From the review) Optional 2.x indices go into sidecar files that name the base cache by its identity (its
   header checksum); the base file is never rewritten. Decision 3 changed: mapped mode verifies with a sequential
   read before mapping. Decision 6 changed: disposal never blocks; the last lease released disposes the graph.
10. (From the review) The memory budget reads live physical memory from the operating system
    (GlobalMemoryStatusEx), with a GC hard limit only as an extra cap.
11. (From the review) Deferred to 2.1: a public opened-cache API with optional sections and an incremental section
    writer. Deferred to the backlog: cold page cache and antivirus reopen measurements.

**Task breakdown**
1. [x] Core: explicit reserved field in `LargeObjectSize`; `LeaseTracker`, `SnapshotLease`,
   `ISnapshot.AcquireLease`. Tests: layout, lease drain on dispose.
2. [x] Storage: format constants, `SnapshotFingerprint`, section directory, `IndexCacheWriter` (atomic, chunked,
   checksummed, header last, cancellation). Tests: round trips, determinism, cancellation leaves nothing.
3. [x] Storage: `IndexCacheReader` in-memory and mapped (`MappedMemoryManager<T>`), validation with reasons.
   Tests: every invalidation reason, truncation, corruption of each section.
4. [x] Storage: `IndexCacheLocator` with fallback; `MemoryModeSelector`. Tests: fallback and selection.
5. [x] Benchmark `index-cache` (write, reopen in both modes, private bytes); numbers recorded in
   ARCHITECTURE.md 7 and 10.

**Review summary**: 2 High, 8 Moderate, 9 Low (docs/reviews/sprint-0.5.md). High fixed and confirmed: optional
indices move to sidecar files keyed by the base cache identity (H1), and the reader validates every directory
entry overflow-safely on one file handle, so hostile or replaced files return a reason instead of crashing the
worker (H2). Moderate fixed now: non-blocking reference-counted disposal, sequential verification for mapped
opens (0.6 s, +202 MB working set instead of 1.4 s and +1.6 GB), live memory budget, overflow-safe string and
metadata decoding, stale temporary file cleanup and `IndexCacheInUseException`, malformed-file tests in both
modes. Deferred: public opened-cache API and incremental writer (2.1), cold-cache and antivirus measurements
(backlog). Found while fixing: a static test hook leaked into parallel tests (now flow-local).

---

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

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

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

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

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

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 1.5: Object search and inspection

**Goal**: inspect individual objects and walk outgoing references.

**Scope**: instances of a type (paged), field values including nested structs, arrays, strings; follow
outgoing references; back and forward navigation.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): an instances grid (address, bytes, later retained bytes); filters by string value, regex and address; outgoing references as fields plus a references list; one drill-down action everywhere (double-click or Enter opens the row as a new subject); back and forward buttons (and mouse buttons).

From the 1.2 review: the full-dump `ISnapshot` that reopens the dump with ClrMD for `FieldValues`, `StringContents` and `ThreadStacks` (`IFieldValueReader` and the string and thread stack services), so full dumps stop reporting them as missing; ClrMD runtime creation failures wrapped with a next step.

**Acceptance criteria**
- Field values of a known LeakyConsole object match the values the fixture set.
- Nested struct fields are shown as nested rows.
- Field inspection is disabled with an explanation for snapshots without `FieldValues`.

**Dependencies**: 1.3.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 1.6: Simple inspections

**Goal**: one-click findings for common waste.

**Scope**: inspection plugin model in Analysis; duplicate strings (with wasted bytes), LOH contents, empty and
oversized collections, large arrays; UI list of inspections; CLI `inspect <name>`. Fixtures and known answers (format
version 1, kind `inspection`): repeated strings, empty and oversized collections, deliberate LOH arrays.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): inspections as overview tiles: count, wasted bytes, one-line cause and fix, a link to the objects; add sparse arrays, finalizable objects and a fragmentation bar (pinned, unpinned, free) as cheap wins.

**Acceptance criteria**
- Each inspection has synthetic-graph tests with known answers and a fixture integration test.
- Duplicate strings requires `StringContents` and is disabled otherwise.

**Dependencies**: 1.5.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

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

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 1.8: gcdump support

**Goal**: capture and load gcdumps behind the same snapshot abstraction.

**Scope**: gcdump capture; `HeapScope.Loaders.GcDump`; capability flags drive the UI; diffs across gcdumps and
mixed kinds.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): views a gcdump cannot answer stay visible but disabled, with the capability reason.

From the 1.2 review: a loader contract in Core (`LoadResult`, `LoadProgress`, `LoadDiagnostics`) shared by both loaders, and the snapshot store picking the loader by file signature (minidump `MDMP` or gcdump).

**Acceptance criteria**
- gcdump of LeakyConsole: type counts match known answers when not sampled.
- Field inspection and duplicate strings are disabled with an explanation.
- Diff of two gcdumps ranks the leaking type first.

**Dependencies**: 1.2, 1.4.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 2.1: Reverse references

**Goal**: answer "who references this object or type".

**Scope**: incoming-reference CSR built in the worker and cached as an optional section; queries by object and
by type. From the 0.5 review: the optional section is a sidecar file naming the base cache identity; a public
opened-cache API (`IndexCacheFile`, `TryGetSection`) and an incremental section writer come first.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): an incoming references grid per object and per type.

From the 1.2 review: `IndexCacheWriter.Write` returns the cache identity; the worker keeps it per snapshot and the descriptor carries it, so a reopen can tell a changed file and sidecars can name their base. Reachability treats frozen objects as alive (6.1).

**Acceptance criteria**: synthetic tests with known in-degrees; Medium build time and memory recorded within
budget; cached section reused on reopen.

**Dependencies**: 0.5, 1.2.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 2.2: Retention paths

**Goal**: explain why an object or type is alive.

**Scope**: shortest paths to GC roots, root kinds in plain language, top N distinct paths, framework noise
collapsed, field names resolved on demand for full dumps.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): a merged paths tree from the object toward its roots with a reachable roots count; the top distinct paths presented as key retention paths; plain-language root kinds.

**Acceptance criteria**: each LeakyApi leak's path matches its known-answers path shape and root kind.

**Dependencies**: 2.1.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 2.3: Dominator tree and retained size

**Goal**: retained size per object and per type.

**Scope**: iterative dominator computation over the CSR, retained sizes, cached section.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): retained bytes columns wherever sizes appear; an "open objects retained by this set" action; a largest retained size tile.

**Acceptance criteria**: synthetic graphs with known dominators (including cycles and diamonds); known-answer
retained sizes for fixtures; Medium within memory budget in both modes.

**Dependencies**: 2.1.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 2.4: Retention explorer UI

**Goal**: explore what retains memory.

**Scope**: drill-down dominator tree, biggest retainers, grouping by type and namespace, cross-links between
summary, diff, inspection and retention views.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): a dominator tree with tree and path modes plus an icicle chart (simpler than a sunburst in WPF, same data); click highlights a path, double-click zooms; a biggest retainers tile.

**Acceptance criteria**: every LeakyApi leak is reachable from "biggest retainers" in three clicks or fewer.

**Dependencies**: 2.2, 2.3.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 2.5: Allocation tracking

**Goal**: show where allocations come from.

**Scope**: EventPipe allocation sampling with call stacks, symbol resolution, top call stacks and types over a
time range.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): select a timeline interval, then types with back traces below and a call tree with total and own columns.

**Acceptance criteria**: LeakyApi's leaking allocation site appears in the top call stacks under LoadGen.

**Dependencies**: 1.7.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 2.6: Smart leak detectors

**Goal**: name the leak, not just show the data.

**Scope**: event handler leaks, disposed-but-referenced objects, timers keeping objects alive, static
collections growing across snapshots, HttpClient misuse, common ASP.NET Core patterns. Fixtures first: a timer leak,
disposed-but-referenced objects, and a non-leaking LeakyApi workload (bounded cache with eviction, subscribe then
unsubscribe, dispose and release) for the "no detector fires" run.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): one card per detector (cause, fix, link to the objects), the 1.6 presentation; event handler leaks and the WPF binding, collection binding, dependency property and x:Name leaks.

**Acceptance criteria**: every LeakyApi leak is detected with its known answer; no detector fires on a clean
LeakyApi run.

**Dependencies**: 2.2, 1.4.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Sprint 2.7: Navigation and polish

**Goal**: a coherent, keyboard friendly tool.

**Scope**: full navigation history, object bookmarks, consistent cross-view linking, keyboard support, CSV
export of any grid.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): an analysis path sidebar (branch from any earlier step), analysis tabs, object marks across snapshots, the full filter syntax, keyboard shortcuts, CSV export.

**Acceptance criteria**: every grid exports to CSV; every view reachable by keyboard; history survives view
switches.

**Dependencies**: 2.4.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Backlog

**High**
- Intermittent dispatcher lag: re-measured in 1.2 with real loads (smoke open of Small and Medium: probe lag 88 to
  133 ms, longest operation 48 to 61 ms, inside the gates).
- Intermittent 105 to 130 ms dispatcher lag at job completion under heavy machine load (from 0.3): find the
  cause (likely CPU contention with the worker) and bring the smoke gate back to 100 ms. Capture a WPR or PerfView
  trace (CPU sampling, context switches, ready thread) of the smoke run and log the UI thread's CPU time per lag.
- Worker Server GC threads run at normal priority despite the below-normal process (from 0.3): evaluate a job
  object CPU rate, EcoQoS or GC heap affinity once a trace shows them preempting the UI.
- Remote git, `develop`/`release` branches, CI pipeline, CI leak checks using the CLI.
- Trend analysis across more than two snapshots.
- Finalizer queue and finalizable object analysis.
- Async state machine and Task leak detection.

**Medium**
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
- HTML report export.
- .NET Framework target support.

**Low**
- UI cold start: first window render about 0.6 s after ReadyToRun (1.2), dominated by runtime and WPF start; first
  job row 130 to 190 ms once per run. Template preloading and deferring startup work remain options (from 0.3).
- WPF and WinForms specific leak inspections.
- Unmanaged/native memory overview.
- Linux dump analysis.
- Attach to processes in containers.
