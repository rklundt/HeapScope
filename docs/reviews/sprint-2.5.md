# Review: Sprint 2.5 Allocation tracking

Changes since `sprint-2.4` (63 files). Four perspectives, run in parallel: solution-architect, senior-dev, gc-specialist,
and a support and system analyst (a general reviewer with a read-only brief, kept since 2.3.7 so the logs can back up any
result a user contests). Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go with your reco
for moderate recommendations"), marked "decided autonomously". The first solution-architect run stalled without a
report and was run again with a narrower brief.

Totals after dedupe: 4 High, 25 Moderate, 28 Low. A finding raised by more than one reviewer is merged and keeps the
highest severity.

The reviewers confirmed:
- layering holds: `Loaders.Trace` references Core and TraceEvent only, the 3.1 table and diagram match `LayerRules`, the
  front ends use only Application and Contracts types (solution-architect);
- `AllocationAmount64` is the right field and summing it is a sound estimate; Server GC ticks per heap keep the rate per
  byte; the interval binary search, the call tree's CSR and totals, and back-trace grouping are correct; the rundown
  request includes ReadyToRun methods (gc-specialist);
- no em or en dashes and every changed file has its license header (senior-dev).

## High / Urgent

- **H1 Unnamed managed frames were labeled "[native code]" and merged, with nothing saying why** (gc-specialist,
  support). EventPipe walks managed frames only: a frame without a name is a managed method the trace does not name,
  usually because the recording ended before the rundown (the process exited, or the stream-end timeout). Merging runs
  hid different methods behind one row; the open line logged neither a rundown flag nor unresolved counts; a cut-off
  file was accepted silently (`ContinueOnError`, conversion log discarded). Wrong top stacks with no evidence in the log.
  Fix: unnamed frames are `[unresolved frame in <module>]` (or `[unresolved frame]`), never merged; the reader counts
  samples with unresolved frames, detects the rundown (rundown provider events) and keeps TraceEvent's conversion
  problems (count and the first three lines); `AllocationTraceInfo` and `TraceDescriptor` carry them; the worker's open
  line logs them; the UI's note and the CLI's notes say what happened and what to do (`AllocationTexts.NotesFor`:
  "Record again and let the recording finish"). Tests: the fixture warms the leaks before recording, so only the
  rundown names them (`HasRundown` true, unresolved samples under 5 %); a recording whose process exits has no rundown
  and says so. Status: Fixed; confirmation below.
- **H2 The memory reservation rested on a misreported measurement** (gc-specialist). The benchmark divided the
  process's absolute peak by the file size, so "4.6 times the file" was mostly the runtime's baseline, from one 5.6 MB
  trace. Fix: the benchmark reports growth over the private bytes before the read, with the worker's GC settings
  (Server GC, 12 heaps; the re-review found the first runs used DATAS and read low). Three traces: 5.6 MB 40 MB growth,
  25.6 MB 131 MB, 72 MB (600 s, 565,625 samples) 227 MB, about 76 MB plus 2.1 bytes per file byte. The reservation is
  now 96 MB plus 3 bytes per file byte (`TraceStore.ReadReservation`); section 10 is corrected. Status: Fixed;
  confirmation below.
- **H3 Reading a trace again after a worker restart never checked it was the same file** (support). Rows and open keys
  could silently come from a different file. Fix: `TraceDescriptor` carries the file's size and write time (logged on
  open); a re-read compares them and refuses a changed file with `TraceChanged` (1013), closing the new read and
  remembering it on the handle so later queries refuse at once; both identities are logged. Test:
  `A_trace_whose_file_changed_is_not_read_again_silently`. Status: Fixed; confirmation below.
- **H4 The CLI labeled all-types totals as one type's** (support). With `--view types --type X` the header and the
  JSON said "type X" over every type's bytes. Fix: `--type` with the types view is a usage error (it narrows the stacks
  only); the summary's types table is headed "All types from ..."; the totals come from the answers shown. Status:
  Fixed; confirmation below.

## Moderate (all decided autonomously)

- M1 Concurrent queries after a worker restart each read the trace again and leaked copies (solution-architect,
  senior-dev, support). Fixed now: one re-read per handle (`TraceHandle.Reopening`), the others wait and use it; test
  `After_a_worker_restart_one_action_reads_the_trace_once`.
- M2 TraceEvent's conversion cannot be canceled; the read checked cancellation on allocation ticks only (all four).
  Fixed in part now: `Build` checks every 4,096 events of any kind; the conversion's status says a cancel takes effect
  after it; ADR-028 records it. Deferred to the Backlog with the streaming reader: canceling the conversion itself.
- M3 The timeline answer had no bucket limit (solution-architect, gc-specialist). Fixed now:
  `AllocationTreeLimits.MaxBuckets` (10,000), refused with a plain message.
- M4 The timeline, types and stacks queries ran inline and ignored the token (solution-architect, support). Fixed now:
  they run on the pool and pass the token into `AllocationProfile`, which checks it every 64K samples. Progress on them
  is deferred (they take milliseconds on the measured traces).
- M5 The flattened-tree paging is copied from ADR-027's tree (solution-architect, senior-dev). Deferred to the Backlog:
  extract a shared builder when 2.6 or Compare adds a third tree, with one set of limits in Contracts.
- M6 Types keyed by name; allocation kind, type id and module dropped (solution-architect, gc-specialist). Fixed in part
  now: each sample keeps its kind (small, large or pinned object heap), and the types answer has LOH and POH byte
  columns. Deferred with Compare's "allocated between": the type id and module, needed to join trace types to diff rows.
- M7 Kept trace memory unaccounted; the reservation borrowed a snapshot mode (solution-architect, gc-specialist). Fixed
  now: `ReserveWorking` takes a trace-specific next step and no snapshot mode. A standing reservation per open trace was
  tried and removed in the re-review: the arrays are already in the committed memory every reservation counts, so it
  counted them twice; the open line logs what a trace keeps.
- M8 The stacks line logged ten stacks of four frames; disputed frames sit deeper (support). Fixed now: every stack
  answered, with its rank, bytes, samples, innermost node (which rebuilds it from the same trace), frame count and the
  first 16 frames.
- M9 Truncated or damaged traces were read silently (support). Fixed with H1.
- M10 A trace without allocation samples opened with only "nothing sampled" (support). Fixed now: refused with the
  profile to use.
- M11 The CLI's `--type` text reached the logs and the error message verbatim (support). Fixed in part now: the
  unknown-type message no longer repeats it and names the types view. The request line keeps logging type names as
  metadata names, like `referrers --type` (ARCHITECTURE 11).
- M12 A canceled recording was kept silently; Stop was not logged; Stop stayed enabled while reading (support,
  senior-dev). Fixed now: a canceled job deletes its recording and logs it; the session logs why the recording ended
  (duration, Stop, process exit, cancel); Stop is disabled once reading starts.
- M13 A good recording that could not be read did not say where it was (support). Fixed now:
  `TraceRecordedButNotReadException` names the path and how to open it later.
- M14 The worker's versions line had no trace reader (support). Fixed now: reader version and TraceEvent's version.
- M15 The CLI and the UI computed a stack's % differently (support, senior-dev). Fixed now: a share of the bytes shown
  in both, said by `AllocationTexts.ShareOf`; the extra totals query is gone.
- M16 Read and memory errors had no next step (support, senior-dev). Fixed now: "record it again", "wait until the
  recording ends", and a trace-specific memory step with the file's size.
- M17 `.etlx` files left by a killed worker were never removed; `*.nettrace.partial` was not swept (support,
  gc-specialist, senior-dev). Fixed now: the worker sweeps `.etlx` files over an hour old that no reader holds, and
  logs it; the session sweep includes partial traces.
- M18 A closed tab's loads ran on and could fault `LoadTask` (senior-dev). Fixed now: `AllocationTraceViewModel.Close`
  cancels them; the timeline catches like the stacks; `LoadTask` is the latest loads only.
- M19 The CLI's read could not be canceled with Ctrl+C and showed no progress (senior-dev). Fixed now, as other opens.
- M20 Sample order and parent-before-child were assumed, not checked (senior-dev, gc-specialist, solution-architect).
  Fixed now: the reader sorts samples by time when EventPipe's per-thread buffers deliver them out of order; the model's
  constructor checks both rules; a test refuses each.
- M21 "All types" left the row selected, so the same type could not be picked again (senior-dev). Fixed now; the empty
  text names the type.
- M22 Races, recursion, empty intervals and the row limit were untested (senior-dev). Fixed in part now: re-read
  concurrency, a changed file, closing mid-load, a zero-width interval, recursion, an empty interval and the row limit
  have tests. Stale answers arriving out of order remain covered by the shared grid tests only.
- M23 The reader grew `List<T>` by doubling (gc-specialist). Fixed now: arrays sized from TraceLog's event counts.
- M24 Every tree request allocated seven node-sized arrays (gc-specialist). Fixed now: rented from `ArrayPool`; sums
  without LINQ.
- M25 The rundown path was untested (gc-specialist). Fixed with H1's tests.

## Confirmations

To be recorded after the reviewers re-read the fixes.

## Low / Trivial (listed only)

- L1 `AllocationTreeKeys` had only `Root` and `More`; added `Node` and `BackTrace` (solution-architect); the mirror
  test still checks literals.
- L2 The model said a sample without a stack is -1; corrected, and -1 is refused (solution-architect, senior-dev,
  gc-specialist).
- L3 The descriptor does not say which sampling source made the trace (solution-architect).
- L4 `TimeInterval` accepted zero width; a click is now the whole trace (solution-architect).
- L5 Two quick Opens of one file make two tabs; the page keeps its own list instead of `TracesChanged`
  (solution-architect, senior-dev).
- L6 Stacks answers are capped at 1,000 but not paged (solution-architect).
- L7 `Samples` is `int` while other counts are `long`; a non-trace file reports `SnapshotFormat` (solution-architect).
- L8 The lost-events comments called it an estimate; corrected to an upper bound (gc-specialist).
- L9 `AllocationTypesSource` held the whole trace; it holds the type names now; closing collects (gc-specialist).
- L10 Per-list LINQ and growing back-trace keys; record packing (gc-specialist).
- L11 The sampled-bytes note did not say ticks are per heap or how edges fall; added. The CLI prints exact byte counts
  for estimates (gc-specialist).
- L12 `CheckSignature` mapped only a missing file; folders and sharing violations now have plain messages (senior-dev).
- L13 Limits duplicated between Analysis and Contracts; `Act` helpers copied (senior-dev).
- L14 `!` without comments in `AllocationSession`; the descriptor is set in the constructor now and `latest!` has its
  comment (senior-dev).
- L15 The `LoadTask` chain grew without bound; fixed with M18 (senior-dev).
- L16 Path comparison ignores case on every system (senior-dev).
- L17 The smoke step does not check that the expand grew the rows and selects no interval or type (senior-dev).
- L18 `CloseTraceAsync` bypassed `GuardAsync`; it is logged now (senior-dev).
- L19 "Whole trace" logged two action lines; one now (senior-dev).
- L20 No keyboard way to choose an interval (senior-dev; Backlog with 2.7).
- L21 The recording's CTS was not disposed and `LastCapturePath` held traces; fixed (senior-dev).
- L22 Action lines did not name the trace's path or worker id; they do now (support).
- L23 The logged interval used the current culture; invariant now (support).
- L24 Samples without a stack were stated nowhere; the notes say so now (support).
- L25 The CLI logs `--view` and `--format` by length (support).
- L26 The grids' generic failure text says "reopen the snapshot"; `TraceNotLoaded` shows a GUID (support).
- L27 The client does not log which answers it applied or discarded (support).
- L28 The open line lacked the start time and the process name's source; added (support).
