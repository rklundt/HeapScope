# Review: Sprint 2.1 Reverse references

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, the change set since `sprint-1.9`). Phase 2 runs
autonomously one sprint at a time (the product owner on 2026-10-24: "go - and yes autonomous - one sprint at a time"), so
Moderate findings were decided by the lead engineer with their recommendation and are marked "decided autonomously".

Totals after dedupe: 0 High, 11 Moderate, 20 Low (findings raised by more than one reviewer are merged and keep the
highest severity).

All three reviewers checked and confirmed:
- the reverse CSR build (count, fill, in-place shift; sorted rows, one entry per reference);
- the per-type counting (distinct referrers through one bitset, distinct targets per group, sizes through `GetSize`);
- the lifetime of a mapped sidecar (`HeapGraph.Attach`, leases taken before the index is read, the result set keeping
  its lease);
- the sidecar's crash safety and its identity and fingerprint checks;
- the layering (Core and Contracts reference nothing new);
- that logs carry no snapshot contents.

## High / Urgent

None.

## Moderate (decided autonomously)

- **M1 In-memory sidecar sections were limited to 2 GiB** (all three reviewers). `IndexCacheFile` read a section into one
  `byte[]` of `(int)ByteLength`, so a reverse index of more than about 536M references (or an 8-byte 2.3 section of more
  than about 268M objects) failed as malformed and was rebuilt on every open. **Fix now:** sections are read into arrays
  of their element size (byte, ushort, int, long; other sizes as bytes, refused with a clear reason above `Array.MaxLength`)
  in chunks of whole elements, so a section holds 2^31 - 1 elements like the graph's columns; the arrays are no longer
  pinned (L1). Test `Sections_of_every_element_size_round_trip` (both modes).
- **M2 Going mapped compacted the heap mid-session and threw if the reopen failed** (gc-specialist, senior-dev twice,
  solution-architect as Low). The switch to a mapped index ran a blocking, aggressive, LOH-compacting collection (copying
  every in-memory graph's arrays, against 6.5's rule) and dropped the built index before the reopen, throwing if it
  failed. **Fix now:** `ReverseIndexBuilder` writes the sidecar, maps it, and only then drops the built arrays with a
  non-compacting gen2 collection (7 to 14 ms measured, M11); if the mapping fails the index stays in memory. **Deferred to
  2.3:** building optional indices straight into the sidecar file, so a mapped computation never holds a managed copy
  (recorded in the 2.3 section).
- **M3 The memory mode ignored what the worker already holds and the forced mode** (gc-specialist). **Fix now:**
  `ReverseIndexBuilder.ChooseMode`: a forced open mode wins; next to a mapped graph mapped; next to an in-memory graph in
  memory only when it fits both the available memory and the worker's share of physical memory minus what the worker
  commits. Theory test over eight cases.
- **M4 Dependent-handle edges show as plain referrers** (gc-specialist). A `ConditionalWeakTable` key appears as a referrer
  of its value with an empty field and no explanation. **Deferred to 2.2:** recording which edges come from dependent
  handles needs a loader change and version bump, and retention paths need the same label; recorded in the 2.2 section.
- **M5 Naming fields could walk 10^8 array elements under the reader's lock** (gc-specialist). **Fix now:** a budget of 2M
  array elements per query (array lengths from the graph); when it is spent the remaining fields stay empty and the note
  says how many were named. Test `Naming_stops_when_the_querys_array_element_budget_is_spent`.
- **M6 The store's index paths had no tests; the "built once" test proved nothing** (gc-specialist, senior-dev twice).
  **Fix now:** the open-or-build logic moved to `ReverseIndexBuilder` and got worker tests: no index cache, built and
  saved then reopened, mapped from the sidecar it wrote, a sidecar that cannot be written, a stale and a damaged sidecar,
  the mode choice, and one shared computation with its progress reaching each waiter. The integration test no longer
  relies on timings.
- **M7 Progress went to the first caller only and the front ends passed none** (senior-dev, solution-architect). **Fix
  now:** `SharedProgress` sends the shared build's progress to every waiting caller for as long as it waits (the template
  for 2.3 and 2.6 slots); the object's Incoming tab and the referrer types grid show the phases. The CLI prints a note
  after the build (no live progress, like its other queries).
- **M8 An unknown type exited 0 and built the index first** (senior-dev). **Fix now:** the worker answers an unknown sort
  column or a type that matches nothing before building the index (`Index` is null then); `referrers --type` exits 1 with
  the reason. Tests in `ReverseSidecarTests`.
- **M9 Referrers of an incomplete graph looked exact** (solution-architect). **Fix now:** both results carry
  `CompleteGraph`; the UI's totals and empty texts and the CLI's stderr say that referrers may be missing. View model test.
- **M10 The cache identity was carried but never compared** (solution-architect). **Fix now:** a reopen after a worker
  restart whose index cache was rebuilt meanwhile closes what it opened and fails with `SnapshotChanged`. Tests
  `SnapshotReopenTests`.
- **M11 Measurement gaps behind the budget row** (gc-specialist). **Fix now:** the benchmark measures the release pause
  (7 to 14 ms) and fails on a reopen failure; DenseHeap mapped peak recorded (1,695 MB); a Release worker run on DenseHeap
  logged a build of 1.45 s and a peak commit of 2,644 MB (the worker now logs its peak commit), with up to 512 MB of
  ClrMD cache when fields are named; the section 10 row says it is met on the 32 GB machine and that 16 GB is not
  measured.

## Low / Trivial (listed, not fixed unless noted)

- L1 In-memory sections were pinned on the POH without need (gc-specialist). Fixed with M1.
- L2 `IncomingReferences.ForObject` grows a list by doubling; a hub with millions of referrers allocates about 3x on the
  LOH (gc-specialist).
- L3 The per-type loop indexes `IReadOnlyList<bool>` and a `BitArray` per object (gc-specialist, senior-dev).
- L4 A target beyond element 100,000 of an array referrer leaves the field empty without a note (gc-specialist).
- L5 The known-answer test checks the first row whose type contains the step, by containment (gc-specialist).
- L6 A failed incoming load is not retried when the tab is shown again (senior-dev).
- L7 `ReverseReferences.Build` checks cancellation every 1M items, DEVELOPER.md 4 says 64K (senior-dev).
- L8 The base-identity check runs after the section check, so `BaseMismatch` between a base cache and a sidecar is reported
  as Malformed; the test asserts only failure (senior-dev).
- L9 Directory messages say "index cache" for a sidecar (senior-dev).
- L10 `TypeGroups.MatchTypes` was inserted under `ModuleKey`'s summary (senior-dev).
- L11 `ReverseIndex.Seconds` was unused (senior-dev). Removed with M6.
- L12 The CLI said "later queries reuse it" when no sidecar was written (senior-dev). Fixed with M8 (`ReverseIndexInfo.Saved`).
- L13 The benchmark exited 0 on a failed reopen; some null-forgiving operators lack a comment (senior-dev). The benchmark
  part fixed with M11.
- L14 `IndexCacheFileWriter.Commit` reports any rename failure as "in use" (senior-dev).
- L15 The sidecar of `x.dmp` has the base cache name of a snapshot named `x.dmp.rev` (solution-architect).
- L16 Build optional indices into the file and check the projected peak first (solution-architect). Folded into M2's 2.3
  part.
- L17 Sidecar layouts need a semantics version before dominators (solution-architect). Recorded in the 2.3 section.
- L18 Section 7 said a rebuilt cache keeps its identity; the identity covers the metadata too (solution-architect). Fixed,
  as docs must describe what was built.
- L19 No contract test round-trips a `ulong` with the top bit set (solution-architect). The sidecar integration test
  reopens through the real pipe with a real identity.
- L20 Sidecars were not in section 12 nor in the delete backlog item (solution-architect). Fixed in the docs.

## Test runs and measurements

- Full gate before the review: build zero warnings, 770 tests, 1 failure: `WorkerIpcTests.Worker_stderr_is_forwarded_into_the_client_log`
  under full load; it passed three isolated runs (recorded in the gate history).
- Smoke run (`--smoke-open` Small): the drill shows an object's incoming references and the type's referrers (index built
  in 0.26 to 0.6 s). The first version put these views' first showing in the checked part; one run right after the gate
  failed the gate at a 108 ms operation. The drill now shows them cold with the first object and checks a second
  object's incoming references warm. Three runs after that passed: maximum lag 83 to 120 ms, longest operation 68 to
  72 ms.
- Gates at the close:
  - The first gate after the review fixes passed: 792 tests, zero warnings.
  - The next three failed once each, under full load:
    - `WorkerIpcTests.Worker_stderr_is_forwarded_into_the_client_log`, the second time this sprint. The worker's first log
      line missed its 10 s wait while several workers loaded dumps. Its wait is now 30 s; alone the line arrives in under
      a second.
    - `SessionLibraryTests.Concurrent_records_and_touches_never_lose_a_snapshot`, the known 1.8 flake, not changed.
    - `LiveViewModelTests.Samples_reach_..._stop_ends_the_run`, twice. The stop handler set `IsRunning = false` before
      the final `Status`, so a test thread could see the run stopped with the old status. The status is now set first.
  - The final gate: pass, 792 tests, zero warnings.
- Measurements: ARCHITECTURE 6.4 and 10.
