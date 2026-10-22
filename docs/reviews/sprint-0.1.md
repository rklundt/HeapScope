# Review: Sprint 0.1 Solution skeleton

Reviewers: solution-architect, senior-dev, gc-specialist (run in parallel on the full change set since
`sprint-0.0`). Phase 0 runs in autonomous mode: Moderate findings were decided by the lead engineer and are
marked "decided autonomously".

Totals: 1 High, 12 Moderate, 14 Low.

## High / Urgent

### H1 Dependent handles modelled as roots
- Reviewer: gc-specialist
- Location: docs/ARCHITECTURE.md 6.1 (root table, `RootKind.DependentHandle`)
- What: a dependent handle was a root kind with one object index. The CLR treats it as a conditional edge from
  primary to secondary, not a root.
- Why it matters: everything held through a `ConditionalWeakTable` would look permanently rooted, or real
  retention through a live primary would be missed. Retention paths, dominators and retained sizes would be
  wrong.
- Suggested fix: model dependent handles as primary -> secondary edges in the reference pass; exactly one
  mechanism; synthetic test and fixture known answer.
- Status: **Fixed.** `DependentHandle` removed from `RootKind`; ARCHITECTURE.md 6.1 "Root and reference semantics"
  states dependent handles are edges added in the reference pass, with exactly one mechanism. Sprint 0.2 adds a
  synthetic test (edge only retains while the primary is reachable) and sprint 0.4 a `ConditionalWeakTable`
  known answer. Confirmed by gc-specialist (see Confirmations).

## Moderate

### M1 Static field roots do not come from root enumeration
- Reviewer: gc-specialist. Location: ARCHITECTURE.md 6.1.
- What: modern .NET has no static field roots; statics live in `object[]` storage held by pinned handles.
- Decision: **fix now** (decided autonomously). 6.1 now says the ClrMD loader builds `StaticField` roots from
  static field enumeration and marks the storage array handles as non-retaining duplicates. Implementation and
  verification are in the 1.2 scope.

### M2 Root resolution rules underspecified (interior pointers, dropped roots, finalizer queue, ref-counted)
- Reviewer: gc-specialist. Location: ARCHITECTURE.md 6.1.
- Decision: **fix now** (decided autonomously). 6.1 defines floor search for `Interior` roots,
  `DroppedRootCount`, f-reachable-only `FinalizerQueue` with a has-finalizer object flag, and a `Retaining` root
  flag honored by all retention algorithms (weak never, ref-counted only while strong). Synthetic tests are in the
  0.2 scope.

### M3 No memory-safe first load on the 16 GB baseline
- Reviewer: gc-specialist. Location: ARCHITECTURE.md 6.5, 10.
- Decision: **fix now** (decided autonomously). Design: `IColumnAllocator` sink in the builder (file-backed
  allocator possible later without a rewrite), absolute budget "< 4 GB first load on 16 GB" including ClrMD
  caches, ClrMD cache capped, compacting gen2 GC after switching to mapped mode, optional `DOTNET_GCHeapHardLimit`.
  The allocator interface is built in 0.2; measurements in 1.2.

### M4 No lifetime rule for mapped views
- Reviewer: gc-specialist. Location: ARCHITECTURE.md 6.1, 8.
- Decision: **fix now** (decided autonomously). New "Lifetime of mapped data" in 6.5: ref-counted owner, a lease
  per query, dispose drains leases, spans never stored. `Column<T>` wraps `ReadOnlyMemory<T>` so a native
  `MemoryManager<T>` backing in 0.5 does not change its shape.

### M5 Reference pass order and embedded value types unspecified
- Reviewer: gc-specialist. Location: ARCHITECTURE.md 6.3.
- Decision: **fix now** (decided autonomously). 6.3 requires strictly increasing source indices (the builder
  throws otherwise), a reference pass over sorted addresses, and GCDesc based enumeration covering embedded
  structs. 0.2 tests the ordering check; 0.4 adds an embedded struct known answer.

### M6 `--version` printed the source revision
- Reviewers: senior-dev (Moderate), solution-architect (Low). Location: src/HeapScope.Cli/CliCommands.cs.
- Decision: **fix now** (decided autonomously). The built-in version action is replaced to print
  `ProductInfo.Version`; the integration test asserts exact equality with the product version and no `+`.

### M7 Malformed `--parent-pid` crashed the worker
- Reviewer: senior-dev. Location: src/HeapScope.Worker/Program.cs, WorkerOptions.cs.
- Decision: **fix now** (decided autonomously). `WorkerOptions.TryRead` validates before the host runs; usage
  errors exit with code 2 and a clear message. Integration tests added for an invalid pid (exit 2) and a parent
  that is not running (prompt exit 0). Options are bound once and registered as a singleton.

### M8 Host lifecycle in UI and CLI (sync start, dispose without stop)
- Reviewers: senior-dev, solution-architect. Location: src/HeapScope.UI.Wpf/App.xaml.cs, src/HeapScope.Cli.
- Decision: **fix now** (decided autonomously). The UI starts with `StartAsync`; closing the window cancels the
  close once, awaits a bounded `StopAsync`, disposes and shuts down; a host stop request closes the window. The
  CLI starts and stops its host around command invocation. Documented in ARCHITECTURE.md 8.

### M9 CA2007 disabled without enforcement
- Reviewer: senior-dev. Location: .editorconfig.
- Decision: **fix now** (decided autonomously). CA2007 is a warning (error) for every library project and the
  worker; it stays off with a comment for UI.Wpf, Cli, tests and benchmarks.

### M10 Capability vocabulary in Core versus the UI layering rule
- Reviewer: solution-architect. Location: LayerRules.cs, ARCHITECTURE.md 3.1, 4.4, 5.2, 15.
- Decision: **fix now** (decided autonomously). ADR-016: Contracts references no solution project and owns the
  wire vocabulary; the worker maps Core values; mirrored enum values are tested. The Contracts -> Core reference
  was removed.

### M11 Architecture checker saw only literal csproj XML
- Reviewers: solution-architect (Moderate), senior-dev (Low). Location: tests/HeapScope.Architecture.Tests.
- Decision: **fix now** (decided autonomously). The checker reads the evaluated restore graph
  (`project.assets.json`) plus build-order references from imported shared targets, and the compiled assembly
  check now covers governed package assemblies and Microsoft.Extensions.* (forbidden in Core). New tests cover
  both. This change found a real defect: the CLI assembly `heapscope` and the UI assembly `HeapScope` collide on a
  case-insensitive file system; the CLI is now `heapscope-cli`.

### M12 ClrMD symbol and DAC downloads not decided
- Reviewer: solution-architect. Location: Directory.Packages.props, ARCHITECTURE.md 12.
- Decision: **fix now** (decided autonomously), as a design decision: ADR-017, no network access by default,
  local DAC lookup, opt-in setting later. Implementation in 1.2. Flagged for the product owner to double-check.

## Low / Trivial (recorded only)
- L1 (gc-specialist) Free objects: say they are excluded from the graph; record fragmentation as aggregates.
- L2 (gc-specialist) Allocate final columns with `GC.AllocateUninitializedArray<T>`; free chunk buffers per column.
- L3 (gc-specialist) Assert "server GC True" for the deployed worker; log DATAS mode and available memory.
- L4 (senior-dev, solution-architect) Worker.Deploy.targets: `FileWrites`, stale files, fast up-to-date check,
  publish, forwarded global properties.
- L5 (senior-dev) Integration test robustness: "started" also matches the host's line, title read after exit,
  broad catch in `ChildProcess.Dispose`, dispose on failed start, cancellation tokens on waits.
- L6 (senior-dev) Integration test paths hardcoded per configuration; duplicated `FindRoot`.
- L7 (senior-dev) CLI passes args to both the host and System.CommandLine; no-argument run prints nothing.
- L8 (senior-dev, solution-architect) Parent watcher resolves the pid after start (pid reuse window).
- L9 (senior-dev) Acceptance criterion text still used the old test command. (Corrected as part of the docs
  update, since the Definition of Done requires accurate docs.)
- L10 (solution-architect) Worker resolves NETCore.Client 0.2.661903 transitively through ClrMD while Capture
  pins 0.2.750501; transitive pinning is off.
- L11 (solution-architect) Worker stderr uses the multi-line console format; prefer single-line or JSON before
  0.3 forwards it.
- L12 (solution-architect) ARCHITECTURE.md cross reference "section 5.1" and the "any diagnostics library"
  wording. (Corrected as part of the docs update.)
- L13 (senior-dev) `Build(IServiceProvider)` does not use its services yet.
- L14 (senior-dev) `PackageReference Update` without `Include` would be reported as "" (handled by the M11
  rewrite).

## Confirmations

- H1 confirmed by gc-specialist: `DependentHandle` is gone from `RootKind`; 6.1 makes dependent handles
  primary -> secondary edges with exactly one mechanism, and 6.3's reference pass agrees. The promised 0.2
  synthetic test and 0.4 `ConditionalWeakTable` and embedded-struct known answers are now in SPRINTS.md.
- M1 to M5 doc changes confirmed by gc-specialist as matching the suggestions. Its nit on the 1.5x row was
  applied: the row is scoped to machines with room, and 6.2 points to the absolute < 4 GB first-load budget.
