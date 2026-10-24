# Review: Sprint 1.8 gcdump support

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, the change set since the licensing commit `140e805`,
which followed `sprint-1.7`). Phase 1 runs in autonomous mode: Moderate findings were decided by the lead engineer and
are marked "decided autonomously". The vendored code in `src/HeapScope.ThirdParty.GcDump` was reviewed for how it is
integrated and for the changes listed in its `CHANGES.md`, not for its own quality.

Totals after dedupe: 5 High, 14 Moderate, 19 Low (findings raised by more than one reviewer are merged and keep the
highest severity).

## High / Urgent

- **H1 A canceled gcdump capture never finished** (senior-dev). Once the token fired, the vendored dumper started the
  task that ends the session with the already canceled token, so it never ran; the event reader then waited for a
  session that never ended, and our writer waited on it without a limit. Ctrl+C on `capture --kind gcdump`, a canceled
  job, a monitoring run's teardown and closing the app would hang until the target exited. **Fixed:** vendored change 3
  (`CHANGES.md`) ends the session with `CancellationToken.None`; independently, `EventPipeGcDumpWriter` waits with the
  caller's token and gives a canceled session at most 10 seconds to close before returning; `GcDumpCapturer` deletes
  the partial file on cancellation and checks the token after the write. Tests:
  `Canceling_a_gcdump_capture_while_the_heap_is_reported_ends_promptly_without_a_file` (a 500,000 item heap, canceled
  300 ms into the session), `A_cancellation_during_the_snapshot_removes_the_partial_file`. With the vendored change
  reverted the integration test still passes, because the writer's bounded wait alone ends the capture; the vendored
  change makes the session end cleanly in the target. Status: fixed; confirmed by senior-dev.
- **H2 A mixed diff row could open an empty list** (solution-architect). The hidden key of a row held the gcdump form of
  a nested type's name; opening it on the full dump side matched nothing and showed no objects, for a row showing N.
  **Fixed:** `TypeDiff` keeps each side's own names per row; the key is the name on the side front ends open (the current
  snapshot, or the baseline for a gone row), or none when that side joined several differently named types under one
  gcdump name, in which case the Compare tab says why it cannot open the row. Test:
  `A_mixed_diff_row_opens_the_full_dump_side_by_its_own_name_or_says_why_it_cannot`. Status: fixed; confirmed by
  solution-architect.
- **H3 The vendored writer silently stops at 10 million nodes** (gc-specialist). It counts pseudo and placeholder nodes,
  logs a warning and reports success, so a heap at the Medium scale would have been written as a fraction of itself and
  then blamed on lost events. **Fixed:** the capture fails with "has more objects than a gcdump can hold (10 million).
  Capture a full dump instead." when the writer's warning appears; every line of the vendored log goes to the HeapScope
  log; the incomplete-gcdump note names a writer's limit as a possible cause. **Deferred** to the backlog: raising the
  cap after measuring gcdump capture and load at the Medium scale. Status: fixed; confirmed by gc-specialist (no test of
  the `TooLarge` path, Low L13).
- **H4 COM wrappers were loaded as objects** (gc-specialist). A CCW is written as a described node at the native IUnknown
  address (`[CCW 0x... for type X]`, 200 bytes) holding the managed object; an RCW object gets 1,000 extra bytes and a
  child at its COM object's IUnknown, which is never described. The loader counted CCWs as objects with one type each,
  rooted the CCW instead of the managed object, inflated RCW sizes and marked every COM-using gcdump incomplete.
  **Fixed:** CCWs are pseudo nodes (ref-counted handle roots of their managed object); RCW objects get their name and size
  back and their COM pointer is not counted as lost; undescribed children are skipped as references and roots. Tests:
  `Com_wrappers_load_as_roots_and_real_objects_without_marking_the_gcdump_incomplete` (a gcdump built by the vendored
  writer), `Pseudo_nodes_map_to_root_kinds`. Status: fixed; confirmed by gc-specialist (the loader test was added after
  its confirmation, Moderate M14).
- **H5 Finalizable objects silently showed zero on a gcdump** (gc-specialist). A gcdump records no finalizer
  registrations, but the inspection and the finalizable scope only required capabilities a complete gcdump has.
  **Fixed:** a loader fact, `SnapshotFacts.FinalizerRegistrations`, set by the full dump loader and required by both;
  a gcdump explains "needs data a gcdump does not record. Capture a full dump for it." Tests: `InspectionAvailabilityTests`,
  `Inspections_that_need_string_text_are_unavailable_and_refused_end_to_end`. Status: fixed; confirmed by gc-specialist.

### Confirmations

Each fix went back to the reviewer that raised it; all were confirmed on re-reading the code and tests (H1 by
senior-dev, H2 by solution-architect, H3, H4 and H5 by gc-specialist), together with the Moderate fixes each had raised.

## Moderate (decided autonomously)

- **M1 Capture failures gave one misleading message, and the vendored log was lost** (senior-dev, solution-architect,
  gc-specialist). The vendored dumper catches its own exceptions, so an exited target became a timeout; only the last
  log line was kept. **Fix now:** the writer keeps the last 200 lines, classifies errors, missing heaps and timeouts,
  logs every line (warnings at Warning) through DI, and an exited target becomes "exited while its heap was being
  reported". Test: `The_vendored_log_tells_errors_timeouts_and_missing_heaps_apart` and the fake-writer tests.
- **M2 Multi-dimensional arrays got wrong names** (senior-dev, gc-specialist). The writer turns `[,]` into `<,>`.
  **Fix now:** mapped back before the spacing pass; tests use the writer's real output.
- **M3 Truncation was not checked for heap dumps** (senior-dev; gc-specialist as Low). **Fix now:** `IsTruncated` also
  walks `MemoryListStream` descriptors, with counts bounded by the stream size and overflow guards; three new tests.
- **M4 No tests of the capture's failure and cleanup paths** (senior-dev). **Fix now:** `GcDumpCapturerTests` with a fake
  writer: success, elevation, each failure kind, an exited target, IO failure, a success without a file, no overwrite,
  cancellation.
- **M5 Half-written gcdumps were never swept** (senior-dev, solution-architect). **Fix now:** the sweep covers
  `*.gcdump.partial`; test `Old_half_written_dumps_and_gcdumps_are_swept_and_fresh_ones_kept`.
- **M6 Mixed diffs flagged "copies" on common rows** (solution-architect). Mapped names join types, so type counts per
  row differ by construction. **Fix now:** no copies marker across kinds.
- **M7 Index caches did not record loader semantics** (solution-architect). Caches written by 1.7 kept claiming complete
  graphs for truncated dumps. **Fix now:** `IndexCacheMetadata.LoaderVersion` and `ISnapshotLoader.Version` (full dumps
  2, gcdumps 1); an older cache is rebuilt with a note. This also gives old caches the new finalizer fact.
- **M8 The Compare tab's totals misdescribed gcdumps and mixed pairs** (solution-architect, gc-specialist). **Fix now:**
  worded per pair of kinds, with the mixed-kind caveat (dead objects in the dump, gcdump names); "sampled or incomplete".
- **M9 Reading a gcdump file had no progress or cancellation** (solution-architect). **Fix now:** the vendored reader
  reads through `ObservedReadStream`; test `Reading_a_gcdump_reports_progress_and_stops_on_cancellation`.
- **M10 Objects of 2 GB and more got size 0** (gc-specialist). **Fix now:** sizes read unsigned (exact up to 4 GB).
- **M11 Dependent handle values looked like garbage** (gc-specialist). **Fix now:** retaining other roots ("dependent
  handle value (key not recorded)"), ARCHITECTURE 6.1 and 5.3 amended, decision 9 changed.
- **M12 Static roots were untested and misnamed** (gc-specialist). .NET 10 does report statics, but by the value's type.
  **Fix now:** the detail reads `s_cache (Dictionary<System.Int32, ...>)`; test
  `The_static_cache_is_a_static_field_root_named_by_its_field_and_type`.
- **M13 Lost type events went unnoticed** (gc-specialist). **Fix now:** an `UNKNOWN 0x...` type makes the load
  incomplete.
- **M14 The loader's COM path was untested** (gc-specialist, on confirming H4). **Fix now:** the synthetic gcdump test
  under H4; it also fixed a large RCW (`[RCW X] (Bytes > 1K)`) not being recognized.

## Low / Trivial (listed, not fixed unless noted)

- L1 `CaptureAsync` took the token before the timeout (senior-dev). Fixed in passing with M1.
- L2 A cancellation during the final write was not honored, and every `File.Move` failure was called a name clash
  (senior-dev). Fixed in passing with M1.
- L3 The single-line log writer still buffered `Write` calls (senior-dev). Replaced by the bounded line log (M1).
- L4 Loader allocations: lists grown by doubling, references added by address, the MemoryGraph alive through the build
  (senior-dev, gc-specialist). Backlog.
- L5 A damaged gcdump with duplicate addresses or bad type indices fails in the builder, not with the "damaged" message
  (senior-dev).
- L6 `CapabilityDescriptions` still says a gcdump without `CompleteGraph` "was sampled"; lost events and the node cap do
  that too (solution-architect).
- L7 Nested types inside generic arguments are assumed to keep their declaring type in a gcdump; no known answer covers
  it (solution-architect, gc-specialist).
- L8 In a mixed diff grouped by namespace, the full dump's nested types fall into the no-namespace row
  (solution-architect).
- L9 Task 7's text names an Application `CaptureKind`; the code uses Contracts' `SnapshotKindDto`, and an undefined value
  is not validated (solution-architect).
- L10 Whether TraceEvent's FastSerialization refuses unregistered type names in a crafted gcdump is unconfirmed
  (solution-architect). Backlog.
- L11 The truncation theory copies up to 90 percent of the dump into one array (gc-specialist).
- L12 The vendored lossy-buffering fallback warning is printed to the console, not the log (solution-architect,
  gc-specialist).
- L13 No test of the `TooLarge` path (gc-specialist).
- L14 The integration cancellation test cannot detect a revert of vendored change 3 (senior-dev).
- L15 A cancellation during `WriteMemoryGraph`, or a session that outlives the 10 second wait, can leave a
  `.gcdump.partial` until the next sweep (senior-dev, solution-architect).
- L16 A reopen after a worker restart of a cache from an older loader fails with the generic "changed or gone" message
  (solution-architect).
- L17 Index caches written before 1.8 lack the finalizer fact (gc-specialist). Covered by M7: they are rebuilt.
- L18 The CCW check in the root walk allocates node storage per child (gc-specialist).
- L19 Rank-1 arrays recorded as `<*>` are not mapped (gc-specialist, trivial).

## Test runs

- Full suite after the review fixes: 711 of 711 passed before the last two tests were added (712 with the COM test);
  zero warnings in Debug.
- Earlier full runs in this sprint failed once each in `LiveViewModelTests.Samples_reach_the_chart_rules_become_cards_and_markers_and_stop_ends_the_run`
  and `SessionLibraryTests.Concurrent_records_and_touches_never_lose_a_snapshot`, code the sprint did not change; both
  passed three isolated runs, and the second passed 8 parallel stress runs. Recorded as load-dependent one-offs.
- The truncated dump theory was run five times in a row on five freshly captured dumps after the truncation fixes.
