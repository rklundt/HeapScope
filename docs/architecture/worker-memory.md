# HeapScope architecture: Worker memory and GC settings

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 9" still name the right section.

## 9. Worker memory and GC settings
- Server GC, non-concurrent (`ServerGarbageCollection=true`, `ConcurrentGarbageCollection=false`):
  throughput matters more than pause times in the worker, and background GC adds memory overhead. DATAS
  (dynamic adaptation, on by default for Server GC since .NET 9) is off in the worker: the GC turns it off when a heap
  count is set, which the client always does (next item). The worker logs the GC's own settings at start
  (`GC heaps 12, GC dynamic adaptation 0` on a 24-core machine). The
  fixed count is kept, since it is what keeps the UI responsive during blocking collections (measured in sprint 1.3);
  an idle worker still returns its memory through the compacting collection after each large job (below).
- GC heaps capped at half the logical processors (sprint 1.3): the client starts the worker with
  `DOTNET_GCHeapCount` (`Worker:GcHeapCount` overrides it; a value already in the environment wins). With one heap per
  core, the worker's blocking collections occupied every core and starved the UI thread: on the Medium smoke open
  (24 logical cores) input lag of 550 to 760 ms in most runs, with the UI thread using only 16 to 94 ms of CPU in that
  time; with 12 heaps 8 of 8 runs passed (lag 86 to 193 ms) at the same load time (0.8 s from the index cache).
- After a job that dropped large transient state (completed, failed or canceled), the worker runs one blocking,
  compacting, aggressive gen2 collection before replying: an idle worker allocates nothing, so otherwise
  gigabytes would stay committed.
- Work whose size the client chooses is checked against the memory budget before it starts (the synthetic job
  projects its peak and fails with `InsufficientMemory`), and large jobs of the same kind are serialized.
- Large arrays are allocated once at their final size. No `List<T>` doubling for per-object data; chunked
  buffers during building, exact arrays after.
- Hot loops avoid LINQ, closures, boxing and per-item allocations. `ArrayPool<T>` for temporary buffers.
- Object sets (sprint 3.1, ADR-030) are built inside a reservation and cached within 256 MB of sets and 128 MB of
  detector tables, outside the reservations' total; letting the reservations evict that cache before refusing is in the
  Backlog. Similar Retention (sprint 3.2) reserves `SimilarRetention.WorkingBytes`: 4 bytes per member, 200 per possible
  pattern (at most 1,000,000) and the path signature memo's cap (8 million objects at 48 bytes, the resize peak),
  then compacts; a retention group set reserves the same plus a set's working bytes. Matching two snapshots' objects
  (sprint 3.3) reserves a bit per object of each. Since 3.8 each snapshot keeps its reachable objects (for the leak detectors and the instances scope)
  and its path-root reachable objects (the unreachable set) as a bit per object each, and the dominator tree's group
  tables per grouping; they are kept unreserved, like the other derived slots, and dropped when the snapshot closes. The
  walk that computes a reachable set reserves `Reachability.WalkBytesPerObject` (4.125 bytes per object: the bits and
  a stack that marks on push).
