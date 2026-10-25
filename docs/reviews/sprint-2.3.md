# Review: Sprint 2.3 Dominator tree and retained size

Reviewers: solution-architect, senior-dev, gc-specialist (in parallel, on the sprint's uncommitted change set). Phase 2 runs
autonomously, one sprint at a time (the product owner said "continue" for 2.3 on 2026-10-25), so Moderate findings were
decided by the lead engineer, each with its recommendation, and are marked "decided autonomously".

Totals after dedupe: 2 High, 14 Moderate, 21 Low. A finding raised by more than one reviewer is merged and keeps the
highest severity.

The reviewers confirmed:
- the algorithm, checked by hand against Lengauer-Tarjan and Georgiadis' semi-NCA (gc-specialist, senior-dev): the
  stackless numbering, `Eval`'s compression order, sources with semidominator 0, the NCA climb over final dominators,
  the reverse preorder sum in 64 bits with large objects through `GetSize`;
- that the group lower bound is a true lower bound, since the counted subtrees do not overlap;
- that the CLR semantics follow 6.1 and ADR-024: weak and dead ref-counted handles, dependent handles as edges, frozen
  objects as sources, the f-reachable queue;
- layering, ADR-016 (primitive DTOs), the optional request members at the end, and the protocol bump (solution-architect);
- disposal, WPF threading and MVVM, logging without snapshot contents, no blocking waits (senior-dev).

## High / Urgent

- **H1 The dominators benchmark deleted the whole `--out` folder** (senior-dev). `perf/HeapScope.Benchmarks/DominatorsBenchmark.cs`
  ran `Directory.Delete(output, recursive: true)` on a folder the user named, so `--out artifacts/dumps` would have deleted
  every dump in it. **Fixed:** only the two sidecars it wrote and their temporary files are deleted, and the folder only
  when the run created it; DEVELOPER.md 6 says so. Confirmation below.
- **H2 A null retained cell crashed the CLI's JSON output** (solution-architect). Protocol 9 sends the first null cell in a
  numeric column (an object no root reaches has no retained bytes), and `JsonRows` parsed every numeric cell, so
  `instances --retained --format json` threw on any list with garbage in it. **Fixed:** a null cell is JSON null in any
  column; ARCHITECTURE 4.6 says any cell may be null; `RetainedSizesTests` runs the JSON on a late garbage type and
  expects null. Confirmation below.

### Confirmations

- H1: CONFIRMED by senior-dev (only the run's own names are deleted, the folder only when the run made it and never
  recursively, the sidecars are disposed before the cleanup). Its two nits were fixed too: the temporary folder is
  created only after the cache opens, and the cleanup also tolerates access errors.
- H2: CONFIRMED by solution-architect (a null cell of any kind is JSON null before parsing; CSV, text and the UI already
  handled it; 4.6 states the contract; the test exercises the failing path).

## Moderate (decided autonomously)

- **M1 The projected peak was not fully checked, and the fallback allocated unchecked** (solution-architect, senior-dev,
  gc-specialist). The mode was chosen before the reverse index existed; the reverse index was built before the check; an
  in-memory sidecar was read back while the working arrays were uncollected; a failed sidecar write recomputed into
  managed arrays without a check, even when another process had just written the sidecar. **Fix now:** the dominators
  moved into `DominatorIndexBuilder`: the working arrays are checked before the reverse index is built for them, reserved
  while computing, collected, and only then is the sidecar opened in the mode chosen with the memory committed at that
  moment; a failed write first tries a sidecar another process wrote, and the managed fallback is reserved with its
  outputs counted.
- **M2 Concurrent derived computations were checked against one reading** (solution-architect). **Fix now:**
  `MemoryReservations`, one per worker: the root path tree, the dominators and the retained sizes per group reserve their
  bytes before allocating, and the check counts what is reserved; ARCHITECTURE 8 no longer claims index builds are
  serialized per snapshot.
- **M3 No collection after group retention, or after a failed or canceled computation** (solution-architect,
  gc-specialist). **Fix now:** every large computation collects (non-compacting) in `finally`.
- **M4 The working arrays could live in the outputs** (gc-specialist). The semidominators and ancestors (8 bytes per
  object) were separate arrays while the retained sizes output sat unused. **Fix now:** they live in the retained sizes
  output, indexed by preorder number minus one (the virtual root is never evaluated, so 2 ints per reachable object fit
  in 8 bytes per object); working arrays 13 bytes per object instead of 21. DenseHeap in memory now peaks at 3,875 MB,
  within the 4 GB budget (4,285 MB before).
- **M5 The compression path buffer could reach the reachable count, pooled and kept after the job, and was untested**
  (gc-specialist). A circular list (an LRU `LinkedList<T>`) makes one path as long as the list. **Fix now:** no buffer: the
  climb reverses each ancestor pointer and the walk down compresses from the top; tests: a circular doubly linked list of
  100,000 nodes, and random graphs of 1,500 nodes with long chains against the brute-force definition.
- **M6 Incomplete snapshots and a gcdump's unrooted survivors were not noted** (solution-architect, gc-specialist;
  senior-dev as Low). Missing edges, or survivors whose root the gcdump did not record, can overstate retained sizes.
  **Fix now:** `DominatorIndexInfo.CompleteGraph` and `Note` (an incomplete snapshot; the count of a gcdump's unrooted
  survivors), shown beside retained sizes in the UI and the CLI. Treating those survivors as sources: **deferred** to the
  Backlog (it changes ADR-024's sources).
- **M7 Requests were checked only after the dominators were computed** (solution-architect). **Fix now:**
  `InstancesSource.Validate` and `TypeSummarySource.Validate` refuse a bad sort column, filter or grouping, and skip the
  dominators for a type with no objects, before computing; test `Requests_are_checked_before_any_dominators_are_computed`.
- **M8 A failing retained query hid the grids' rows** (senior-dev). **Fix now:** the types and instances grids re-run
  without retained sizes, turn the switch off, and say why in the note; test
  `A_failing_retained_query_still_shows_the_rows_and_says_why`.
- **M9 No worker tests of the dominators slot** (senior-dev). **Fix now:** `DominatorIndexBuilder` is tested directly:
  no index cache, computed into the sidecar and reopened in both modes, a sidecar from other rules not used and
  rebuilt, an unwritable sidecar falling back to memory with no temporary file left, a refusal before anything is
  computed, reservations counting against each other.
- **M10 The mapped writer's crash safety was untested** (senior-dev, solution-architect). **Fix now:** a failure before the
  header (the temporary file has no magic) keeps the previous file and leaves no temporary file; replacing an open file
  keeps it. The temporary file is now shared for reading, like the streamed writer's.
- **M11 The refusal always suggested opening mapped** (senior-dev; solution-architect, gc-specialist as Low). **Fix now:**
  the next step depends on the snapshot: reopen mapped only for an in-memory snapshot with an index cache; otherwise
  close other snapshots or applications. Tests cover each text.
- **M12 The independent oracle ignored frozen objects and checked one item per fixture** (gc-specialist). **Fix now:** the
  ClrMD walk also starts from every object of a frozen segment; the first, middle and last item of each rooted fixture
  are compared; the frozen literal `RuntimeFixtures.s_literal` is alive, dominated only by the roots, and retains what the
  oracle says.
- **M13 Canceling the last waiter does not stop the computation** (senior-dev). The dominators run on the snapshot's
  lifetime, like every derived slot since 2.1. **Deferred** to the Backlog: reference-counting waiters is a change to all
  slots; the memory reservation and the up-front refusal bound the cost meanwhile.
- **M14 Not measured on a 16 GB machine; the DenseHeap miss was reported as met** (solution-architect). **Deferred** to the
  Backlog: no 16 GB machine or constrained run is available in this sprint; after M4 DenseHeap is within the budget in
  both modes on the 32 GB machine, and the section 10 row now says what was and was not measured.

## Low / Trivial (listed; fixed where noted)

- L1 Misplaced doc comments: `SnapshotSession.QueryTypePathsAsync`, `IndexFileLayout` (senior-dev, solution-architect).
  Fixed, as docs must describe what was built. `TypeGroups`' `ModuleKey` and `MatchTypes` comments predate the sprint.
- L2 Full passes without cancellation checks, and checks every 1M items against the 64K convention (senior-dev,
  solution-architect, gc-specialist). Fixed with the M4 and M5 changes: every loop checks every 64K items.
- L3 Every sidecar open counts the reachable objects, paging in the whole dominators column (senior-dev, gc-specialist).
- L4 Group retention reported no progress and logged no time (senior-dev, gc-specialist). Fixed with M1 to M3.
- L5 The CLI's `--sort` help did not name `retained` (senior-dev). Fixed.
- L6 `RetainedResult.CompleteGraph` was never read (senior-dev). Covered by M6.
- L7 The grids' retained progress could overwrite the final status; finished computations replay their last progress
  (senior-dev). The first is fixed with M8 (a finished flag); the replay is listed.
- L8 Stale texts: working bytes 25 and 21 against the code, DerivedData's summary, `TypeSummaryResult`'s doc (senior-dev,
  solution-architect, gc-specialist). Fixed.
- L9 `Dominators.FromColumns` checks lengths, not contents (senior-dev).
- L10 Test gaps: an empty graph, an unrooted graph (added), group -1, group retention cancellation, the "only the roots"
  text, the smoke run's types switch; a test comparing a record to its own copy; a synchronous helper wrapped in a task
  (removed) (senior-dev).
- L11 Only parent pointers are kept; 2.4 needs children (solution-architect). The 2.4 section names the dominators sidecar
  as its starting point; where children live is 2.4's decision.
- L12 ADR-015 and ADR-006 did not describe the 2.3 memory policy (solution-architect). Fixed: both amended.
- L13 Mapped writes are not flushed with FlushViewOfFile before the final flush, and a sidecar beside a dump on a remote
  or removable volume is written through a mapping (solution-architect). The open-time checksums limit the first to a
  rebuild.
- L14 The dominators layout's semantics version defaulted to 0 (solution-architect). Fixed: `IndexFileLayout.ForDominators`.
- L15 ADR-024 said both paths and retained sizes are checked against ClrMD (solution-architect). Fixed.
- L16 The benchmark's in-memory run is the worker's path without an index cache, not with one (gc-specialist). Fixed in
  6.4's wording.
- L17 Hot loops use the `Column<T>` indexer (`PathRoots.Sources`, the instances retained sort) (gc-specialist).
- L18 Retained sort keys tie above 8 GiB (gc-specialist).
- L19 The CLI object JSON did not carry the incomplete note (solution-architect). Covered by M6 (`Index.Note`).
- L20 `TryOpen` after `Commit` hashes the sidecar a second time (gc-specialist).
- L21 The CLI's object retained text has no line for an incomplete graph beyond the note (senior-dev). Covered by M6.

## Test runs and measurements

- Gate before the review: pass, 886 tests, zero warnings.
- Smoke run (`--smoke-open` Small): pass; the dominators computed in 1.45 s; the drill shows an object's retained size
  and the instances' retained column.
- Measurements before and after the review: ARCHITECTURE 6.4 and 10.
