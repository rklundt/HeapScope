# Review: Sprint 1.2 Full dump loader

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-0.5`). Phase 1 runs in
autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided autonomously".

Totals after dedupe: 4 High, 19 Moderate, 20 Low.

## High / Urgent

### H1 A dump moved with its cache reported, and reopened, its old path
- Reviewers: solution-architect, senior-dev.
- Location: SnapshotStore.cs (`GraphSnapshot(metadata.Info, ...)`, `Describe`, `FindOpen`); SnapshotSession.cs.
- What: a snapshot opened from its index cache took `SourcePath` from the cache metadata (the path of the first
  load). The fingerprint does not depend on the path, so a dump moved or copied with its `.hsidx` opened from cache
  under the old path: the session keyed its handle on it, a reopen after a worker restart asked for the old path, and
  `FindOpen` never matched, so every open loaded another copy into the worker.
- Why it matters: leaked graphs, failed crash recovery, possibly results for the wrong file.
- Fix: the store describes every snapshot with the path that was opened (`metadata.Info with { SourcePath = path }`).
  Test `A_dump_moved_with_its_cache_reports_and_reopens_the_new_path` moves dump and cache, opens twice (same id),
  restarts the worker and uses the handle.
- Status: **Fixed.**

### H2 Paths from the dump could make the worker reach a network share or load native code from it
- Reviewer: solution-architect.
- Location: DacLocator.cs (`CandidateDirectories`), LocalOnlyFileLocator.cs.
- What: the dumped runtime's folder (a path recorded in the dump) was searched first, and the file locator accepted
  any rooted path; UNC paths and network drives were not rejected, and the only gate was a PE time stamp and size also
  taken from the dump.
- Why it matters: SMB access with NTLM credentials and native code loaded from a location an untrusted dump chose;
  contradicts ADR-017 and section 12.
- Fix: candidates and locator paths must be local fixed-drive paths (`DacLocator.IsLocalFixedPath`: no `\\`, `\\?\UNC`,
  `//`, relative or non-fixed drives); the dumped runtime's folder counts only inside an installed runtime folder
  (no `..`); every installed runtime folder is now a candidate, so a dump from another machine still finds its DAC by
  PE match. Section 12 and ADR-017 say a DAC next to the dump or in the DAC folder is trusted native code. Tests:
  UNC, `\\?\UNC`, `//`, outside and `..` runtime folders, network dump and DAC folders, `IsLocalFixedPath` cases, and
  an integration test loading the fixture dump with the recorded folder ignored.
- Status: **Fixed.**

### H3 Docs claimed a dump-backed snapshot; capability messages told full-dump users to capture a full dump
- Reviewer: solution-architect.
- Location: ARCHITECTURE.md 5.1; SnapshotFeatures.cs (`CapabilityDescriptions`).
- Fix: 5.1 says every snapshot is a `GraphSnapshot` in 1.2 and the full-dump `ISnapshot` with dump-backed services is
  planned for 1.5 (added to its scope). `ExplainMissing(missing, kind)` explains full-dump gaps as "not available in
  this version yet" or "could not be read from this dump"; a test checks no full-dump explanation says "capture a full
  dump".
- Status: **Fixed.**

### H4 Dumps without the heap, or truncated ones, would still claim complete and exact answers
- Reviewer: gc-specialist.
- Location: ClrMdDumpLoader.cs.
- What: only `CanWalkHeap` was checked; a mini or triage dump, or a truncated copy, still produced `FullDump`
  capabilities including `CompleteGraph` and `ExactCounts`.
- Fix: `MinidumpHeader` reads MINIDUMP_HEADER before ClrMD: not a minidump fails with a format error (gcdump later);
  no `MiniDumpWithFullMemory` or `MiniDumpWithPrivateReadWriteMemory` fails with how to capture a full dump. Each
  segment walk is measured (bytes walked including free objects); a segment missing more than 5 percent and more than
  1 MB, or any unreadable object, drops `CompleteGraph` and `ExactCounts` and adds a note. Thresholds set from the
  Roslyn dump (an unfilled 24 KB gen0 region walked 37 percent; other segments 97 to 99 percent). Tests: header with
  no heap flags, a real `DumpType.Normal` dump, a 60 percent truncated copy (refused or incomplete), shared dumps
  complete.
- Status: **Fixed.**

## Moderate (decided autonomously)

- **M1 Session lifecycle gaps** (closed handle resurrected by a reopen, reopen not tracked and possibly a full load,
  mode dropped; architect and senior-dev). **Fix now:** handles keep `RequestedMode` and `IsClosed`; `UseAsync` refuses
  a closed handle; a reopen that finishes after a close closes its worker snapshot; reopens use `RequireCache` and fail
  with `SnapshotChanged` (1011) instead of loading the dump. Tests: changed dump not reloaded, closed handle never
  reopened.
- **M2 No cache identity in descriptor or worker entry** (architect). **Deferred to 2.1**, which adds sidecars keyed by
  identity and the opened-cache API (backlog entry).
- **M3 Memory warning rule duplicated in UI and CLI, ignoring the fallback cache location and stale caches**
  (architect, senior-dev). **Fix now:** `ProbeSnapshotAsync` in the worker (size, cache state from header and
  fingerprint, open or not) and `SnapshotSession.GetOpenWarningAsync` used by both front ends; the view model no longer
  touches the file system. Tests: probe states, warning only for a first load, missing file.
- **M4 Loader contract types live in Loaders.ClrMd; no format detection** (architect). **Partly fixed:** the minidump
  signature is checked (other files get a clear message). Moving `LoadResult`/`LoadProgress` into a Core loader
  contract is **deferred to 1.8** (scope note).
- **M5 I/O and access failures uncoded; failed requests not logged; ClrMD failures without a next step** (architect,
  senior-dev). **Fix now:** `SnapshotUnavailable` (1010) with a plain message and next step; `GuardAsync` logs every
  failure with its stack trace (warning coded, error otherwise).
- **M6 Symbol options per installation, readable from environment variables, no privacy note** (architect). **Fix
  now:** the worker reads options from its command line only; client and worker log a warning when downloads are on;
  ADR-017 amended to "per installation".
- **M7 Cancellation criterion tested only in the best case** (all three). **Fix now:** checks inside the handle loop and
  per 1,024 types in the static roots loop; the worker test waits for a load phase and asserts under 2 s; `load-dump
  --cancel-at` measured on the dense Medium dump: 0.2 to 1.1 s at every phase.
- **M8 CLI could not set the DAC folder or the opt-in** (senior-dev). **Fix now:** recursive `--dac-folder` and
  `--allow-symbol-server`; the missing-DAC message names both.
- **M9 Memory mode chosen while load garbage and the graph were still committed** (senior-dev, gc-specialist). **Fix
  now:** collect first, then select with the graph's own bytes counted as available; the decision is logged.
- **M10 Unknown DAC index accepted any file** (senior-dev, gc-specialist). **Fix now:** without an index the file
  version must equal the runtime version. Test updated.
- **M11 `load-dump` checked no budget** (senior-dev). **Fix now:** first load, peak (1.5x heap and 4 GB) and both
  reopens checked, exit code 1 on a miss.
- **M12 Docs drift: synthetic job's own budget, unused `Worker:MemoryBudget`** (senior-dev). **Fix now:** the synthetic
  job uses `MemoryBudget.Current()`; the doc no longer promises the key (a configurable fraction is in the backlog).
- **M13 Interior roots relied on ClrMD's resolution** (gc-specialist). **Fix now:** the raw slot value is passed with
  `Interior`, so HeapScope's bounds check decides; the test asserts the `Interior` flag.
- **M14 Frozen objects have no rule** (gc-specialist). **Fix now (rule), implement with 2.x:** 6.1 says frozen objects
  are alive (a virtual root) for reachability, dominator and garbage analyses.
- **M15 Statics of collectible types rooted as retaining** (gc-specialist). **Fix now:** recorded without `Retaining`
  (`ClrType.IsCollectible`); documented in 6.1. A collectible-context fixture is **deferred** (backlog).
- **M16 The `DumpLoadResult` local kept the built graph alive through the mapped transition** (gc-specialist). **Fix
  now:** the store extracts what it needs and clears the reference before collecting.
- **M17 A compacting collection after every open, cache hits included** (gc-specialist, senior-dev Low). **Fix now:**
  only after dump loads and failed or canceled loads.
- **M18 Estimate calibrated on sparse dumps only** (gc-specialist). **Won't fix beyond measurement:** the dense dump
  (3.03 references per object) gives an estimate of 3.2 GB against 2.6 GB measured, so the constants hold; recorded in
  section 10.
- **M19 Tests did not prove several claims** (gc-specialist). **Fix now:** edge count equals ClrMD references plus live
  dependent handles (fixture and dense heap), handle and static roots not duplicated, finalizer roots only from the
  f-reachable queue and registered-only objects not rooted, `Interior` flag. Generation assertions per object are
  **deferred** (backlog).

## Low / Trivial (recorded only)
- L1 (architect) `GetTypeSummaryAsync` has no progress parameter.
- L2 (architect) Decision 6 says the summary is owned by a lease; the lease only covers its creation. (Decision text
  aligned.)
- L3 (architect) Opening an open file with a different mode returns the open snapshot. (Documented on the interface.)
- L4 (architect) Section 3 code map does not list the new types.
- L5 (architect) Decision 2 named the header bit, the code uses `IsFinalizeSuppressed`; task 6 named
  `WorkerOperations.StartOpenSnapshot`. (Aligned.)
- L6 (architect) File checks ran on the dispatcher. (Gone with M3.)
- L7 (senior-dev) Graph not disposed when description failed after a load. (Fixed while fixing M16.)
- L8 (senior-dev) Unexplained `!` suppressions; `MessageBox.Show` with a null owner.
- L9 (senior-dev) Several files hold more than one public type.
- L10 (senior-dev) `PhysicalMemory` duplicates Storage's P/Invoke.
- L11 (senior-dev) `SnapshotItemViewModel` not observable; overview load not cancelable; cancel shown as a raw message.
- L12 (senior-dev) Store disposal during a load. (Fixed: disposed flag, gate kept.)
- L13 (senior-dev) Shell disposes a DI singleton; `Snapshots` depends on `Rpc.ThrottledProgress`; UiHost recomputes the
  smoke flag.
- L14 (senior-dev) Decision 14 says the worker is published; the targets copy its build output.
- L15 (senior-dev) Exit code 130 and `--no-overcommit` not tested.
- L16 (senior-dev) Process name is "dotnet" for apps started as `dotnet app.dll`.
- L17 (gc-specialist) `GetObject` and an iterator for pointer-free objects in the reference pass.
- L18 (gc-specialist) Finalizable set probed for every object; f-reachable objects lack `HasFinalizer`.
- L19 (gc-specialist) Zero-count ref-counted and other weak handle kinds not recorded.
- L20 (gc-specialist) Peak sampled every 100 ms; numbers from a 32 GB, 24-core machine; the loader collects
  process-wide; large pooled buffers stay in the shared pool.

## Confirmations
- H1 confirmed by solution-architect and senior-dev: the snapshot is built from the opened path; Describe, FindOpen and
  the session key on it; the move test checks the cache reuse, both paths, the same id and a reopen after a restart.
- H2 confirmed by solution-architect: local fixed-drive paths only, the dumped runtime folder only inside installed
  roots (no `..`), all installed runtimes searched by PE match, version match without an index, locator guarded,
  tests cover UNC, `\\?\UNC`, `//`, traversal and network folders. Its Low leftovers were taken: section 12 says the
  guarantee holds while the opt-in is off, and the first ADR-017 paragraph was aligned with the amendment.
- H3 confirmed by solution-architect for code and ARCHITECTURE.md; it asked for the 1.5 scope line, now added
  (full-dump `ISnapshot` with `IFieldValueReader` and the string and thread stack services).
- H4 confirmed by gc-specialist: header check, per-segment completeness, reduced capabilities persisted in the cache
  with the note, tests present. Its two Low weaknesses (no total across small regions; the truncation test can pass
  without the segment check firing) are in the backlog.
- Moderate re-check: all confirmed (M1, M3, M5, M6, M7, M8, M9, M11, M13, M15, M16) except M17, where gc-specialist
  found the new pre-decision collection compacted the LOH under the live graph and a successful load collected two or
  three times. Fixed: no LOH compaction before the mode decision (only in the mapped transition, after the graph is
  dropped), and the final collection runs only after failed or canceled loads (not after a cache-only miss either).
  Remaining Lows from the re-check (ClrMD runtime creation failures not wrapped, now in the 1.5 scope; a disposal race
  during worker shutdown; the 512 MB floor of the 1.5x check, now stated in section 10) are recorded only.
