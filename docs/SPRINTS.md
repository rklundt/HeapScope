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
| 2.6 | Smart leak detectors | Closed | 2026-10-26 | sprint-2.6 |
| 2.7 | Navigation and polish | In Progress | | |
| 2.8 | Packaging and distribution | Planned | | |

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

**Task breakdown**
1. [x] Incremental section writer: `IndexCacheFileWriter` (temporary file, sections one at a time, header last, flush and
   rename) used by `IndexCacheWriter`, whose `Write` now returns the cache identity; the header's last reserved field
   becomes `BaseIdentity` (0 in a base cache, no format change). Tests: the existing writer tests unchanged (identical
   bytes), the returned identity equals the reader's, a disposed uncommitted writer leaves no file.
2. [x] Opened-cache API: `IndexCacheFile` opens any HeapScope index file (the base cache's directory checks, shared with
   `IndexCacheReader`), in memory or mapped, with `TryGetSection<T>`, `Identity` and `BaseIdentity`; sidecar sections
   `InEdgeStarts` and `InEdges`; reason `BaseMismatch`; `IndexCacheLocator.GetSidecarPath`. Tests: a sidecar round trip
   in both modes, a wrong base identity, a missing or unknown section, a checksum mismatch, a base cache refused as a
   sidecar.
3. [x] Core `ReverseReferences`: built by counting in-degrees and filling (referrers of each object ascending, one entry per
   reference), from columns for a cache, `GetReferrers`; `HeapGraph.Attach` disposes an index with the graph after the
   last lease. Tests: chain, diamond, cycle, self loop, duplicate references, a seeded random graph against a naive count.
4. [x] Analysis `IncomingReferences`: per object (distinct referrers with their reference counts) and per type (referrers
   grouped by their type: referring objects, references, targets referenced, referrer bytes). Tests: known answers on
   synthetic graphs, types matched by name and module.
5. [x] Worker: the cache identity kept per snapshot and carried by the descriptor; the reverse index as a derived slot,
   opened from its sidecar or built on first use, written as a sidecar when the base cache was written, kept in the
   snapshot's memory mode (mapped when it does not fit the budget), build time and memory logged. Tests: built once and
   shared, the sidecar reused on reopen, a stale sidecar (other base) ignored and rebuilt, both memory modes.
6. [x] Contracts and RPC (protocol 7): `QueryReferrersAsync` (an object's referrers: field, address, type, bytes,
   references) and `QueryTypeReferrersAsync` (a type's referrers by type, sortable). Tests: worker tests on synthetic and
   fixture snapshots, unknown address and type.
7. [x] Application and CLI: `SnapshotSession` methods; `heapscope-cli referrers <snapshot> --address | --type [--module]`
   with table, csv and json. Tests: CLI integration on the Small dump and a gcdump.
8. [x] UI: the object view's references as Outgoing and Incoming tabs (incoming loaded when first shown); the instances
   view's "Referenced by" opens a referrer types grid whose rows open that type's objects. Tests: view model tests;
   `--smoke-open` on the Small dump.
9. [x] Known answers: incoming references of LeakyApi leak objects (referrer types per leak) in the integration tests.
10. [x] Measurement: the reverse index on the Medium dump (build, write, reopen from the sidecar, both modes; time and
   memory) recorded in ARCHITECTURE 10.
11. [x] Docs: ARCHITECTURE (6.4, 7, code map, protocol), DEVELOPER, CLAUDE.md.

**Decisions** (Phase 2 autonomy granted by the product owner on 2026-10-24: "go - and yes autonomous - one sprint at a
time", so the sprint stops after its close; each default below was decided autonomously)
1. The reverse CSR keeps one entry per reference, so a referrer holding an object in two fields appears twice; the
   object grid shows each referrer once with a References count and its field paths joined.
2. Incoming rows of a full dump name the referrer's field that holds the object, for up to 1,000 referrers (the array walk
   capped as for outgoing references); beyond that and for gcdumps the column is empty and the totals say why.
3. The per-type grid groups referrers by their type (name and module, ADR-018's key) with referring objects, references,
   targets referenced and referrer bytes; opening a row shows that type's objects (all of them; "only those referencing
   the type" goes to the backlog).
4. Roots are not rows of the incoming grid in 2.1; 2.2 explains roots in plain language. The object summary already says
   when a root holds it.
5. The reverse index is built on first use (an incoming query), not at open. It is written as a sidecar only when the
   base cache was written; it follows the snapshot's memory mode and goes mapped when it would not fit the budget; a
   sidecar naming another base identity is ignored and rebuilt (a rebuilt base cache has a new identity).
6. The descriptor carries the base cache identity (1.2 review); the protocol version goes to 7.
7. Self references count as incoming references.
8. The frozen-objects-alive rule (6.1) applies when reachability is computed (2.2, 2.3); 2.1 computes none.
9. Like Phase 1 sprints, Moderate review findings are decided with their recommendation and logged; the sprint stops and
   reports after `/close-sprint`.

**Review summary** (`docs/reviews/sprint-2.1.md`): 0 High, 11 Moderate, 20 Low after dedupe. Decided autonomously: 9
Moderates fixed now, M4 (label dependent-handle referrers) deferred to 2.2, and M2's build-into-the-sidecar part to 2.3.
Fixed:
- M1: in-memory sidecar sections allocate by element type, so a section holds 2^31 - 1 elements, not 2^31 bytes.
- M2: going mapped maps the sidecar first and keeps the built index if that fails; no compacting collection (the release
  takes 7 to 14 ms).
- M3: the index's mode charges what the worker already commits and honors a forced mode.
- M5: field naming walks at most 2M array elements per query.
- M6: worker tests for every index path, the shared build and the mode choice.
- M7: progress goes to every waiter and the UI shows it.
- M8: an unknown type or sort column is answered before the index is built; the CLI exits 1 on an unknown type.
- M9: results say when the graph is incomplete.
- M10: a reopen refuses a rebuilt cache.
- M11: the release pause, the DenseHeap mapped peak (1,695 MB) and a Release worker run (build 1.45 s, peak commit 2,644 MB
  on DenseHeap) are measured, and the budget claim is qualified (32 GB machine).

Measured: Medium index 470 MB, built in 0.6 to 0.8 s; DenseHeap 831 MB in 0.8 to 1.7 s; reopen from the sidecar 0.1 to
1.1 s. Tests: 792 in the final gate (from 731 at 1.9), all passing; the first gate had one load flake in a worker IPC test.

---

## Sprint 2.2: Retention paths

**Goal**: explain why an object or type is alive.

**Scope**: shortest paths to GC roots, root kinds in plain language, top N distinct paths, framework noise
collapsed, field names resolved on demand for full dumps.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): a merged paths tree from the object toward its roots with a reachable roots count; the top distinct paths presented as key retention paths; plain-language root kinds.

From the 2.1 review (M4, deferred here): dependent-handle edges are ordinary CSR edges, so a `ConditionalWeakTable` key
shows as a plain referrer of its value with an empty field. Record which edges come from dependent handles (a sorted
list of pairs, a loader version bump) so referrers and retention paths label them "(dependent handle)", and check the
label on the ConditionalWeakTable known answer.

**Acceptance criteria**: each LeakyApi leak's path matches its known-answers path shape and root kind.

**Dependencies**: 2.1.

**Task breakdown**
1. [x] Dependent-handle edges recorded: `HeapGraphBuilder` keeps the sorted (primary, secondary) pairs it merged into the
   CSR, `HeapGraph.DependentHandles` and `IsDependentHandleEdge`, a base cache section and format version 2 (older caches
   are rebuilt once). Tests: builder and synthetic graphs, cache round trip in both modes, a version 1 cache refused.
2. [x] Referrers label dependent-handle edges "(dependent handle)" when no field holds the object (2.1 review M4). Tests:
   worker source test; the ConditionalWeakTable known answer in the integration tests.
3. [x] Analysis: plain-language root descriptions (`RootDescriptions`); `RetentionSearch`, a backward breadth-first search
   from one object over the reverse index that stops at retaining roots and frozen objects, finding up to N roots with
   their shortest paths merged into one tree; `RootPathTree`, one forward breadth-first search from every retaining root
   (static fields first) and every frozen object, giving each object its shortest path from a root; `KeyPaths`, a type's
   objects grouped by the type sequence of their shortest path (repeats of one type folded, framework steps marked).
   Tests: chain, diamond, cycle, self loop, unreachable objects, a frozen holder, a static field preferred over the
   storage handle, a dependent-handle step, repeated types, search budgets.
4. [x] Worker and Contracts (protocol 8): `QueryObjectPathsAsync` (the merged tree with fields named on full dumps and the
   key paths among the roots found) and `QueryTypePathsAsync` (key paths with object counts, bytes and an example object,
   plus the unreachable objects); the root path tree as a derived slot with shared progress. Tests: worker tests on
   synthetic snapshots; unknown address and type.
5. [x] Application and CLI: `SnapshotSession` methods; `heapscope-cli paths <snapshot> --address <hex> | --type <name>
   [--module] [--max N] [--format text|json]`. Tests: CLI integration on a full dump and a gcdump.
6. [x] UI: the object view's Retention tab (key paths and the merged tree toward the roots, loaded when first shown) and
   the instances view's "Why alive" subject (a type's key paths, a row opens its example object). Tests: view model tests;
   `--smoke-open` on the Small dump.
7. [x] Known answers: every rooted leak's item path matches its path shape and root kind, including the
   ConditionalWeakTable step "(dependent handle)"; a type's key paths name the leak's root; late garbage is unreachable.
8. [x] Measurement: the root path tree and the key paths on Medium and DenseHeap (time, memory), an object's paths query.
9. [x] Docs: ARCHITECTURE (6.1, 6.4, 7, code map, protocol 8, section 10), DEVELOPER, CLAUDE.md.

**Decisions** (Phase 2 autonomy, one sprint at a time: the product owner said "go" for 2.2 on 2026-10-24; each default
below was decided autonomously)
1. A shortest path counts references. Only retaining roots and frozen objects (never collected, 6.1) start paths; weak
   handles never do. When several roots are equally close, static fields come first, then thread stacks, handles, the
   finalizer queue, other roots and frozen objects, so a static's value is explained by its field and not by the
   runtime's storage handle.
2. An object's paths: up to 10 roots found by the backward search, which visits at most 1,000,000 objects; the result says
   when the search stopped early. A root's own object has a path of length zero.
3. A type's key paths: the 20 largest groups by object count; the rest are summed in one line. Groups use the type
   sequence of the shortest path, with repeats of one type folded (a long linked list is one step) and the root's kind
   (and field, for statics).
4. Framework noise: steps whose type is in a `System.` or `Microsoft.` namespace and lie between two other steps are marked
   as framework steps; the UI and CLI fold a run of them into "(N framework objects)", which expands in the tree. The data
   keeps every step.
5. The root path tree (4 bytes per object) is kept in memory for the snapshot's lifetime, not written to disk in 2.2;
   building it needs one more 4 bytes per object for the queue.
6. Dependent-handle pairs go into the base cache (a section, format version 2). Existing caches are rebuilt once on their
   next open, as when the loader version was bumped.
7. Field names on path steps for full dumps, with the 2.1 budget of array elements per query; gcdumps have none. A step
   through a dependent handle with no field is labeled "(dependent handle)".
8. Unreachable objects (garbage not yet collected in a full dump) get no path and say so; a type's key paths count them on
   their own line.
9. The acceptance check runs on the shared all-leaks LeakyConsole dump, whose leaks are the same `Fixtures.Leaks` code
   LeakyApi runs (ADR-013).
10. As before, Moderate review findings are decided with their recommendation and logged; the sprint stops and reports
    after `/close-sprint`.

**Review summary** (`docs/reviews/sprint-2.2.md`): 3 High, 9 Moderate, 19 Low after dedupe.

All 3 Highs are fixed and confirmed:
- H1: an object's tree had no size or depth limit. The worker now folds runs of one type, cuts long paths around a gap,
  and stops at 2,000 nodes. Segments merge only when identical, and key path steps are capped at 64.
- H2: a search stopped at its limit called a live object unreachable. Reachability now comes from the root path tree,
  the search is pruned to reachable objects, and the tree's path is the fallback.
- H3: the CLI tree printed holders under the wrong parent.

All 9 Moderates were fixed, decided autonomously:
- the root path tree is checked against the memory budget and its queue released;
- one definition of path roots, with a semantics version;
- unreachable explained by snapshot kind;
- worker, root kind, gcdump CLI and view model tests;
- the key paths memo capped and its stack reused;
- cancellation per dequeue;
- measurements re-run.

Measured (32 GB machine), peak in memory and mapped:
- Medium (57.5M objects): root path tree 0.8 to 1.2 s, key paths up to 1.7 s, an object's paths at most 6 ms; peak
  2,802 MB and 1,197 MB.
- DenseHeap: tree 1.6 to 2.3 s; peak 3,320 MB and 1,416 MB.

---

## Sprint 2.3: Dominator tree and retained size

**Goal**: retained size per object and per type.

**Scope**: iterative dominator computation over the CSR, retained sizes, cached section.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): retained bytes columns wherever sizes appear; an "open objects retained by this set" action; a largest retained size tile.

From the 2.1 review (M2, L16, L17): build optional indices straight into their sidecar file (`IndexCacheFileWriter` as
the file-backed column allocator of 6.3) and check the projected peak (graph, reverse CSR, about 28 bytes per object of
working arrays) against the budget before starting, so a mapped computation never holds a managed copy; give each
sidecar layout a semantics version, so a dominators sidecar computed under older rules (retaining roots, frozen objects
alive, the virtual root) is rebuilt like a cache from an older loader.

**Acceptance criteria**: synthetic graphs with known dominators (including cycles and diamonds); known-answer
retained sizes for fixtures; Medium within memory budget in both modes.

**Dependencies**: 2.1.

**Task breakdown**
1. [x] Storage: sidecars record a semantics version (the header's reserved field, written by `Commit`, checked by
   `IndexCacheFile.Open`; a mismatch is refused and rebuilt; base caches and 2.1 sidecars keep 0, so the format version
   stays 2). `MappedIndexFileWriter`: sections planned up front, a temporary file mapped read-write, writable spans per
   section, then checksums, header last, flush and rename (the crash safety of section 7). The dominators layout
   (`.dom.hsidx`: ImmediateDominators, RetainedSizes). Tests: round trip in both modes, a semantics mismatch refused, a
   writer that never commits leaves no file, a crash before the header leaves no valid file.
2. [x] The reverse index is built straight into its sidecar (2.1 review M2): `ReverseReferences.Build` fills given spans;
   with an index cache it is written through `MappedIndexFileWriter` and then opened in its chosen mode, so a mapped index
   never holds a managed copy. Tests: the existing reverse index tests; the sidecar's bytes equal the previous writer's.
3. [x] Analysis: `DominatorTree` (semi-NCA over the CSR and the reverse index, iterative with no stack at all, a virtual root
   joined to `PathRoots.Sources`), retained sizes in reverse preorder, written into spans the caller gives;
   `RetainedByGroup`, the lower bound per type or group. Tests: chain, diamond, cycle, self loop, a root reached only
   through another root, two roots sharing an object, a list held from both ends, a frozen object, a weak handle, a
   dependent-handle edge, unreachable objects, seeded random graphs against a brute-force dominator check, the group
   lower bound on nested objects of one type.
4. [x] Worker and Contracts (protocol 9): the dominators as a derived slot (projected memory checked first, opened from or
   written into the sidecar, shared progress, its own lease); `IncludeRetained` on type summary and instances requests
   (a `Retained` column); `QueryRetainedAsync` (an object's retained size and immediate dominator). Tests: worker tests
   on synthetic snapshots, the sidecar reused on reopen, a stale semantics version rebuilt, the budget refusal.
5. [x] Application and CLI: `SnapshotSession` methods; `--retained` on `summary`, `instances` and `object`. Tests: CLI
   integration on a full dump and a gcdump.
6. [x] UI: a "Retained sizes" switch on the summary and instances views (a column, sortable, computed with progress); the
   object view's retained size and immediate dominator (a link). Tests: view model tests; `--smoke-open` on the Small dump.
7. [x] Known answers: every rooted fixture item's retained size equals the independent ClrMD computation and is at least
   its `minRetainedBytesPerItem`; a leak type's retained lower bound covers its items; late garbage is unreachable.
8. [x] Measurement: Medium and DenseHeap in memory and mapped (time, peak private bytes, sidecar size, reopen).
9. [x] Docs: ARCHITECTURE (6.4, 7, code map, protocol 9, section 10, an ADR for the retention semantics shared by paths
   and dominators, 2.2 review L6), DEVELOPER, CLAUDE.md, the known answers README.

**Decisions** (Phase 2 autonomy, one sprint at a time: the product owner said "continue" for 2.3 on 2026-10-25; each
default below was decided autonomously)
1. Semi-NCA as chosen in 1.9. The virtual root's successors are exactly `PathRoots.Sources` (retaining roots, then frozen
   objects). An object no root reaches has no dominator (-1) and a retained size of 0; an object dominated only by the
   virtual root (a root's own object, or one held by several roots) has -2.
2. Kept per object: the immediate dominator (4 bytes) and the retained size (8 bytes), in the sidecar `.dom.hsidx`. Its
   semantics version combines `PathRoots.SemanticsVersion` and the algorithm's own version, so a sidecar computed under
   older rules is rebuilt.
3. With an index cache, both optional indices are computed straight into their mapped sidecar file and then opened in
   the chosen memory mode; without one they stay in managed arrays.
4. Before computing, the working arrays (13 bytes per object after the review's M4; 21 before) and any managed outputs
   are checked against what is left of the worker's share; when they do not fit, the worker refuses with InsufficientMemory and suggests reopening
   the snapshot mapped. A live in-memory graph is not switched to mapped mid-session (Backlog).
5. Computed on demand, not at open (4 to 13 s on Medium and DenseHeap): when a retained column or value is first asked
   for; later opens reuse the sidecar.
6. A type's (or namespace's, or assembly's) retained size is a lower bound: the retained sizes of its objects not dominated
   by another object of the same group. Shown as such. Exact retention of a set ("objects retained by this set") is 2.4.
7. The dominator tree drill-down and the biggest retainers tile are 2.4; retained deltas in diffs go to the Backlog.
8. gcdumps are computed the same way; an incomplete graph is noted, as for referrers.
9. The known-answer check compares each item's retained size with an independent ClrMD computation on the shared
   all-leaks dump.
10. As before, Moderate review findings are decided with their recommendation and logged; the sprint stops and reports
    after `/close-sprint`.

**Review summary** (`docs/reviews/sprint-2.3.md`): 2 High, 14 Moderate, 21 Low after dedupe.

Both Highs are fixed and confirmed:
- H1: the dominators benchmark deleted the whole `--out` folder; it now deletes only its own sidecars.
- H2: a null retained cell crashed the CLI's JSON output; a null cell is now JSON null, and 4.6 says any cell may be null.

Twelve Moderates were fixed, decided autonomously:
- the budget is checked before the reverse index is built, reserved while computing, re-checked on the fallback, and the
  sidecar opened after the working arrays are collected; a sidecar another process wrote is used (M1);
- one worker-wide `MemoryReservations` for concurrent large computations (M2), and a collection after every one (M3);
- the semidominators and ancestors live in the retained sizes output (13 bytes per object of working arrays instead of
  21; M4), and the path compression reverses pointers instead of keeping a buffer (M5);
- notes beside retained sizes for incomplete snapshots and a gcdump's unrooted survivors (M6);
- requests checked before any dominators (M7); a failed retained query no longer hides the grids' rows (M8);
- worker tests through `DominatorIndexBuilder` (M9), crash-safety tests of the mapped writer (M10), refusal advice that
  fits the snapshot (M11), and an oracle that treats frozen objects as alive, checked on three items per fixture (M12).

Deferred at the close: stopping a computation nobody waits for (M13), the 16 GB measurement (M14), and gcdump
survivors as extra sources (M6's second half). After the close, at the product owner's request: M13 fixed, M14 measured
under a working set cap (a real 16 GB machine stays in the Backlog), M6's second half kept in the Backlog with a revisit
trigger and the note now giving the bytes affected (review file, "After the close").

Measured after the review (32 GB machine): Medium reverse index and dominators 3.9 s in memory (peak 3,289 MB) and 5.6 s
mapped (peak 699 MB); DenseHeap 4.6 s (3,875 MB) and 7.0 s (661 MB). Every rooted fixture item's retained size equals an
independent ClrMD walk.

---

## Sprint 2.3.5: Cross-platform and sharing decision

**Goal**: decide, before 2.4 adds more UI, how HeapScope reaches Linux and macOS developers and how teams share results.

**Scope**: a spike and a decision, not a port. From the product owner (2026-10-25): the users to reach are Linux developers
(possibly macOS) and teams sharing results. Today everything but the WPF UI targets plain `net10.0`; the Windows-only parts
are the UI, capture (process discovery, dump writing, elevation) and a few native calls in Storage and Application. The
engine stays .NET (ClrMD has no equivalent elsewhere); the open question is the front end.
- Run the CLI and worker on Linux against a Linux core dump of LeakyConsole and check the known answers.
- List what capture needs on Linux (createdump, `/proc`) and macOS.
- Compare the UI routes with one view prototyped each: Avalonia (view models carry over, desktop on all three systems)
  and a TypeScript web front end over the worker (also opens a shared or hosted view for teams).
- Sharing results: what a shared result is (an exported analysis, not a dump), how it stays within the privacy rules
  (ARCHITECTURE 12: snapshot contents are sensitive), and how it relates to the HTML report export in the Backlog.

**Acceptance criteria**: an ADR choosing the UI route and the sharing model; Phase 2 re-planned from 2.4 on accordingly.

**Version**: 2.3.5 (between 2.3.0 and 2.4.0).

**Dependencies**: 2.3.

**Task breakdown**
1. [x] Portability survey: every Windows-only call outside the UI (capture's process discovery, elevation and disk space
   checks; the memory status calls, already guarded), the worker's path, the pipe (already current-user only on every
   system), the DAC locator's local-path rules on Linux and macOS. Written into the ADR.
2. [x] CI on GitHub's hosted runners (`.github/workflows/ci.yml`): Windows builds the solution and runs the fast tests;
   Linux (ubuntu-latest) and macOS (macos-latest, informational: an arm64 runner) build the engine, the worker and the
   CLI, run the engine's unit tests, start LeakyConsole, take a core dump of it with `dotnet-dump` (installed on the
   runner only), and run `heapscope-cli summary`, `instances --retained` and `paths` on it. Tests: the workflow passes on
   the sprint PR (Linux required, macOS reported).
3. [x] Small portability fixes the CI run asks for (the worker's executable name off Windows, the DAC locator's path
   rules, capture refusing clearly where it is Windows-only). Tests: unit tests for each; the CI run.
4. [x] Avalonia prototype (`spikes/AvaloniaGrid`, outside `HeapScope.sln`, not shipped): one window that opens a dump
   through the real `SnapshotSession` and shows the types grid and a type's objects with the existing data-virtualized
   row lists, sorting and the filter, on MIT packages only. Measured on the Small dump: first show, scrolling 1.5 million
   rows, the longest UI-thread block. Licenses of every package checked.
5. [x] Decision: ADR-025 (UI platform: Avalonia, amends ADR-001; the rule for Avalonia updates) and ADR-026 (sharing: an
   exported analysis, analysis only by default); Phase 2 re-planned with a port sprint before 2.4.
6. [x] Docs: ARCHITECTURE (ADRs, 12 privacy for exports), DEVELOPER (CI, the prototype), CLAUDE.md (the Avalonia update
   rule), SPRINTS.

**Decisions** (the product owner answered on 2026-10-25; the rest decided autonomously, marked so)
1. The Linux and macOS check runs on GitHub's hosted runners ("github"), not on this machine: no WSL or Docker here.
   It also starts the CI the Backlog asked for.
2. Avalonia packages may be downloaded for a throwaway prototype (MIT, about 50 MB). The web route is not prototyped:
   the product owner chose Avalonia after the comparison ("avalonia", "port before 2.4").
3. Port to Avalonia before 2.4, if the prototype performs and every package used is MIT; otherwise the product owner
   decides again with the prototype's results.
4. Before any Avalonia update, check that nothing HeapScope uses moved to a paid license (Avalonia sells "Accelerate"
   components; TreeDataGrid moved there). Only MIT components are used (product owner, 2026-10-25).
5. A shared result is an exported analysis, analysis only by default: type summaries, diffs, retained sizes, retention
   paths with type and field names; never string text or field values unless the person exporting opts in for that
   export (product owner: "Analysis only").
6. The prototype lives in `spikes/` outside the solution and is deleted after the port sprint replaces it (decided
   autonomously).
7. macOS runs on an arm64 runner while HeapScope targets x64 (ADR-001): its job is informational (continue on error) and
   its findings go into the ADR (decided autonomously).
8. CI is not yet a required check on the protected branches; making it one is the product owner's call once it is
   stable (decided autonomously).

**Review summary** (`docs/reviews/sprint-2.3.5.md`): 2 High, 13 Moderate, 20 Low after dedupe.
- High, both fixed and confirmed: the local path rule (ADR-017) did nothing on Linux and macOS, now `UnixLocalPath`
  (the mount holding the path, links resolved, types read from `/proc/self/mountinfo`); Linux and macOS cores had no
  truncation check, now `CoreFileHeader`.
- Moderate, fixed (decided autonomously): a DAC paired through `libcoreclr` is checked by its version string outside
  installed runtimes; the runtime version is named from the runtime's folder when ClrMD reads 0.0; CI asserts the known
  answers with `jq`; a core's capture time is marked as the file's time; the missing DAC message says where to open a
  dump of another system or architecture; Unix runtime roots; ADR-026 amended; capture refused off Windows is
  `CaptureFailure.NotSupportedOnThisSystem`; section 1, ADR-007, ADR-012 and CLAUDE.md updated; 2.3.6 to 2.6 re-planned.
- Deferred: the grid adapter's enumeration (2.3.6 scope), Compare's baseline with file-time captures, Unix file
  semantics tests and Capture.Tests on Linux, a Windows CI dump step (Backlog).
- Measured: CI on GitHub's runners reads a `dotnet-dump` core of LeakyConsole on Linux (required) and macOS arm64
  (informational) with the known answers: 2,000 objects per leak type, 64,000 bytes and 148 retained per cache entry,
  the path from `StaticCacheLeak.s_cache`, a complete graph, .NET 10.0.14. The Avalonia prototype on the Small dump:
  zero row list enumerations, 197 MB private (540 MB before the adapter), scroll stalls at most 36 ms, first show
  108 to 162 ms (WPF 140 ms). Local gate before the review: 932 tests, 0 failed, 0 warnings.

---

## Sprint 2.3.6: Avalonia port

**Goal**: the HeapScope UI runs on Avalonia on Windows, Linux and macOS, at parity with the WPF UI (ADR-025).

**Scope**: from 2.3.5 (ADR-025):
- Move the view models out of `HeapScope.UI.Wpf` into a platform-neutral UI library; a new `HeapScope.UI` (Avalonia,
  Fluent theme, MIT packages only) hosts the views. Every current view: capture, sessions, the types summary, compare,
  objects and inspection, inspections, monitoring, retention paths and retained sizes, the about box and attribution
  strip (unchanged, ADR-022).
- Grids keep the data-virtualized row lists through the `IDataGridCollectionView` pass-through proven in the prototype;
  no paid Avalonia component (the update rule, ADR-025).
- The render yield, power throttling (ADR-019, Windows only) and the UI smoke and lag gates move to the Avalonia app;
  UI tests move to Avalonia's headless test platform.
- CI: Linux and macOS also build the UI and run its headless tests.
- Layering: the view model library and the Avalonia UI project get their rows in ARCHITECTURE 3.1 and `LayerRules` (the
  architecture test refuses unknown projects).
- From the 2.3.5 review: the grid adapter's enumeration and `CopyTo` pass through to the row list, which makes a
  placeholder row per index; select all, copy and keyboard paths that enumerate must go through indices or the worker,
  and a probe test (as `RowListProbe` does) proves no grid path enumerates 1.5 million rows. The render yield moves
  into `IUiDispatcher` (Wpf, Avalonia, Immediate) instead of the static `TaskSchedulerExtensions.RenderYield`. Front
  ends ask `ICaptureService` whether capture is available (with the reason) before showing capture views.
- Capture stays Windows only (the views say so off Windows and point to opening a dump taken with `dotnet-dump`).
- Retire `HeapScope.UI.Wpf` and `spikes/AvaloniaGrid` once parity is reached; update THIRD-PARTY-NOTICES.

Added by the product owner before the start (2026-10-26, "add 1-7"):
1. Runnable builds per system: self-contained builds for `win-x64`, `linux-x64`, `osx-arm64` and `osx-x64` (the UI, the
   worker and the CLI), produced by CI as downloadable artifacts. macOS signing and notarization stay in the Backlog.
2. Owner-only files off Windows (2.3.5 review L5): sessions, logs, index caches and sidecars are created readable by the
   user only (files 0600, folders 0700); Windows keeps the profile's ACL.
3. Light and dark themes in Avalonia (the WPF themes' keys and colors), following the system unless `Ui:Theme` says.
4. The first-window time is measured in the smoke gate (the Backlog's cold start item gets an Avalonia number).
5. Compare's default baseline when a capture time is only the file's time (2.3.5 review M4): the snapshot opened first
   is the baseline, and the view says why.
6. A Windows CI step that captures LeakyConsole with the CLI and checks the known answers (2.3.5 review M3).
7. 2.3.5 review cleanups: L2 (one module key in `TypeGroups`), L11 (`ProcessName` through `RecordedPath`), L13 (the
   socket bound), L14 (one public type per file, guard order, `using`), L16 (attach pipe names checked for length).

**Acceptance criteria**: every WPF view and command has its Avalonia equivalent; the smoke gate (first show, scroll, the
longest UI-thread block) meets the WPF numbers on the Small dump on Windows; the UI opens a Linux core dump of
LeakyConsole on the Linux runner (headless) with the known answers; only MIT (or otherwise allowed) packages.

**Version**: 2.3.6.

**Dependencies**: 2.3.5.

**Task breakdown**
1. [x] Groundwork: Avalonia packages in `Directory.Packages.props` (Avalonia, Desktop, Themes.Fluent and DataGrid as in
   the prototype; Avalonia.Headless for tests and the headless smoke run), all MIT and checked by the update rule;
   Avalonia's build telemetry turned off for the repository (an empty `AvaloniaStats` target in
   `Directory.Build.targets`, and `AVALONIA_TELEMETRY_OPTOUT=1` in CI); THIRD-PARTY-NOTICES. Tests: the build log shows
   no `AvaloniaStats` task; a repository test checks the override exists.
2. [x] `HeapScope.UI.ViewModels` (net10.0): the view models, `IUiDispatcher` (with `YieldToRenderAsync`, replacing the
   static `RenderYield`), `IUserInteraction` made asynchronous (Avalonia's dialogs are), the immediate and automatic
   implementations; `ICaptureService.UnavailableReason` (null when capture works, else the reason) used by the Processes
   and Live view models.
   `HeapScope.UI.Wpf.Tests` becomes `HeapScope.UI.ViewModels.Tests` (net10.0, runs on Linux and macOS). The WPF app uses
   the library until it is retired. ARCHITECTURE 3.1 and `LayerRules` get the new rows. Tests: the moved view model
   tests; capture availability; the architecture test.
3. [x] `HeapScope.UI` (Avalonia, `HeapScope` executable): `Program`, `App` (host start and bounded stop, last-chance
   handlers, power throttling on Windows per ADR-019), `UiHost`, the services (dispatcher, dialogs, lag watchdog), light
   and dark themes, and the shell window: navigation, worker crash banner, jobs, status bar, back and forward (mouse
   buttons, Alt+arrows) and the attribution strip with the same text, link, place, surface and contrast (ADR-022).
   Tests (`HeapScope.UI.Tests`, headless): the window builds with no binding errors; the attribution checks move to
   the Avalonia markup; both themes define the same keys.
4. [x] Grids: the `IDataGridCollectionView` pass-through (from the prototype), through which no grid path enumerates
   or copies the list (a walk would still make a placeholder row per index, so walks are counted and every smoke run
   fails on one, review M3), worker-side sorting with header arrows, open by double-click or Enter, highlighted filter matches, columns from a result set's column list, column visibility from
   the view model, the heap kinds bar. Whole-list walks are counted (`VirtualCollectionViews.Enumerations`) rather than
   failed, so the tests can prove there are none without breaking the app if a grid path ever does one. Tests
   (headless, 1.5 million rows): first show, scrolling, select all, copy and keyboard moves enumerate nothing; a header
   click sends the column to the sort command.
5. [x] Pages: Home, Processes (capture availability and the `dotnet-dump` hint off Windows), Snapshots (the snapshot
   tab with types, instances, object, referrers, paths and retained sizes; compare; inspections), Live (the timeline
   chart), Jobs, About. Tests: each page builds headless with fake view models and no binding errors; the WPF commands
   and views listed against their Avalonia equivalents in the review file.
6. [x] Smoke and gates: the smoke runs (`--smoke-synthetic`, `--smoke-open`, `--smoke-compare`, `--smoke-trace`,
   `--smoke-live`) and the dispatcher lag checks on Avalonia; `--headless` runs them without a display; the first-window
   time is logged and gated; `build/gate.ps1` runs the Avalonia smoke. Measured on the Small dump against the WPF
   numbers (first show, scroll, the longest UI-thread block). Tests: the gate's smoke steps.
7. [x] Compare's baseline with file-time captures (added item 5). Tests: the compare view model picks the first opened
   and says why.
8. [x] Owner-only files off Windows (added item 2): one helper creates HeapScope's folders and files with user-only
   permissions (sessions, logs, index caches, sidecars, partial dumps). Tests: run on Linux and macOS in CI (skipped on
   Windows).
9. [x] Builds per system (added item 1): `dotnet publish` self-contained per runtime identifier with the worker and the
   CLI; builds are AnyCPU and a published build takes its runtime identifier's architecture (decision 10); a CI job
   builds the four, runs the three it can and uploads them as artifacts. Tests: the CI job; the Windows build passes the smoke gate.
10. [x] CI: Linux and macOS build the UI and run the UI tests; Linux runs the headless smoke on the LeakyConsole core
    dump; Windows captures LeakyConsole with the CLI and checks the known answers (added item 6).
11. [x] Review cleanups (added item 7): L2, L11, L13, L14, L16 of the 2.3.5 review, each with its test where one applies.
12. [x] Retire `HeapScope.UI.Wpf`, its tests and `spikes/AvaloniaGrid`; docs: ARCHITECTURE (3.1, code map, section 8
    threading, ADR-019 and ADR-025 amended), DEVELOPER (run, smoke, publish), CLAUDE.md, THIRD-PARTY-NOTICES, SPRINTS.

**Decisions** (decided autonomously from the recommended defaults unless marked otherwise)
1. Two projects: `HeapScope.UI.ViewModels` (platform neutral, CommunityToolkit.Mvvm) and `HeapScope.UI` (Avalonia,
   executable `HeapScope`); the tests split the same way. Namespaces drop `Wpf`.
2. The WPF app is deleted in this sprint once the Avalonia app reaches parity; the two are not shipped side by side.
3. Dialogs become asynchronous (`IUserInteraction`): Avalonia has no blocking dialogs; a small confirm window replaces
   `MessageBox`.
4. The theme follows the system (Avalonia's default variant) unless `Ui:Theme` is Light or Dark, as in WPF.
5. Avalonia's build telemetry is turned off for the repository: HeapScope sends nothing anywhere (ARCHITECTURE 12), and
   this extends it to its build. It ran during the 2.3.5 prototype builds before this was noticed (reported to the
   product owner).
6. Avalonia.Headless (MIT) is added for tests and for `--headless` smoke runs in CI; covered by the product owner's go
   on this scope ("UI tests move to Avalonia's headless test platform"). Its download is the only new one.
7. Builds are self-contained and not single-file (the worker is a separate executable), ReadyToRun on; CI uploads them
   on pushes to `develop` and `main` and on manual runs, kept 14 days; never anything but build output.
8. The smoke gate keeps its limits (the dispatcher budget, twice for the smoke). The first-window time is logged by every
   smoke run and fails it only above 30 s (a start that hangs): measured 2.2 s warm for a Release build here, the same as
   WPF, and 8 to 15 s for the first launch after a build while its files are scanned, so a tight limit would be noise
   (first planned at 2 s; revised with the measurement, decided autonomously).
9. Owner-only permissions apply to files HeapScope creates; a dump written by the runtime (Linux capture, later) and a
   `--out` folder the user chose keep their own permissions.
10. Builds are AnyCPU instead of x64 (found by CI: the x64 test assemblies could not load on the arm64 macOS runner, and
    an osx-arm64 publish mixed arm64 and x64 assemblies): each process runs as its machine's 64-bit architecture, and a
    published build takes its runtime identifier's (decided autonomously; ADR-001 and ADR-025 amended).
11. Headless smoke runs (CI) check that every step works and log the UI lags without gating them: on a two-core runner
    drawing in software the lag measures the machine, not HeapScope (decided autonomously; the Windows smoke gate is
    unchanged).
12. Avalonia's dispatcher has no operation hooks, so the longest UI operation is bounded, not timed: smoke runs probe
    every 10 ms and check an upper bound (the highest-priority probe's wait plus the longest gap between probe ticks)
    against the 100 ms budget (sprint 2.3.6 review, decided autonomously).
13. `Avalonia.Fonts.Inter` (a dependency of the headless package, its font under the SIL Open Font License) is kept out
    of the app's build output: the app uses the system font and the headless runs need no font of their own (decided
    autonomously).


**Review summary**: 1 High, 15 Moderate, 30 Low (docs/reviews/sprint-2.3.6.md). The High (the Linux and macOS builds
lost their executable modes in the artifact upload, and the package job never ran what it built) was fixed with
tarballs that CI unpacks and runs (CLI version, a worker ping, the worker's Server GC, a headless smoke), and confirmed
by solution-architect after a second pass (relative tar names for Git Bash on Windows). Moderates, decided
autonomously: 14 fixed (space reserved before mapped writes; a sound upper bound for the longest UI operation, decision
12; smoke runs fail on any whole-list grid walk and log UI memory; one module key for summary, diff and inspections;
the self-contained worker's own DAC folder instead of a folder beside it; the architecture check before the DAC
search; the worker's GC settings logged and section 9 corrected (DATAS is off with a heap count); a guarded window
close; attribution and message window tests with a hit-test fix; the grid dispatcher required; ReadyToRun for every
published executable; the WPF parity table; TreeView limits documented and the 2.4 plan corrected; the Inter font kept
out of the app, decision 13), 1 deferred to 2.4 (the grid adapter keyed by an interface). Lows listed. Measured
(Release, this machine): Small dump checked lag 102 to 105 ms and longest operation at most 64 ms, Medium 71 ms and at
most 69 ms (WPF, timed exactly: 44 to 68 ms); no whole-list grid walk; UI 219 to 229 MB private; published first window
1.0 to 1.3 s; builds (CI) 266 to 370 MB unpacked, 112 to 139 MB as tarballs, each run from its unpacked tarball
(worker ping, headless smoke) except `osx-x64`. Gate: 993 tests, 0 failed, 0 warnings (an earlier run hit the known
SessionLibrary concurrency flake under load; it passed 5 of 5 alone). CI green on all jobs.

---

## Sprint 2.3.7: Port peer review

**Goal**: a second, wider review of the Avalonia port (2.3.6 changed 173 files) from three perspectives, solution
architect, developer and support or system analyst, looking for refactoring items and for logging that can back up
every result HeapScope shows.

**Scope** (product owner, 2026-10-26: "Really look at this for refactor items and also assure logging telemetry is
solid. If someone contests a finding, we'll need logging to back up why result was proposed."):
- Review the port's change set (`sprint-2.3.5..sprint-2.3.6`) and the code it touches for refactoring: duplication,
  misplaced responsibilities, seams 2.4 will need, dead code from the WPF era.
- Result provenance in the logs: when a user contests a result (an inspection finding, a retained size, a retention
  path, a diff row, a growth alert, a refused capability, a DAC choice, a cache reuse), the local log shows why it was
  produced: the snapshot's identity and loader facts, versions, the rule and its thresholds, counts, timings, with ids
  that tie the UI, the CLI and the worker lines together. Never dump contents (ARCHITECTURE 12): no field values or
  strings. "Telemetry" here means local diagnostics only; HeapScope sends nothing anywhere.
- A support perspective: can someone diagnose a user's problem from the log folder alone (where logs are, how long they
  are kept, what a failure looks like, what is missing).
- Fix every High; Moderates follow the lead engineer's recommendation (product owner: "Go with your reco for moderate
  recommendations").

**Acceptance criteria**: docs/reviews/sprint-2.3.7.md with the three perspectives; every High fixed and confirmed;
every Moderate decided; a test or a logged run shows each fixed provenance gap; docs describe the logging that exists.

**Version**: 2.3.7.

**Dependencies**: 2.3.6.

**Task breakdown** (from docs/reviews/sprint-2.3.7.md; the review ran first, as this sprint's purpose)
1. [x] Log files per process (H1, M17): `heapscope-yyyyMMdd-<app>-<pid>.log`, each starting with a header (product and
   informational version, app, pid, system, architecture, runtime, self-contained or not, log folder); lines in ISO 8601
   with offset; scopes rendered; files kept 14 days; a file rolls at 50 MB; `Logging:File:MinimumLevel` (also passed
   to the worker). Tests: two providers on one folder keep every line; old files removed; the roll; scopes; the level.
2. [x] Snapshot identity (H2): one structured line when a snapshot opens (id, path, size, fingerprint, kind,
   capabilities, facts, runtime, capture time and its source, loader and cache writer versions, cache path and identity,
   from cache or rebuilt or reopened, memory mode, notes, load timings, loader diagnostics); incomplete segments and
   static roots kept in the cache metadata (optional fields); the client logs its handle with the worker id on open,
   reopen and a changed-file refusal. Tests: the line's fields on a fixture open; the metadata round trip.
3. [x] Result provenance (H3, M5): one structured line per answered query at the worker's RPC boundary (operation,
   snapshot ids, parameters without filter text, versions and thresholds, outcome counts and flags, elapsed);
   inspections get a version and parameters; duplicate strings count unreadable strings and say unreachable strings are
   included; the Compare view model logs how the pair was chosen. Tests: a captured logger sees each result line; the
   duplicate strings note.
4. [x] Who ran what (H4, M20): `ProductInfo.InformationalVersion` (with the commit); a start line for the UI, the CLI
   (with its command and options, never values) and the worker (protocol, system, architecture, runtime, path, rule and
   format versions); `JobTracker` logs each job's start and end with id, title, elapsed, status and error code; the CLI
   logs its exit code; worker start failures and the errors shown to the user are logged. Tests: job lines; CLI exit
   line.
5. [x] Correlation (M1, M20): an `Activity` per job, CLI command and grid query; StreamJsonRpc's activity tracing on both
   ends so worker lines carry the client's trace id; scopes in the worker's console log; forwarded worker lines prefixed
   with the worker pid and generation; result set re-runs logged. Tests: a worker line carries the client's trace id.
6. [x] Errors and refusals (M2): expected refusals logged at Information without a stack, with operation, snapshot and
   reason; errors created as codes logged; inspection availability reasons logged; the CLI console shows worker errors
   only; ARCHITECTURE 4.3 and 11 corrected. Tests: refusal logged without stack.
7. [x] DAC choice (M3): `DacChoice` (path, rule, folders searched, downloaded) returned through the loader's diagnostics
   and the reader, logged with the identity line, kept in the cache metadata; each rejected candidate's reason in the
   failure message; a download logged as a warning. Tests: the rule and the rejection reasons.
8. [x] Monitoring and the growth gate (M4): run start and end lines with the rules; a fire line with metric, source,
   baseline, threshold, step and rearm; the same in the timeline marker and the session record; the CLI gate decision
   logged. Tests: the rule decision; the gate line.
9. [x] Log content policy (M6): ARCHITECTURE 11 says what logs may hold (paths, names, counts, addresses; never values or
   strings) and that logs are local evidence, not for publishing; the instances filter text kept out of logged messages.
   Test: a refused filter's log line has no text.
10. [x] Instances filter drops (M10): unreadable strings and regex timeouts counted, returned as a note and logged.
    Tests: the counts.
11. [x] Smoke runner (M11): shared measuring and step helpers, the failed step named in the log, `ILogger<SmokeRunner>`,
    the CI-matched lines marked as a contract with event ids; the operation bound and the exit-code verdict extracted and
    unit tested.
12. [x] Shared patterns (M12, M15): a `WorkerException` base, `ResultSetHandle.ReleaseInBackground`, a section loader for
    `ObjectViewModel`; one `AddHeapScopeViewModels` used by the app and the tests.
13. [x] Support docs (M16): the log folder per system in README and DEVELOPER; the About page shows the log folder,
    versions and runtime, with a copy action; messages name the actual log folder.
14. [x] Early and fatal failures (M18, M19): `Main` logs failures before the host exists; Avalonia's warnings reach the
    log without message text; worker exit codes explained (out of memory kill, abort, unhandled exception); a worker
    last-chance line.
15. [x] Docs: ARCHITECTURE 11 (logging and provenance, the policy), 4.3, 12; DEVELOPER (logs, support); CLAUDE.md;
    SPRINTS 2.4 gets the deferred items (M7, M8, M9, M13, M14, typed fake rows).

**Decisions** (decided autonomously from the recommendations)
1. One log file per process rather than shared appends: no cross-process locking, and each file says who wrote it.
2. Provenance is logged now; a provenance record on the result DTOs (protocol 11) waits for the shared analysis export
   (ADR-026), which needs the same record.
3. Correlation uses W3C trace ids through StreamJsonRpc's activity tracing, so the protocol does not change.

**Review summary**: 4 High, 20 Moderate, 27 Low (docs/reviews/sprint-2.3.7.md; three perspectives: solution-architect,
senior-dev, support and system analyst). The Highs were all logging: processes overwrote each other's log lines, a
snapshot's identity was never logged, no result was ever logged, and there was no trail of versions, jobs or shown
errors. All four fixed and confirmed by the reviewers that raised them (two gaps from the confirmations fixed too:
retention path roots in the log, and host build failures). Moderates, decided autonomously: 14 fixed (trace ids from the
UI or CLI to the worker, refusals logged without stacks, the DAC choice with every candidate's outcome, monitoring rule
decisions and the CLI growth gate, rule versions and parameters, a log content policy, instance filter drops, the smoke
runner's steps and tested verdicts, a worker exception base and shared release and section helpers, one view model
wiring, support docs and the About page, early UI failures and Avalonia's warnings, worker exit codes, job and re-run
lines), 6 deferred to 2.4 (navigation targets, the dominator tree's worker pieces, exactness on the wire, column groups,
shared view controls, typed fake rows). Also found: the session lock's 10 second deadline caused the long-standing
SessionLibrary gate flake (now 30 s). Protocol 11. Measured (Release, Small dump smoke): longest UI operation at most 63
to 64 ms, lag 101 to 115 ms, UI 227 to 233 MB, about 80 log lines per smoke run. Gate: 1,025 tests, 0 failed, 0
warnings; CI green on all jobs.

---

## Sprint 2.4: Retention explorer UI

**Goal**: explore what retains memory.

**Scope**: drill-down dominator tree, biggest retainers, grouping by type and namespace, cross-links between
summary, diff, inspection and retention views.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): a dominator tree with tree and path modes plus an icicle chart (simpler than a sunburst, same data); click highlights a path, double-click zooms; a biggest retainers tile.

From 2.3.5 (ADR-025): built in Avalonia after the port (2.3.6). The tree is built on the free controls (a flattened,
data-virtualized row list with indentation in the DataGrid), not the paid TreeDataGrid. Avalonia's `TreeView` is not
virtualized and stays for small, capped trees only (2.3.6 review). A grid whose column set or row count changes on a
re-query gets an adapter keyed by an interface rather than by row type (2.3.6 review).

From the 2.3.7 peer review (deferred here, decided autonomously):
- M8 first: one navigation target (snapshot, type key with scope, object, retention node, diff row) and one
  `Navigate(target)` on the Snapshots view model that picks the tab, a subject interface with a typed trail (no more
  `List<object>` and reflection binding), tab enums instead of numbers, and the navigator passed to grids by
  constructor instead of settable commands; `SnapshotTab.axaml` split into a view per subject.
- M9: decide at the start, as a decision or ADR, how the dominator tree's children are found (a children section in the
  dominators sidecar, sorted by retained size, or a measured scan per expand), the request shape (expanded node ids;
  `NodeId`, `ParentId` and `Depth` columns) and the updated memory budget (ARCHITECTURE 6.4).
- M7: whether a value is exact or a lower bound travels on the wire (a column or result flag), with caveat codes whose
  texts live in Contracts; the CLI and the view models render from that one source.
- M13: column groups as one `VisibleColumnGroups` behavior and a generic cell row, with the grid adapter work above.
- M14: a note text control and a type grid toolbar control (grouping, filter, totals), used by the types grid,
  Compare and the dominator grid.
- M15 rest: typed fake rows keyed by the Contracts column names in the view model test fakes.

From 2.3 (decisions 6 and 7): the exact retention of a set ("open objects retained by this set": a reachability pass from
the roots that skips the set, since a group's retained column is only a lower bound), the biggest retainers tile and the
dominator tree drill-down build on the dominators sidecar (`Dominators`, `GroupRetention`).

**Acceptance criteria**: every LeakyApi leak is reachable from "biggest retainers" in three clicks or fewer.

**Dependencies**: 2.2, 2.3, 2.3.6.

**Task breakdown**
1. [x] Navigation first (2.3.7 M8): a `NavigationTarget` (snapshot, type key with scope, object, dominator node, diff
   row) and one `Navigate(target)` on the Snapshots view model that picks the tab; `INavigationSubject` with a typed trail
   (no `List<object>`, no reflection binding); `SnapshotTabs` and `ReferenceTabs` enums instead of numbers; the navigator
   passed to grid view models by constructor; `SnapshotTab.axaml` split into a view per subject. Tests: the existing
   navigator and view tests; a navigation target per kind lands on its tab and subject.
2. [x] Dominator children (2.3.7 M9, ADR-027): the dominator tree's children as a CSR (index order since the review, H5)
   (`DominatorTree.BuildChildren`), stored as two more sections of the dominators sidecar (semantics version 2, so older
   sidecars rebuild once) and in memory without one; kept bytes 12 to 20 per object; ARCHITECTURE 6.4 updated. Tests:
   children order and coverage on synthetic graphs; sidecar round trip; an old sidecar is refused.
3. [x] Dominator tree query (worker, protocol 12): a flattened, paged tree from the virtual root or a focus object,
   expanded nodes by key, children as objects or grouped by type or namespace (siblings' retained sizes are disjoint, so
   group sums are exact), at most 100 children per node plus a "more" row, rows with key, parent key, depth, kind, name,
   module, address, objects, retained, shallow bytes, share of the heap and child count. Biggest retainers are the root
   level. Tests: flattening, grouping sums, the "more" row, keys stable across re-runs.
4. [x] Exactness on the wire (2.3.7 M7): `ResultColumn.Bound` (exact or lower bound) set by the worker, with the
   texts in Contracts; the CLI and the view models render from it. Tests: the type summary's retained column is a lower
   bound, the instances' and the tree's exact.
5. [x] Exact retention of a set: "objects retained by this set" for a type (or a scope) by a reachability pass from the
   roots that skips the set; totals (exact) next to the group's lower bound, and the retained objects by type as a grid.
   Tests: synthetic graphs with shared and exclusive children; the known answers' leaks.
6. [x] Icicle chart query and control: the top levels below a focus with segments of at least 0.1 percent; click
   selects and highlights the path in the tree, double-click focuses. Tests: layout widths and the threshold; the control
   builds headless.
7. [x] Retainers view (UI): a Retainers tab with the biggest retainers, the dominator tree grid (indentation, expand and
   collapse with a click, Enter, Right and Left keys), grouping and the icicle; cross-links: type rows, diff rows and
   inspection rows open "show in dominator tree" and "objects retained by this set"; a tree node opens its object,
   its type's instances and its retention paths. Grid pieces from 2.3.7 M13 and M14: `VisibleColumnGroups`, a generic
   cell row, a note text control and a type grid toolbar. Tests: headless views bind cleanly; view model tests for
   expand, group, focus and the links.
8. [x] CLI: `retainers <dump>` (biggest retainers, `--group`, `--depth`, `--top`, `--format table|json|csv`) and
   `retained-by <dump> <type>` (exact set retention). Tests: CLI integration on the fixture dump.
9. [x] Acceptance and smoke: an integration test proves every LeakyConsole leak in the known answers is reachable from
   the biggest retainers in three clicks or fewer (open the row, expand, see the growing collection or its items); the
   smoke drill opens the Retainers tab, expands and focuses, checked against the budget. Typed fake rows in the view
   model tests (2.3.7 M15 rest).
10. [x] Docs: ARCHITECTURE (6.4, 4.4 protocol 12, the retention explorer, ADR-027), DEVELOPER (CLI commands), README,
    CLAUDE.md, SPRINTS.

**Decisions** (decided autonomously from the recommended defaults)
1. The children of the dominator tree are stored, not scanned per expand: a scan reads every object's dominator
   (57.5 million on Medium) per click; stored children cost 8 bytes per object once and make an expand a slice read
   (the review's H5: kept in index order; a query orders only the rows it shows).
   They live in the dominators sidecar (new sections, semantics version 2), so existing sidecars are rebuilt once.
2. Tree rows are keyed by address (objects) and by parent key plus group name (groups), so a re-run after a worker
   restart keeps the expansion; at most 100 children per node with a "more" row that expands by 100, and at most
   50,000 rows per answer.
3. Biggest retainers are the top-level dominators (the virtual root's children), by object or grouped by type; static
   field values are top-level dominators, so each leak's collection appears there directly.
4. Group rows sum their members' retained sizes exactly: siblings in the dominator tree retain disjoint sets.
5. Exact set retention is computed per request (a reachability pass, one bit per object plus a stack), never stored.
6. The icicle chart replaces the sunburst (research doc 7); it shows six levels below the focus by default.
7. Exactness travels as a column attribute (`ResultColumn.Bound`), not per row.
8. Protocol 12.
9. The Retainers view gets its own tab on the snapshot, next to Types, Compare and Inspections.
10. During the build (decided autonomously): set retention takes a type (name and module); a namespace or assembly
    scope is a Backlog item. "Show in dominator tree" from a type row opens the tree grouped by type with the type's
    top-level group open; from an object it focuses on the object, its dominators as a trail. The links are in a
    right-click menu on the types, Compare and inspection grids and in buttons on the instances and object views.
11. The tree is re-queried on every expansion (the worker keeps no view state; the keys are the state), and the client
    reads its rows by column name.
12. 2.3.7 M13 and M14: column groups are named by the view model (`VisibleColumnGroups`), one row type serves every
    result set grid (`CellRowItem`), and status and note lines are one `NoteText` control (21 copies replaced). The type
    grid toolbar was not extracted: the Retainers tab has no type filter, so no third copy appeared (Backlog).
13. 2.3.7 M15 rest: the new fakes are typed (`FakeTreeNode`, the set's rows); the older positional fake rows stay
    (Backlog).
14. The README has no command list (it points to `--help`), so it needed no change.

**Review summary** (`docs/reviews/sprint-2.4.md`): four reviewers (solution-architect, senior-dev, gc-specialist, and
a support and system analyst). 5 High, 17 Moderate, 33 Low after dedupe (the re-review of the fixes added 2 Moderates).
- High, all fixed and confirmed: set retention reserves its memory and uses a chunked stack (H1); the focus trail keeps
  the top 3 and nearest 20 dominators (H2); the toggled or linked row stays selected and in view in the new rows (H3,
  `RevealKey` and `RevealIndex`, `OnRowsApplied`, `ScrollToSelection`); the acceptance test also runs on a live LeakyApi
  dump (H4); the tree's children are kept in index order and only shown rows are ordered (H5: Medium's dominators 13.4
  to 6.4 s, peak 3,747 to 3,291 MB).
- Moderate (decided autonomously): 15 fixed now (the re-review's two included: grouped trees without hashing or
  delegates per object, set retention's peak measured): a message when a type has no top-level group; Contracts owns the
  tree's kinds, keys and limits; texts from the column's bound and one name for the set action; grouped trees cost what
  they show; published limits and CLI notes in every format; re-measured docs; provenance of what the user looked at
  and of each action; reachable versus all members; an unreachable focus's next step; a depth limit; set retention
  checked against ClrMD; a bar with no loaded row; scoped inspection rows; test and fake fidelity. Deferred: a
  `ValueBound.Approximate` and caveat codes, one type entry among several of a name (Backlog).
- Measured (Release, 32 GB machine): Medium dominators with the children 6.4 to 6.9 s in memory (peak 3,291 MB), 11.4 s
  mapped (697 MB), 1,097 MB kept; DenseHeap 7.3 s in memory (3,877 MB), 10.6 s mapped (658 MB); set retention of 3.2M
  objects 3.3 s through the CLI, peak 3,300 MB for 16M objects in the benchmark (DenseHeap 3,876 MB); a tree query 0.03 s; the UI smoke run's Retainers drill warm: lag 50 ms, longest operation 32 ms.
- Both acceptance runs: every leak of LeakyConsole and of LeakyApi is at most three expansions from the biggest
  retainers (three need none, the unbounded dictionary one, the event handlers three).

---

## Sprint 2.5: Allocation tracking

**Goal**: show where allocations come from.

**Scope**: EventPipe allocation sampling with call stacks, symbol resolution, top call stacks and types over a
time range.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): select a timeline interval, then types with back traces below and a call tree with total and own columns.

From 1.5 (product owner): built on the trace recorder added in 1.5 (no dotnet-trace install), with trace reading
(TraceEvent) moving from the developer tool into the product. Recorded between two snapshots, the allocations give the
diff the "new since the baseline" view that dumps alone cannot (ADR-018).

**Acceptance criteria**: LeakyApi's leaking allocation site appears in the top call stacks under LoadGen.

**Dependencies**: 1.7, 2.3.6 (the UI is built in Avalonia, ADR-025). Platform scope: EventPipe allocation sampling works on
Linux and macOS too, but the trace recorder attaches through capture's process discovery, Windows only until the Linux
capture item; the sprint says which systems it covers.

**Decisions** (all decided autonomously at /start-sprint, 2026-10-26; Phase 2 autonomy, one sprint at a time):
1. Source: the runtime's `GCAllocationTick` events (one about every 100 KB allocated, with the type name, the bytes
   since the last one and a call stack), recorded by a new `Allocations` trace profile. It works on .NET 6 and later
   with no agent. Sizes are estimates: each sample stands for the bytes allocated since the previous one, so columns
   say "sampled" and the CLI and UI say so once. .NET 10's randomized allocation sampling goes to the Backlog.
2. The worker reads the trace (ADR-007: heavy data stays out of the front end), through a new loader project
   `HeapScope.Loaders.Trace` (Core plus TraceEvent, like the other loaders). The model (`AllocationTrace`: frames, a
   deduplicated stack tree, types, and per-sample time, stack, type and bytes) lives in Core; the aggregations in
   Analysis. Protocol 13. A new ADR-028 records it.
3. TraceEvent needs an `.etlx` file to resolve stacks: the worker writes it to a private temporary folder and deletes it
   after reading; nothing is written next to the trace.
4. Managed frames are named from the trace's own rundown (no symbol downloads, ADR-017); runs of unnamed native frames
   collapse into one `[native code]` frame.
5. Views (an Allocations page, one tab per opened trace): a timeline of sampled bytes per second where dragging selects
   the interval; a Types grid; Back traces of the selected type (the allocating method, then its callers); a Call tree
   (top-down) with Total and Own; Top call stacks. Trees are served flattened from the open keys like ADR-027's tree
   (children by Total, 100 per page and a "more" row, a row limit).
6. Recording from the UI: the Processes page gets "Record allocations" (30 s by default, stoppable), saved into the
   session folder and opened when done; any `.nettrace` opens from the Allocations page's Open trace (its own picker,
   so the dump picker stays for dumps). The CLI's `trace` takes
   `--profile allocations`; a new `allocations <file>` command prints the top types and top call stacks of an interval.
7. Linking a trace to the Compare tab ("allocated between the two snapshots") needs the trace and the snapshots on one
   clock in the session record: Backlog, next to the 1.7 timeline item. Recording from the Live page goes with it.
8. Platforms: reading and every view work on Windows, Linux and macOS; recording from the UI uses process discovery
   (Windows only until the Linux capture item); the CLI records wherever the diagnostics port answers.
9. Logs (ARCHITECTURE 11): each query logs its interval, filters and the rows it answered with (type and method names,
   counts and bytes), so a disputed stack is traced from the UI action to the worker's answer; never event payloads
   beyond type names and sizes.

**Task breakdown**:
1. [x] Recording: `TraceProfile.Allocations` and `TraceKind.Allocations` (GC events at verbose with loader and JIT
   events, rundown on stop); CLI `trace --profile allocations`. Tests: providers unit test; an allocations trace of
   LeakyApi holds allocation ticks with stacks (integration).
2. [x] Model and reader: Core `AllocationTrace`; `HeapScope.Loaders.Trace` `AllocationTraceReader` (private `.etlx`
   deleted after, native runs collapsed, progress and cancellation, bad files refused with a plain message); layering
   rules updated. Tests: reader on a recorded trace, the `.etlx` is gone, a non-trace file is refused, architecture.
3. [x] Analysis: `AllocationProfile` (timeline buckets, types over an interval, top stacks, call tree and back-trace
   rows with paging and limits). Tests: synthetic traces with known answers.
4. [x] Contracts and worker: protocol 13; open, close, timeline, types, stacks and tree queries; memory reservation;
   provenance lines. Tests: worker tests, contract mirror.
5. [x] Application and CLI: the trace handle in the session; recording into the session folder; `allocations`
   command. Tests: application tests, CLI integration test.
6. [x] UI: the Allocations page (timeline with interval selection, Types, Back traces, Call tree, Top call stacks),
   Record allocations on the Processes page, `.nettrace` from the page's Open trace, a smoke step. Tests: view model and view
   tests, the smoke run.
7. [x] Acceptance and measurements: LeakyApi under LoadGen, its leaking allocation site in the top call stacks
   (integration); reading time and peak for a long trace. Docs: ARCHITECTURE (code map, 4.4, ADR-028, 10, 11, 12),
   DEVELOPER, CLAUDE.md.

**Review summary** (docs/reviews/sprint-2.5.md): four reviewers (solution-architect, senior-dev, gc-specialist, and a
support and system analyst). 4 High, 25 Moderate, 28 Low after dedupe; the re-review of the fixes added 3 Moderates and
5 Lows, all fixed.
- High, fixed and confirmed: managed frames without a name were shown as "[native code]" and merged, with nothing
  saying why (now `[unresolved frame in <module>]`, counted, with the rundown and TraceEvent's conversion problems in
  the descriptor, the log and the notes); the read's memory reservation rested on a misreported measurement (now
  measured with the worker's GC settings on three traces: 96 MB plus 3 bytes per file byte); a trace was read again
  after a worker restart without checking the file (now refused with `TraceChanged` when its size or write time
  changed); the CLI labeled all-types totals as one type's.
- Moderate (decided autonomously): 19 fixed now, among them one re-read per trace for concurrent queries, every stack
  answered in the log with its stack node, the allocation kind per sample with LOH and POH columns, exact arrays and
  pooled tree buffers, sorted samples and checked invariants, `.etlx` and partial-trace sweeps, a timeline bucket
  limit, cancellation through the fast queries and the CLI's read, honest recording cancel and failure messages, one
  meaning of %; 5 fixed in part with the rest in the Backlog or recorded (the conversion's cancellation, progress on
  the fast queries, type id and module, the CLI's type text in the request line, more race tests); 1 deferred (one flattened-tree builder for the dominator and
  allocation trees).
- Measured (Release, the `allocations` benchmark with the worker's GC settings): a 72 MB, 600 s trace (565,625
  samples) reads in 1.9 s with 227 MB of growth; whole-trace queries 2 to 13 ms. Acceptance: on LeakyApi under LoadGen
  the leaking allocation sites are in the top call stacks, through the reader, the worker and the CLI (a 12 s trace
  listed the five leaks' `Grow` methods as its top five stacks).

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

**Dependencies**: 2.2, 1.4, 2.3.6 (the detector cards are built in Avalonia, ADR-025).

**Decisions** (all decided autonomously at /start-sprint, 2026-10-26; the user said "go ahead with sprint 2.6"):
1. Detectors are inspections (ADR-020) of a new kind, `Leak`: one card each on the Inspections tab, in their own group
   ("Leak detectors"), with cause, fix and rows that open the objects; `inspect` on the CLI runs them. No new page.
2. A baseline: the inspection request takes an optional baseline snapshot (another open snapshot, the earlier one), so
   a detector can measure growth; the Inspections tab gets a baseline picker and the CLI `inspect --baseline <dump>`.
   Matching across dumps uses stable keys (a static field's declaring type and name, a type name), never addresses
   (ADR-018). Protocol 14.
3. The detectors, each with its threshold logged as its parameters (2.3.7 review M5):
   - `growing-static-collections`: collections a static field holds directly (List, Dictionary, HashSet, Queue,
     Stack, ArrayList, Hashtable, ConcurrentDictionary, reference arrays; ConcurrentQueue and the others went to the
     Backlog at review, M5), counted by their own fields. With a baseline: grown by at
     least 1,000 elements and 10 %. Without: at least 10,000 elements, said to be large, growth unknown.
   - `event-handler-leaks`: an event field (a delegate field of a publisher type) holding at least 1,000 subscribers
     across its publishers, named `Publisher.Event`, with the subscribers' types.
   - `timer-leaks`: at least 500 active timers whose callbacks target one type.
   - `disposed-but-referenced`: at least 100 reachable objects of one type whose disposed flag is set (the field names
     the .NET guidelines and templates use).
   - `httpclient-instances`: at least 20 reachable `HttpClient` or handler instances (create one and reuse it).
   - `undisposed-service-scopes`: at least 500 reachable dependency injection scopes not disposed (ASP.NET Core).
   They read field values, so they need a full dump; on a gcdump they are unavailable with the reason (ADR-020).
4. Fixtures first: two new leaks outside the classic five (so the five's exact known answers stay): `TimerLeak`
   (timers with an instance callback never disposed, rooted by the runtime's timer queue) and
   `DisposedReferencedLeak` (objects disposed and still held by a static list); LeakyApi's "all" includes them. A clean
   workload (`Clean:Enabled`: a bounded cache with eviction, subscribe then unsubscribe, timers disposed, objects
   disposed and released) for the "no detector fires" run. Known answers gain `detections` (detector, key, minimum
   count); a `clean` known answer lists none.
5. Acceptance runs on two LeakyApi dumps each: a baseline after a warm-up and a current one after more load, all leaks
   enabled, then the clean workload alone.
6. Logs (ARCHITECTURE 11): each detector logs its rule version, thresholds, what it scanned and each row's key and
   counts, so a disputed finding is traced; the baseline used is part of the log line. Never field values.
7. Not in this sprint: WPF binding, collection binding, dependency property and x:Name leaks (a WPF fixture app is
   needed; Backlog); detectors on gcdumps (no field values).
8. Found while building (decided autonomously): a `System.Threading.Timer` nothing references is finalized and stops,
   so the timer leak uses started `System.Timers.Timer`s, the pattern that leaks; the new leaks get their own switch
   (`Leaks:Detected`, LeakyConsole `--detected`) so the five's exact census, paths and retention checks are untouched;
   the baseline picker preselects the latest earlier open snapshot of the same process; without a baseline the
   static collections detector leaves out `System.*`, `Microsoft.*` and `Internal.*` statics (the runtime's own tables,
   such as `CastHelpers.s_table`, fired on the clean run), and arrays count only when they hold references; the event
   handler detector reads a publisher type's field name once per delegate type (replaced at review, H2); the tab shows
   one list with group headings instead of two lists (one selection).
9. Changed at review (decided autonomously, `docs/reviews/sprint-2.6.md`): byte totals are bytes (subscribers'
   delegates and targets, timers' objects); the event detector counts field-like events only, names each delegate by
   its publisher's own field, and leaves out unreachable publishers, self-held handlers, compiler-generated holders and
   framework callback fields (a multicast in any field of the publisher's own is an event too); the timer detector counts periodic timers in the queue only and attributes static or
   lambda callbacks to the timer's state; disposed flags may be integers or auto-property backing fields; HttpClient
   counts distinct handlers owned by clients; the root scope is left out; reachability is computed once per snapshot
   and leaves out the finalizer queue; detector rows open the type's reachable objects (`InstanceScope.Reachable`), so
   the whole-type actions stay off; an unusable baseline is refused; a baseline chosen mid-run reruns the growth tile.

**Task breakdown**:
1. [x] Fixtures: `TimerLeak`, `DisposedReferencedLeak`, the clean workload in LeakyApi; known answers with
   `detections` and `Clean.json`. Tests: known-answer checks for the new fixtures with ClrMD.
2. [x] Contracts and worker: inspection kind and baseline flag on `InspectionInfo`, `BaselineSnapshotId` on the
   request, the baseline in `InspectionContext`; protocol 14. Tests: worker and contract mirror.
3. [x] Detectors in Analysis, each tested on synthetic graphs with a fake reader: the six of decision 3.
4. [x] Front ends: the Inspections tab's detector group and baseline picker; CLI `inspect --baseline`. Tests: view
   model and view tests, CLI integration.
5. [x] Acceptance: every LeakyApi leak detected with its known answer; nothing on the clean run (integration).
   Measurements on Medium (deferred: the sample dumps need a runtime no longer installed; measured on a 658 MB
   LeakyApi dump instead). Docs: ARCHITECTURE (ADR-020 amendment, 4.4, 10, 11), DEVELOPER, CLAUDE.md, the known
   answers README.

**Review summary**: 8 High, 24 Moderate, 33 Low after dedupe (four reviewers: solution-architect, senior-dev,
gc-specialist, support; `docs/reviews/sprint-2.6.md`). Highs fixed: counts reported as bytes; the event field name
cached per type pair; any delegate field counted as an event; one-shot, paused and lambda-callback timers counted or
misattributed; rows opening the whole type; a baseline change mid-run leaving tiles waiting; the disposed probe's
O(types x objects) scan; a gcdump baseline silently ignored. Moderates fixed now: reachability once per snapshot,
finalizer queue left out, delegate targets read as the runtime stores them, integer and backing-field disposed flags,
HttpClient factory clients, the root scope, a clean workload with look-alike patterns, an upper bound in acceptance,
rerun errors, every row and exclusion logged by name, elapsed time; from the re-review, native-int fields, clients
sharing one handler, events in private fields, publisher reads limited to types that can fire. All Highs confirmed by
the reviewers that raised them. Deferred: other collection types, leaky HttpClient and scope fixtures, in-flight scopes,
the picker refresh (2.7), derived-input descriptions, the shared layout table, threshold edge tests, an exact object
set for detector rows, the Medium measurement. Tests: 1,126 (3 skipped).

---

## Sprint 2.7: Navigation and polish

**Goal**: a coherent, keyboard friendly tool.

**Scope**: full navigation history, object bookmarks, consistent cross-view linking, keyboard support, CSV
export of any grid.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): an analysis path sidebar (branch from any earlier step), analysis tabs, object marks across snapshots, the full filter syntax, keyboard shortcuts, CSV export.

From 2.3.5 (ADR-026): CSV export is the first form of the shared analysis export, analysis only by default (no string
text or field values unless the person exporting opts in for that export).

From the 2.6 review: the Inspections tab's baseline picker refreshes as snapshots open and close and matches the
process by id and start time (M12); its group headings are not selectable (L13).

**Acceptance criteria**: every grid exports to CSV; every view reachable by keyboard; history survives view
switches.

**Dependencies**: 2.4.

**Decisions** (all decided autonomously, 2026-10-27, Phase 2 autonomy):
1. History becomes an analysis path per open snapshot, kept while the snapshot is open: switching snapshots, tabs or
   pages never resets it. It is a tree: opening something from an earlier step adds a branch beside the later steps
   instead of dropping them (dotMemory's Analysis Path). Back goes to the step's parent, Forward to the child visited
   last. Links into the Retainers tab are steps too, replayed when chosen. At most 30 steps per snapshot; past that the
   oldest leaf off the current path is closed. A step can be closed with its branch. The path is a sidebar on the
   Snapshots page.
2. Analysis tabs (several analyses side by side) are deferred to the Backlog: the branching path covers coming back to
   an earlier analysis, and each tab would hold its own worker result sets.
3. Bookmarks mark objects (snapshot, address, type, one of five colors). They are kept in `bookmarks.json` in the app's
   data folder (a root the tests replace), keyed by the snapshot file's full path, size and write time, so they come
   back when the snapshot is opened again; they hold no field values or string text. A Bookmarks list beside the path
   shows the marks of every open snapshot and opens them; marks of snapshots not open are kept. Types are not bookmarked.
4. Filter syntax (types and diff grids): space-separated terms must all match; `-term` leaves out names that match;
   `module:text` (or `m:text`) matches the row's module, and rows without a module (namespace groups) do not match it.
   Each term keeps the 1.3 rules (smart case, `*`, CamelHumps); highlighting shows the positive terms. The instances
   filter (string value, address) is unchanged.
5. Keyboard: one shortcut table (`Shortcuts`) drives the window and a shortcuts list (F1, also on the About page).
   The command key is Ctrl on Windows and Linux and Cmd on macOS. Command+1 to 7 open the pages in sidebar order;
   Alt+1 to 4 the Snapshots tabs; Alt+Left and Alt+Right (and the mouse buttons) move along the path; Command+F
   focuses the current view's filter; Command+E exports the focused grid (else the view's main grid); Command+D
   bookmarks the shown object or the selected row's object; Command+O opens a dump; Command+L focuses the path; F5
   refreshes the page; F1 shows the shortcuts list on the About page. Enter, double-click, Right and Left keep their
   1.5 and 2.4 meaning.
6. CSV export (the first form of ADR-026's export): every grid has "Export to CSV..." in its context menu and
   Command+E. It writes all of the grid's rows in its current sort and filter (a tree as currently expanded), not only
   those loaded, with the result's columns other than hidden link columns. Columns that carry values (the instances'
   Value, a duplicate string's text) are left out unless the person answers yes for this export, which then says so on
   its first line. Text cells that a spreadsheet would read as a formula get a leading apostrophe. UTF-8 with a BOM
   (spreadsheets read it). The file is written off the dispatcher; the log names the grid, rows, columns, the opt-in and
   the time, never the path or values.
7. `ResultColumn` gains `IsHidden` and `HasValues` (protocol 15), replacing the naming convention (Backlog: 1.5 L2,
   1.6 M2); the CLI's `--format csv|json` stays a local view and keeps its columns (ADR-026).
8. The Inspections tab's baseline picker (2.6 review M12, L13) is taken as planned. Dumps record no process start time,
   so an earlier capture of the same process matches by process id when both dumps record one, else by name; a
   baseline the person chose stays chosen while it is open, and selecting a snapshot chooses its default again.

**Task breakdown**:
1. [x] Result columns say what they are: `ResultColumn.IsHidden` and `HasValues`, protocol 15; every source sets them;
   the grids, the inspections tab and the CLI table use the flags. Tests: ContractMirrorTests, worker source column
   tests, InspectionsViewModelTests, CLI output tests.
2. [x] CSV writing in Application (`CsvExport`): streams a result set's pages (or in-memory rows), quoting, the formula
   guard, the values opt-in and its first line; the CLI shares the cell quoting. Tests: CsvExportTests (quoting,
   formulas, hidden and value columns, all pages fetched, cancellation).
3. [x] Export on every grid: `IGridExport` on each grid's view model, `GridExportService` (save picker, the values
   question, writing off the dispatcher, logging), `GridBehaviors.Export` adding the context menu item. Tests: view
   model export tests per source kind; a view test that every DataGrid in every view has an export.
4. [x] Filter syntax: `NameFilter` terms, negation and `module:`; the worker's types and diff sources pass the module;
   highlighting. Tests: NameFilterTests, worker filter tests.
5. [x] Analysis path: `AnalysisPath` (a tree of steps per snapshot) replacing the navigator's list; Retainers steps;
   the sidebar; closing a branch; the 30-step cap. Tests: AnalysisPathTests, NavigatorViewModelTests (history survives
   snapshot and tab switches), a view test of the sidebar.
6. [x] Bookmarks: `BookmarkStore` (file, keyed by snapshot identity), `BookmarksViewModel`, the list, Command+D and a
   context menu item on object rows. Tests: BookmarkStoreTests, BookmarksViewModelTests.
7. [x] Keyboard: the `Shortcuts` table, the window's handling (platform command key), focus of filters and the path,
   the F1 list. Tests: ShortcutsTests; a view test that reaches every page, tab and subject by keyboard only.
8. [x] Baseline picker: refreshes as snapshots open and close, matches the process by id (else name); headings
   not selectable. Tests: InspectionsViewModelTests, a view test.
9. [x] Docs: ARCHITECTURE (code map, protocol 15, ADR-029 for the analysis path and bookmarks, ADR-026 consequences),
   DEVELOPER, README (shortcuts, filter syntax, export), Backlog (analysis tabs).

**Review summary**: 3 High, 14 Moderate, 16 Low after dedupe (four reviewers: solution-architect, senior-dev,
gc-specialist, support; `docs/reviews/sprint-2.7.md`). Highs fixed: in-memory grid exports wrote command lines without
asking (columns now flag values in the view); overlapping filter terms drew wrong names (ranges merged); two windows
overwrote each other's bookmarks (each change re-reads the file under a lock). Moderates fixed now: export cancel, no
partial files, errors shown, the export tied to its result set in the log; bounds in CSV headers; the CLI's formula
guard; the default baseline ignores file times; bookmark toggle races and file problems; in-memory exports keep the
grid's sort; step and snapshot ids and filter shapes in the log; tests for the path's edges. Deferred: a column
projection on page requests (values never read for an export without them), releasing and measuring the result sets of
snapshots not shown, analysis tabs. Tests: 1,194 before the review fixes (3 skipped).

---

## Sprint 2.8: Packaging and distribution

**Goal**: people install and run HeapScope without building it.

**Scope**: decided at /start-sprint once the product owner picks the audiences and channels (2026-10-26: "good with
your proposal for a packaging sprint once you pick audiences and channels"). Options discussed after 2.4:
- Windows: a self-contained single-file publish (the app and the worker, which is its own process), a
  framework-dependent zip on a shared runtime, winget, MSIX, code signing.
- Linux: a self-contained tar.gz with a `.sh` launcher; AppImage, deb or rpm.
- macOS: an app bundle in a DMG, signed and notarized (needs an Apple developer account).
- The CLI as a `dotnet tool`.
- Release builds from CI on a tag, with checksums; releases to `main` stay the product owner's call (DEVELOPER.md 7).

Constraints: every bundled component keeps an allowed license (MIT or allowed), with its notices shipped; the
attribution strip and the license files ship unchanged (ADR-022); before any Avalonia update, check whether anything
used has moved to a paid model. Signing certificates, store accounts and paid services need the product owner's OK.

**Open questions for /start-sprint**: audiences (who installs it, on which systems); channels (GitHub Releases only,
or also winget, a dotnet tool, package feeds); self-contained or shared runtime; signing on which systems; how updates
reach users; whether CI becomes the release builder.

**Acceptance criteria**: to be set at /start-sprint with the channels; at least a clean machine of each chosen
system installs the package, runs the app, opens a fixture dump and runs the CLI, with no SDK installed.

**Dependencies**: 2.3.6 (the Avalonia app, ADR-025). Can run before 2.5 if the product owner wants packages earlier.

**Task breakdown**: to be filled by /start-sprint.

**Review summary**: to be filled by /wrap-sprint.

---

## Backlog

**High**
- Intermittent 105 to 130 ms dispatcher lag at job completion under heavy machine load (from 0.3): find the
  cause (likely CPU contention with the worker) and bring the smoke gate back to 100 ms. Capture a WPR or PerfView
  trace (CPU sampling, context switches, ready thread) of the smoke run and log the UI thread's CPU time per lag.
- Worker Server GC threads run at normal priority despite the below-normal process (from 0.3): evaluate a job
  object CPU rate, EcoQoS or GC heap affinity once a trace shows them preempting the UI.
- CI leak checks using the CLI (the remote, `develop` and the CI pipeline exist since 2.3 and 2.3.5; CI already runs the
  CLI on a fixture dump on Linux and macOS). Making CI a required check is the product owner's call (2.3.5 decision 8).
- Trend analysis across more than two snapshots.
- Finalizer queue and finalizable object analysis.
- Async state machine and Task leak detection.

**Medium**
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
- Retained bytes in snapshot diffs (from 2.3 decision 7): a type's retained lower bound in each snapshot and the delta,
  once both snapshots have dominators.
- gcdumps at scale (from 1.8, ADR-023 in 1.9): move gcdump capture into the worker (the capturing front end peaked at
  1,191 MB near the 10 million node cap), then raise the cap (a vendored change) after measuring a gcdump capture and
  load of the Medium dump against the budgets (section 10). Do the "gcdump survivors without a recorded root" item
  above with it.
- gcdump lost-events check (from 1.8): count the EventPipe session's lost events and fail or mark the capture.
- gcdump loader memory (from 1.8 review): presize from `NodeIndexLimit`, add references by index rather than address,
  release the MemoryGraph before the build.
- Generations for gcdumps (from 1.8): place objects by the GC's generation ranges when a writer records them.
- One capture per target at a time across the Processes page and monitoring runs (`CaptureService` serializes per
  process; rules treat a manual capture as running) (from the 1.7 review M4).
- Monitoring timelines recorded in `session.json` (run id, process, interval, rules, baseline, start and end), snapshot
  ids in the CSV markers, a format line in the CSV; reopening a past run's timeline on the Live page (1.7 M3,
  decision 4).
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
- Diff growth by generation (`--growth-of gen2`, or gen2, LOH and POH delta columns) and a per-row gate
  (`--fail-on-growth-per-row`), so uncollected gen0 garbage and a shrinking neighbor cannot mask a leak (from 1.4).
- Capture with an induced full GC first (for CI gates), where the target allows it (from 1.4).
- A diff on the 16 GB profile with neither index cache present: the second first load next to the first snapshot,
  peak and chosen modes recorded (from 1.4).
- View model tests on a single-threaded synchronization context, and a `TimeProvider` for the filter debounce
  (from 1.3).
- Delete snapshots and sessions (also their `.hsidx` and its `*.<kind>.hsidx` sidecars, beside the dump or under the
  fallback cache root, refused while open) from the UI and CLI, and a
  retention rule for old sessions (from 1.1).
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
  front end and worker memory with three Medium snapshots open, each with a full analysis path (M3).
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
  alone suffices (timers and events need field values); a type growth detector across two snapshots (the diff ranked by
  growth, with retention of the grown objects); measuring the detectors on a Medium-sized dump of the current runtime
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
