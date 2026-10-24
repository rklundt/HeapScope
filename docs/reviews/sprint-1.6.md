# Review: Sprint 1.6 Simple inspections

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-1.5`). Phase 1 runs in
autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided autonomously".

Totals after dedupe: 3 High, 18 Moderate, 19 Low (findings raised by more than one reviewer are merged and keep the
highest severity).

## High / Urgent

- **H1 Finalizable objects missed those ready for the finalizer thread** (gc-specialist). `HasFinalizer` marks only
  objects in the finalizable-object list (registered, still alive); objects already dead and queued for the finalizer
  thread (the f-reachable queue, recorded as `FinalizerQueue` roots) were not counted, so a blocked or slow finalizer
  thread, the classic finalizer leak, showed almost nothing. **Fixed:** the inspection counts both and shows a "Ready"
  column (rows with ready objects first); the finalizable instance scope uses the same set (`Finalization.ReadyObjects`).
  The Waste fixture gained `WasteDoomed` objects queued behind a finalizer that never returns. Tests:
  `Finalizable_objects_are_those_registered_and_those_ready_for_the_finalizer_by_type`, the Waste known answer
  (`finalizable WasteDoomed`, at least 10) in `KnownAnswerTests` (raw ClrMD, `EnumerateFinalizerRoots`) and
  `InspectionsTests`. Status: fixed; confirmation below.
- **H2 Duplicate strings read whole strings, a chunk at a time** (gc-specialist, solution-architect; senior-dev as part
  of a Moderate). Strings were read in full (`int.MaxValue`) and 4,096 texts kept per chunk; equal-size large payloads,
  the case the inspection exists for, could put gigabytes in the worker; the equal-strings scope did the same.
  **Fixed:** the reader streams a string's UTF-16 text from the dump in pieces of one pooled 128 KB buffer
  (`ReadStringPieces`); Analysis hashes the pieces incrementally (`StringHashes`, XxHash3), so no string is built; each
  size group sorts its (valid, hash) keys instead of filling a dictionary (folds in gc-specialist's group-memory
  Moderate); the equal-strings scope compares the same hashes. The fixture gained three 200,000-character copies.
  Status: fixed; confirmation below.
- **H3 No test that duplicate strings is unavailable without string contents** (senior-dev). An acceptance criterion.
  **Fixed:** availability is one rule, `InspectionAvailability.Explain` (capability reason, or a missing loader fact),
  used by listing and by running (which refuses with `CapabilityNotSupported` and the same reason). Tests:
  `InspectionAvailabilityTests` (gcdump capabilities: duplicate strings and collections unavailable with the capability
  texts, LOH available; every inspection available on a 1.6 full dump; an old cache points at Rebuild). The end-to-end
  check on a real gcdump comes with 1.8. Status: fixed; confirmation below.

### Confirmations

Each fix went back to the reviewer that raised it; all were confirmed on re-reading the code and tests.
- H1: confirmed by gc-specialist (ready objects are not in the registered list, so none is counted twice; at least 10
  is right because the finalizer thread holds one).
- H2: confirmed by gc-specialist and solution-architect (one pooled buffer per call whatever the strings; a half-read
  string never yields a hash; only the shown 101 characters per row are built). Both noted that hashing ran under the
  reader lock for a whole count-limited batch: M18.
- H3: confirmed by senior-dev (one rule used by listing and running; the test asserts the exact capability texts).

## Moderate (decided autonomously)

- **M1 Availability ignored facts the loader may not have recorded** (solution-architect; senior-dev on sparse arrays).
  Sparse arrays needs reference-array marks and guessed an old cache from missing free space; a gcdump would get the
  same wrong note. **Fix now:** `SnapshotFacts` on `SnapshotInfo` (free space, reference-array marks), set by the loader
  and absent from older caches; inspections declare `RequiresFacts`; sparse arrays is unavailable without the marks
  ("reopen with Rebuild" for a full dump). Fragmentation still runs without free space, with its note.
- **M2 Drill-down vocabulary copied by hand across projects** (solution-architect, senior-dev). **Fix now:** Contracts
  owns the shared names (`InspectionColumns` with the fragmentation columns and `IsHidden`, `InspectionNames`); the UI
  and CLI use them; `Inspection_vocabulary_mirrors_the_wire` checks Analysis's scopes and columns against Contracts.
  **Deferred:** a hidden flag on `ResultColumn` (with the 1.5 review's L2).
- **M3 An evicted inspection set re-runs the whole inspection silently** (solution-architect). **Deferred** to the
  backlog: keep each inspection's compact result per snapshot so a re-query only rebuilds the set, with progress.
- **M4 The path for 2.6 detectors is not defined** (solution-architect). **Fix now (documentation):** ADR-020 records
  that the context grows additively (derived data such as reverse references and dominators, an optional baseline,
  requirements beyond capabilities) and that runs may become asynchronous then.
- **M5 Late results of a canceled run reached the new snapshot's bars** (senior-dev). **Fix now:** cancellation is
  checked after every await before shared state is touched.
- **M6 The inspection run was fire-and-forget** (senior-dev). **Fix now:** a final catch at the boundary logs and
  shows the failure; tile failures are logged at Warning.
- **M7 A tile selected while running never showed its rows** (senior-dev). **Fix now:** the grid reloads when the
  selected tile's run finishes. Test: `A_row_naming_one_object_opens_that_object_and_a_tile_selected_while_running_fills_when_done`.
- **M8 The fragmentation known answer proved little** (senior-dev). **Fix now:** the integration test asserts the
  handle-pinned buffer (a pinned object in Gen0 to Gen2), the pinned object heap buffer and free bytes on every row.
- **M9 Test gaps** (senior-dev). **Fix now:** released sets on a snapshot switch, fragmentation bars, an address-only
  row opening its object, a drained Dictionary and an empty Queue, arrays of collections ignored, placeholder cells.
- **M10 Placeholder rows failed their cell bindings** (senior-dev). **Fix now:** placeholders carry a blank cell per
  column. Test: `Placeholder_rows_have_a_blank_cell_for_every_column`.
- **M11 Per-object LINQ, delegates and doubling lists** (senior-dev; gc-specialist's Low on per-type keys folded in).
  **Fix now:** per-type totals in arrays merged by key once, duplicate strings counts into exact arrays, fragmentation
  walks the root table once. **Deferred:** the remaining LINQ in the collection pass (with M15).
- **M12 Sparse arrays counted dependent-handle edges and dropped references** (gc-specialist). **Fix now:** null slots
  are clamped at zero and a note names dropped references. **Deferred** to the backlog: counting dependent-handle edges
  apart from fields.
- **M13 Stack pins and async pinned buffers were not counted as pinned** (gc-specialist). **Fix now:** any root flagged
  `Pinned` counts (stack pins). **Deferred** to the backlog: expanding async pinned handles to their user buffers, with
  an overlapped I/O fixture.
- **M14 Fixture gaps** (gc-specialist). **Fix now:** a Dictionary emptied by removals, large equal strings, objects
  ready for finalization. **Deferred:** stack and async pins (with M13), a reference array that is a weak table key
  (with M12).
- **M15 Collection reads cost several locked reads each, twice per run** (gc-specialist). **Fix now:** arrays of
  collections and nested types are skipped. **Deferred** to the backlog: field offsets resolved once per type, bulk
  reads under one lock, shapes shared by the empty and oversized inspections.
- **M16 Duplicate strings group memory** (gc-specialist). **Fixed with H2** (sorted keys, no dictionary).
- **M17 Sparse arrays guessed an old cache** (senior-dev). **Fixed with M1.**

- **M18 Hashing ran under the reader lock for 4,096 strings at a time** (gc-specialist, solution-architect, raised
  while confirming H2). A batch of large strings kept other reads (pages, the object view) waiting for seconds.
  **Fix now:** the lock is released after 16M characters as well as after 4,096 strings. A single string larger than
  that is still read in one acquisition (backlog item with M15).

## Low (listed only)

- L1 Requirements differ across inspections for degraded dumps; results carry no `ExactCounts` (solution-architect).
- L2 Duplicate-strings rows keep up to 101 characters of text per row in the result set (solution-architect).
- L3 `FreeBytes` is a list indexed by heap kind value; `SnapshotInfo` equality is no longer by value
  (solution-architect).
- L4 Task 5's text mentions the heap layout on the descriptor (decision 13 dropped it); DEVELOPER.md has no line on the
  Inspections tab; the CLI help lists inspection names by hand (solution-architect).
- L5 Large arrays includes pinned object heap and frozen arrays; its cause line says "large object" (gc-specialist).
- L6 Oversized hashed collections count unused entries but not unused buckets (gc-specialist).
- L7 Enum flag names are composed for every enum, not only `[Flags]` ones; enum members are rebuilt per element
  (gc-specialist).
- L8 `ReleaseQuietlyAsync` in `inspect` is not in a `finally` (senior-dev).
- L9 Fragmentation rows open nothing; the grid tooltip says they open objects (senior-dev).
- L10 The reader-inspection class is named `Collections` (senior-dev).
- L11 The smoke log line builds its findings with interpolation; it holds counts only, no values (senior-dev).
- L12 Old cancellation sources were not disposed (senior-dev; fixed in passing with M5).
- L13 `AllRowsAsync` in the integration test could loop on an empty page (senior-dev; fixed in passing).
- L14 Unloaded-row bindings noise (folded into M10).
- L15 Cancellation of the reader-based inspections has no test (senior-dev).
- L16 (raised while confirming) No worker test runs an unavailable inspection end to end; the refusal is one line over
  the tested rule (senior-dev; the 1.8 gcdump tests cover it).
- L17 (raised while confirming) The memory bound of duplicate strings is not tested; a fake reader recording the
  largest piece would guard it (solution-architect).
- L18 Large arrays' cause line and its pinned and frozen rows (see L5) (gc-specialist).
- L19 The Small smoke dump's cache predates 1.6, so its smoke run shows sparse arrays as unavailable until rebuilt.

## Final run

Debug and Release builds with zero warnings; 593 tests pass. Small smoke runs: the first, started right after the
full suite, measured a 103 ms open phase (before the inspections phase, which took 24 ms); the next three passed with
45 to 47 ms. Logged as one more sample for the 0.3 backlog item on lag under machine load.
