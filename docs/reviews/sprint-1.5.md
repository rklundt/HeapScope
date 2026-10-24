# Review: Sprint 1.5 Object search and inspection

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-1.4`, trace recording
included). Phase 1 runs in autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided
autonomously".

Totals after dedupe: 5 High, 15 Moderate, 21 Low (findings raised by more than one reviewer are merged and keep the
highest severity).

## High / Urgent

- **H1 Reference names were joined quadratically** (solution-architect, gc-specialist).
  `ClrMdObjectReader.ReadReferencePaths` rebuilt a target's name as `existing + ", " + path` for every array slot that
  held it. An array whose slots share one object (empty strings, a sentinel) walked up to 1,000,000 elements
  (`MaxNamedElements`) copying terabytes under the reader's lock, which blocked every other read and closing the
  snapshot. **Fixed:** each target keeps its first three names and a count ("[0], [1], [2] and 7 more"), so the walk is
  linear and a cell stays short. Test: `A_summary_row_opens_its_instances_and_an_instance_shows_the_known_field_values`
  (the `Repeated` field: ten slots holding one object). Status: fixed; confirmation below.
- **H2 Pages were formatted without holding the snapshot** (solution-architect, senior-dev). `ResultSetStore.TryGetPage`
  formatted rows outside its lock. A release or an eviction at the same moment disposed the set's owner lease, and on a
  closed snapshot the last lease unmaps the graph and disposes the reader while `InstancesSource` and `ReferencesSource`
  read them (an access violation in the worker). **Fixed:** each stored set counts the pages being formatted; a set
  removed meanwhile is disposed by the last reader instead of the remover. Test:
  `A_set_released_while_a_page_is_formatted_keeps_its_owner_until_the_page_is_done`. Status: fixed; confirmation
  below.
- **H3 A value filter or sort held a string for every object** (solution-architect, gc-specialist; senior-dev's
  cancellation and LINQ findings folded in). A text or regex filter, or a sort by value, read and kept a `string?[]`
  for every object of the type (6.6M strings in Medium: gigabytes in one result set, above the mapped-mode budget), in a
  `List<int>` that doubled, filtered with LINQ that never checked cancellation, and reported no progress. **Fixed:**
  objects are counted first and kept in an exactly sized `int[]`; a value filter reads and tests one string at a time
  and keeps only the matching indices; only a sort by value keeps text, for at most 250,000 strings (more is refused
  with "filter them first"); other sorts use one `ulong` key per object (column key and address order) and a primitive
  sort; arrays sort by value numerically by length (Moderate M5 of both reviewers); cancellation every 4,096 values and
  progress "Reading values". Measured on Medium (6.6M strings, Release CLI, from the index cache): a text filter 3.8 s
  and a regex filter 4.5 s end to end, against 1.9 s for opening alone. Tests:
  `Value_sorts_order_strings_by_text_and_arrays_by_length`, `String_instances_filter_by_text_regex_and_address_and_show_their_value`,
  the worker's `InstancesSourceTests`. Status: fixed; confirmation below.
- **H4 Multi-dimensional arrays failed inspection** (gc-specialist). Elements were read with ClrMD's single-index calls,
  which are for vectors, so any object holding a `T[,]` failed both its fields and its named references, and the array
  showed its total length. **Fixed:** an `ArraySlot` maps the position in memory to indices (with lower bounds) for
  arrays of rank above 1, names elements `[i,j]` and shows `int[2,3]`. The fixture gained `Grid` (`int[2,3]`), `Pairs`
  (an array of structs holding references, named `[1].Value`) and `Repeated`; `Inspection.json` lists them. Tests: the
  known-answer field check in `InspectionTests` and `KnownAnswerTests`. Status: fixed; confirmation below.
- **H5 A dump replaced after opening was read against the old graph** (solution-architect; senior-dev and
  gc-specialist as Moderate). The fingerprint was checked when the snapshot opened, but the reader opens the dump
  lazily, possibly hours later, so a different file at the same path would have shown wrong field values with
  confidence. **Fixed:** the reader runs a check before opening the dump; the worker recomputes the snapshot's
  fingerprint and fails with `SnapshotChanged` ("Close the snapshot and open the dump again") when it differs or the
  file is gone. Test: `A_dump_changed_after_the_snapshot_opened_is_not_read_for_field_values`. Status: fixed;
  confirmation below.

### Confirmations

Each fix went back to the reviewer that raised it; all were confirmed on re-reading the code and tests.
- H1: confirmed by solution-architect and gc-specialist.
- H2: confirmed by solution-architect and senior-dev (exactly one side disposes the owner; a removed set gains no new
  readers because lookup and count share the lock). The architect asked for ARCHITECTURE 4.6 to describe reader
  counting instead of a lease per page: done.
- H3: confirmed by solution-architect and gc-specialist (also closes M4 and M5).
- H4: confirmed by gc-specialist.
- H5: confirmed by solution-architect.

## Moderate (decided autonomously)

- **M1 No progress on the object and references calls** (solution-architect). The first inspection opens ClrMD and
  naming references walks up to a million elements. **Fix now** (protocol 5 is unreleased, so no version bump):
  `GetObjectAsync` and `QueryReferencesAsync` take `IProgress<JobProgress>` in the contract, the worker, the session and
  the front ends.
- **M2 Paging does dump I/O without the error contract** (solution-architect, senior-dev). **Fix now:** `GetRowsAsync`
  runs inside `GuardAsync` (error codes, worker log; an extra thread pool hop was tried and dropped, as it slowed
  pages under load), and an unreadable string shows an empty value
  instead of failing its page (with M14).
- **M3 A types row opens every loaded copy of its type** (solution-architect). A summary row is one method table, but
  its instances use the name and module key, so a stale copy kept apart in 1.3 merges back in when drilled into.
  **Deferred** to the backlog: a hidden snapshot-local type index on summary rows and an optional index on the request
  (diff rows keep the name and module key).
- **M4 A value filter on a snapshot without string text matched nothing silently** (solution-architect). **Fix now:**
  a value filter or sort on string types without a reader fails with `CapabilityNotSupported` (the CLI exits 1). The
  test needs a snapshot without string contents: **deferred to 1.8** (gcdumps), with the other capability tests (M12).
- **M5 Arrays sorted by value as text** (solution-architect, gc-specialist): `[1,000]` before `[999]`. **Fixed with
  H3:** numeric by length, computed from the graph.
- **M6 The reader offers display text only** (solution-architect). Detectors, duplicate-string and collection
  inspections and retention-path naming need typed reads (a field by path, elements, strings in bulk). **Deferred** to
  sprint 1.6 (Simple inspections), the first consumer, recorded in its scope.
- **M7 ADR-019 said an idle UI does no work** (solution-architect): the dispatcher watchdog probes every 25 ms.
  **Fix now:** ADR-019 corrected. **Deferred** to the backlog: slowing the probe while the window is idle or in the
  background outside smoke runs.
- **M8 A gone diff row opened an empty list** (solution-architect): ADR-018 promises the type's objects in either
  snapshot. **Fix now:** a row the current snapshot no longer has opens in the baseline. Test:
  `A_gone_row_opens_its_objects_in_the_baseline_and_a_kept_row_in_the_current_snapshot`.
- **M9 Double-clicking highlighted text could throw** (senior-dev). `FindRow` walked the visual tree from a `Run`,
  which is not a `Visual`. **Fix now:** it walks logical parents until it reaches a visual.
- **M10 A failed trace left its partial file; a hung target could hang the recording** (senior-dev). **Fix now:** any
  failure deletes `<name>.partial`; after stopping, the recorder waits at most 60 s for the stream to end, then disposes
  the session and keeps what arrived.
- **M11 A failing smoke step showed a dialog and left the trace running** (senior-dev). **Fix now:** the trace stops in
  a `finally` and its failure is logged; the drill's casts are pattern matches that fail the step; a smoke run that
  throws logs the error and exits 4 without a dialog. Checked with a traced smoke run on Small (exit 0, trace written).
- **M12 Test gaps** (senior-dev). **Fix now:** reference naming with a reader (`References_are_named_by_the_reader_and_every_edge_to_one_target_shows_its_names`),
  shared targets, struct arrays, multi-dimensional arrays, a changed dump, a release during a page (H1 to H5 tests).
  **Deferred to 1.8:** the worker's `GetObjectAsync` without `FieldValues`, the CLI's `object` on such a snapshot
  (decision 12) and M4's refusal, all of which need a gcdump.
- **M13 A reference to a non-object showed as null** (gc-specialist). **Fix now:** a non-zero reference ClrMD cannot
  resolve shows `0x... (not a valid object in this dump)`.
- **M14 One unreadable string failed a whole list** (gc-specialist). **Fix now:** the value of an invalid string is
  empty, in the filter pass and in pages.
- **M15 Known-answer coverage** (gc-specialist). **Fix now:** multi-dimensional array, array of structs with
  references, one object in many slots (H4). **Deferred** to 1.6's known answers: a boxed value in an `object` field, a
  `[Flags]` combination, strings longer than the 100 and 1,000 character cuts, an enum array.

## Final run

Debug and Release builds with zero warnings; 533 tests pass. While wrapping, the page cost budget test (200 ms median
for a 10,000-row page) crossed it at 208 ms only when run in parallel with the dump-heavy integration tests (70 to 80
ms alone); it moved to `PageCostTests` in the non-parallel `HeavyTestsCollection`, like the other measurements.

## Low (listed only)

- L1 `WorkerProtocol` comment lists versions up to 3 (solution-architect).
- L2 "Hidden" columns are a naming convention; CLI `summary` csv and json now include `TypeName` and `Module`
  (solution-architect).
- L3 The instances Generation column is filled without the `Generations` capability (solution-architect).
- L4 Traces of HeapScope's own processes are allowed though section 2 says they are never captured; in-product trace
  recording has no ADR of its own (solution-architect).
- L5 Navigator history holds `object` subjects handled by type switches; an `INavigationSubject` would let 2.x subjects
  plug in (solution-architect).
- L6 ARCHITECTURE 4.6 still names only the types and compare grids as paged; the reader's memory (ClrMD caches until
  the snapshot closes) is not measured against the mapped-mode budget (solution-architect, gc-specialist).
- L7 A canceled operation the view model did not cause shows "The operation was canceled." without a next step; the
  stale-result catch logs nothing (senior-dev).
- L8 `_lifetime` cancellation sources in `ObjectViewModel` and `VirtualRowList` are never disposed (senior-dev).
- L9 A failed page's rows stay in `VirtualRowList._items` outside the 50-page bound; enumeration creates placeholder
  rows for the whole count (senior-dev, gc-specialist).
- L10 `!` suppressions without the comment DEVELOPER.md asks for (senior-dev).
- L11 Literal column names in `TypeGridViewModel` instead of the column constants (senior-dev).
- L12 `QueryReferencesAsync` does not return the owner lease if `resultSets.Add` throws (senior-dev).
- L13 Readability nits: the inline empty `ResultSetInfo` in `ObjectViewModel`; `TraceService` maps `ArgumentException`
  to `WriteFailed`; the throttling opt-out's result is not logged (senior-dev).
- L14 `IsRooted` means "a root holds it directly", not reachable; the UI says nothing when false (gc-specialist).
- L15 Value filters match the first 100 characters plus the appended "..." (gc-specialist).
- L16 A boxed primitive shows its type, not its value; `[Flags]` combinations show only the number (gc-specialist).
- L17 The reader's ClrMD cache and DAC state stay alive per inspected snapshot until it closes (gc-specialist).
- L18 (raised while confirming) The reference walk still holds the reader lock for up to 1M slots and builds a name
  for every slot, kept or not (solution-architect, gc-specialist).
- L19 (raised while confirming) Multi-dimensional indices passed to ClrMD include the lower bound; C# arrays always
  start at 0, so only an `Array.CreateInstance` array with other bounds would show whether ClrMD expects absolute
  indices (gc-specialist).
- L20 (raised while confirming) An instances sort by Type orders by type index, not by name (equal names by
  construction, so only copies are affected) (solution-architect).
- L21 (raised while confirming) No test for an eviction, rather than a release, during a page (same code path)
  (senior-dev).
