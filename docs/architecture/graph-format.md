# HeapScope architecture: Compact graph format

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 6" still name the right section.

## 6. Compact graph format (ADR-005)

The object graph is a struct-of-arrays, CSR-style structure indexed by `ObjectIndex` (a 32-bit integer,
0..N-1). There is never one managed object per heap object.

### 6.1 Data structures

Every array below is a `Column<T>` (a read-only, span-producing column of unmanaged values), not a raw
`T[]`. A column is backed either by a managed array or by a read-only memory-mapped view of an index cache
section (ADR-015). Algorithms only ever see `ReadOnlySpan<T>`, so they do not care which backing is used.

| Array | Element | Bytes per object | Notes |
|---|---|---|---|
| `Addresses` | `ulong` | 8 | Sorted ascending, so address -> index is a binary search. |
| `TypeIndices` | `int` | 4 | Index into the type table. |
| `Sizes` | `uint` | 4 | Shallow size. `uint.MaxValue` means "look up in `LargeSizes`" (objects of 4 GiB or more). |
| `HeapKinds` | `byte` | 1 | Gen0, Gen1, Gen2, LOH, POH, Frozen, Unknown. |
| `Flags` | `byte` | 1 | `ObjectFlags`: `HasFinalizer` (registered for finalization and not suppressed); other bits reserved. |
| `EdgeStarts` | `int` | 4 (+4 once) | CSR row offsets: outgoing references of object `i` are `Edges[EdgeStarts[i]..EdgeStarts[i+1]]`. |
| `Edges` | `int` | 4 per reference | Target `ObjectIndex`. References to non-objects are dropped and counted in `DroppedReferenceCount`. |

Supporting tables:
- **Type table**: struct-of-arrays per type: name (string table id), method table address, base size,
  component size, module (string table id), `TypeKind` flags (Array, String, ValueTypeBox, Delegate,
  Exception). Types are interned: one entry per method table.
- **Root table**: array of `GcRoot { int ObjectIndex; RootKind Kind; RootFlags Flags; int DetailId }`,
  sorted by object index. `RootKind`: StaticField, ThreadStack, StrongHandle, PinnedHandle, AsyncPinnedHandle,
  RefCountedHandle, FinalizerQueue, WeakHandle, Other. `DetailId` points into the string table (e.g.
  `LeakyApi.Leaks.StaticCacheLeak.s_cache`, `thread 0x1a2c`). `RootFlags`: `Retaining` (the root keeps its
  object alive), `Pinned`, `Interior`.
- **String table**: interned strings for names and details. Never contains object field values.

#### Root and reference semantics (CLR rules every loader and algorithm follows)
- **Retaining roots.** Only roots with `RootFlags.Retaining` keep objects alive in retention paths, dominators
  and retained sizes. Weak handles are never retaining. Ref-counted handles are retaining only while their count
  is above zero (ClrMD `IsStrong`). Roots shown for information only keep the flag clear.
- **Dependent handles are edges, not roots.** A dependent handle (used by `ConditionalWeakTable`) keeps its
  secondary alive only while its primary is reachable. Loaders add an ordinary edge primary -> secondary in the
  CSR during the reference pass and never record the secondary as a root. Exactly one mechanism is used: either
  ClrMD's reference enumeration including dependent handles, or explicit edges from the handle table, not both.
  Exception (sprint 1.8 review M11): a gcdump records a dependent handle's value without its key, and only when the key
  was alive; every object in a gcdump survived its collection, so the gcdump loader records such a value as a retaining
  `Other` root ("dependent handle value (key not recorded)") rather than letting it look like garbage.
  Since sprint 2.2 the graph also keeps the pairs it merged (`HeapGraph.DependentHandles`, sorted, persisted in the
  index cache), so referrers and retention paths can say "(dependent handle)" for such an edge when no field of the
  primary holds the secondary.
- **Static fields.** On modern .NET the GC sees no static field roots: reference statics live in runtime
  allocated `object[]` storage. Measured on .NET 10 with the sprint 0.4 fixtures, that storage is reached through a
  **strong handle** (`StrongHandle -> System.Object[] -> static value`), not a pinned handle. The full dump loader
  builds `StaticField` roots itself by enumerating static fields per module and app domain (ClrMD
  `ClrStaticField`) and records the field name as the root detail. The strong handle roots of the static storage
  arrays stay retaining (marking them non-retaining would make the storage arrays look unreachable); retention
  paths prefer a `StaticField` root over the storage handle, so they say `s_cache` and not
  `StrongHandle -> Object[]`. Statics of collectible types (unloadable `AssemblyLoadContext`) live as long as their
  loader allocator, so their `StaticField` roots are recorded without `Retaining`; the runtime's own references keep
  them alive while the context is alive (a collectible fixture is a backlog item).
- **Frozen objects.** Objects in frozen (NonGC) segments, such as string literals and `RuntimeType` objects, are never
  collected. They have no root; reachability, dominator and garbage analyses (2.x) treat every `HeapKind.Frozen`
  object as alive (a virtual root), so a literal referenced by nothing live is not reported as garbage.
- **Finalizer queue.** Only the f-reachable queue (objects ready for finalization) produces `FinalizerQueue`
  roots. Objects merely registered for finalization are not roots; that fact is kept in the object `Flags`
  (`HasFinalizer`). `GC.SuppressFinalize` does not remove an object from the finalization queue (measured in 0.4:
  every finalizable instance stays registered); it sets bit 0x40000000 in the object header word. `HasFinalizer`
  is therefore "registered and that bit clear", read from the header by the loader.
- **Interior pointers.** Stack roots may point inside an object (byrefs). Roots flagged `Interior` are resolved
  with a floor search: the object with the largest address not above the pointer, accepted only when the pointer
  is below that object's address plus size. All other roots and references use an exact match. The loader passes
  the raw pointer read from the stack slot, not ClrMD's resolved object, so this bounds check decides and a byref
  into a free gap, a stack local or native memory is dropped.
- **Stack roots of debuggable code.** In frames compiled as debuggable code (Debug builds), ClrMD reports no stack
  roots for some frames (measured in 0.4: the locals of parked fixture threads were missing in Debug and present,
  including an interior pointer, in Release). Objects held only by such frames can look unreachable. The UI states
  this when a snapshot's process ran Debug assemblies (sprint 1.2), and the fixtures are always optimized.
- **Unresolvable targets.** References and roots that do not resolve to an object (stale handles, corrupt slots,
  pointers outside the heap) are dropped and counted: `DroppedReferenceCount` and `DroppedRootCount` are part of
  the graph and shown as a data quality note.
- **Embedded value types.** References inside value types embedded in objects and arrays (struct fields, arrays
  of structs such as `KeyValuePair<,>[]`) are edges of the containing object. Loaders use GCDesc based reference
  enumeration, which covers them.

### 6.2 Memory budget per object
- Fixed: 8 + 4 + 4 + 1 + 1 + 4 = **22 bytes per object** plus **4 bytes per reference**.
- Typical heaps average 2 to 4 references per object, so about **30 to 38 bytes per object**.
- 50M objects with 150M references: 1.1 GB + 0.6 GB = **about 1.7 GB**, well under the worker budget for a
  ~4 GB managed heap. On a 16 GB laptop the absolute first-load budget in section 10 (< 4 GB) is the one that binds.
- Measured in sprint 0.2 (`graph-build`, 50M objects, 150M references, Release, 24 logical cores, 32 GB RAM):
  1,621 MB of columns, **34.0 bytes per object**; managed heap growth identical (no hidden per-object overhead).
  Peak working set 2.5 to 2.7 GB for the build alone (varies by run). Build time: 1.9 s with address-ordered
  input and references by index; 3.8 s with unsorted input (in-place permutation, no LOH temporaries); **46 s
  with unsorted input and references resolved by address**, the loader path, with uniformly random targets as
  the worst case for cache locality (217 s with a plain binary search, before the address index below). The
  unsorted, by-address number is the binding one for first-load budgets (section 10); real heaps have much more
  locality.

### 6.3 Building
`HeapGraphBuilder` is two-phase so peak memory stays near the final size:
1. Objects are appended (address, type, size, heap kind) into chunked buffers (1M-element chunks, so growth
   never copies a huge array), then sorted by address once and frozen.
2. References are appended per source object in strictly increasing index order as address spans, walking
   `FrozenAddresses`; each target address is resolved through an `AddressIndex`, a bucket table over the address
   range (about 8 objects per bucket, at most 2^26 buckets, about 25 MB for 50M objects) followed by a binary
   search within the bucket. Dependent-handle edges enter only through `AddDependentHandle`, never through
   `AddReferences`. Any exception thrown while state changes faults the builder, and a builder that never
   reaches `Build` is disposed to release allocator memory. The builder throws if a source index is not greater than the previous
   one, because an out-of-order row would silently corrupt `EdgeStarts`. Chunked edge buffers are copied once
   into the final `Edges` array.
Full dump loaders enumerate the heap twice (objects, then references) for exactly this reason. Because heap
enumeration order is not address order with regions (.NET 7+), the reference pass walks the sorted `Addresses`
column and reads each object by address (`heap.GetObject(address)`), not `EnumerateObjects`.

Final columns are obtained from an `IColumnAllocator` given to the builder. Sprint 0.2 provides the managed
array allocator; a file-backed allocator that writes columns straight into index cache sections can be added
later (first load on memory constrained machines, Large tier) without changing the builder. Sprint 2.3 built that kind of
writer for the optional indices (`MappedIndexFileWriter`, section 7); the base graph still builds in managed arrays.

Full dump loader (sprint 1.2, `HeapScope.Loaders.ClrMd`: `ClrMdDumpLoader`, `DacLocator`, `LocalOnlyFileLocator`):
- ClrMD reads the dump through its lock-free memory-mapped reader (`DataTargetOptions.UseLockFreeMemoryMapReader`):
  dump pages are file-backed, not private memory, and the walk is about twice as fast as the default cached reader
  (Medium: 15.8 s and 2.4 GB peak, against 36.6 s and 4.2 GB). ClrMD's data cache stays capped at 512 MB.
- Object pass per segment (`ClrSegment.EnumerateObjects`): the heap kind comes from the segment kind, or from
  `GetGeneration` on ephemeral and region segments; types are interned by method table on first sight;
  `HasFinalizer` is `EnumerateFinalizableObjects` minus `ClrType.IsFinalizeSuppressed`.
- After `FreezeObjects` the loader runs one aggressive, compacting collection: the phase-one chunks (about 20 bytes
  per object) are garbage then, and otherwise stay committed through the reference pass, which is where a load peaks.
- Dependent handles become edges before the reference pass; weak handles become non-retaining `WeakHandle` roots.
- Reference pass in address order (`GetObject` per frozen address, `EnumerateReferenceAddresses` without dependent
  handles), into a pooled buffer.
- Roots: `ClrHeap.EnumerateRoots` (strong, pinned, async pinned and live ref-counted handles, stacks with the thread
  as detail, the f-reachable queue). On .NET 9 and 10 it also reports the static storage bucket as `StaticVar` and
  thread statics as `ThreadStaticVar`: the bucket maps to `StrongHandle` with the detail "static storage" and is kept
  once; thread statics map to `Other`. `StaticField` roots come from `ClrModule.EnumerateTypesWithStaticFields` per
  module and app domain (initialized reference statics, detail `Namespace.Type.field`). Statics of generic
  instantiations missing from a module's type map are not reported; their values stay reachable through the storage
  handle.
- Modules compiled as debuggable code are recorded (cache metadata `DebuggableModules`) and shown as a note.
- The snapshot's capture time is the minidump header's time stamp.

Code map (sprint 0.2, namespace `HeapScope.Core.Graph`): `HeapGraphBuilder` (`AddObject`, `FreezeObjects`,
`AddDependentHandle`, `AddReferences` / `AddReferenceIndices`, `AddRoot` / `AddRootIndex`, `Build`),
`HeapGraph` (columns, `GetSize`, `GetReferences`, `TryGetIndex`, `TryGetIndexContaining`, `TotalSize`,
`ColumnBytes`, `FromColumns` for the index cache), `TypeTable`, `RootTable`, `StringTable`, `Column<T>`,
`IColumnAllocator`. `HeapScope.Core.Synthetic` holds `SyntheticGraphBuilder` (hand-made graphs) and
`SyntheticGraphs` (chain, cycle, tree, diamond, seeded random up to 50M objects). `GraphSnapshot` is the
`ISnapshot` for graphs without optional services.

### 6.4 Extending without redesign (2.x)
- **Reverse references** (built in sprint 2.1): a second CSR (`InEdgeStarts`, `InEdges`) built by counting in-degrees
  and filling, +4 bytes per object and +4 per reference, one entry per reference (a referrer holding an object in two
  fields appears twice; rows come out sorted by referrer). The worker builds it on the first referrers query of a
  snapshot, not at open, and keeps it until the snapshot closes, attached to the graph so it is released after the last
  lease. It is written as the sidecar `<snapshot>.rev.hsidx` when the snapshot has an index cache, and opened from that
  sidecar on later opens; without an index cache it stays in memory and is rebuilt on the next open. Its memory mode
  (`ReverseIndexBuilder.ChooseMode`, review M3): a forced open mode wins; next to a mapped graph it is mapped; next to an
  in-memory graph it stays in memory only when it fits both the memory available on the machine and what is left of
  the worker's share of physical memory after what the worker already commits. In 2.1 the index was built in managed
  arrays, written, and dropped once the sidecar was mapped (review M2). Since sprint 2.3 (from the 2.1 review M2) it is
  built straight into its sidecar (`ReverseReferences.Fill` into the sections of a `MappedIndexFileWriter`) and then
  opened in its mode, so a mapped index never holds a managed copy (peak private bytes of the build: 29 to 31 MB on
  Medium and DenseHeap, against 470 and 831 MB before); without an index cache, or when the sidecar cannot be written,
  it is built in managed arrays and stays in memory. Every caller waiting for the shared build gets its progress (review M7),
  and the UI shows it. Measured on real index caches (`reverse-index`, Release, 32 GB machine, times vary by about 2x
  between runs): Medium (57.5M objects, 65.7M references) builds in 0.6 to 0.8 s, 470 MB, sidecar written in 0.6 to
  0.7 s, reopened in 0.1 to 0.2 s mapped (+1 to 6 MB private) or 0.2 to 0.4 s in memory (+472 MB); peak private bytes
  2,423 MB with the graph in memory, 970 MB mapped. DenseHeap (54.1M objects, 163.9M references) builds in 0.8 to 1.7 s,
  831 MB, written in 0.7 to 1.9 s, reopened in 0.2 to 0.4 s mapped or 0.4 to 1.1 s in memory; peak 3,454 MB with the
  graph in memory, 1,695 MB mapped. These peaks are the benchmark process's (it collects between phases and holds no
  type aggregation or dump reader). In the Release worker on DenseHeap (CLI `referrers --address`), the build took
  1.45 s and the worker's peak commit after it was 2,644 MB; naming referrer fields then opens the dump reader, whose
  ClrMD cache is capped at 512 MB. Through the Debug worker on Medium (`referrers --type`): 2.6 s for the first query
  (build and sidecar write), 0.18 s on the next run from the sidecar. Not measured on a 16 GB machine.
- **Dominators** (built in sprint 2.3, `Analysis/Retention`, ADR-024): the immediate dominator (4 bytes), the retained
  size (8 bytes) and, since sprint 2.4 (ADR-027), the dominator tree's children (a CSR: 4 bytes of start and 4 of child per
  object, each node's children in object index order), 20 bytes kept per object, computed with semi-NCA (chosen in 1.9, measurements below) over the CSR and the reverse
  index from a virtual root joined to exactly `PathRoots.Sources`, so paths and dominators agree on what is alive by
  itself. An object no root reaches has `DominatorTree.Unreachable` and retains 0; an object only the roots dominate has
  `DominatorTree.VirtualRoot`.
  - `DominatorTree.Compute` fills spans the caller gives. Every step is iterative and stackless: the depth-first
    numbering climbs back through the parents and keeps each vertex's next edge in an array used later for labels, and
    the path compression reverses ancestor pointers on the way up instead of keeping the path (review M5: a circular
    list makes one path as long as the list). The preorder numbers live in the immediate dominators output, and the
    semidominators and ancestors (by preorder number minus one) in the retained sizes output, until each is needed
    (review M4). Managed working arrays: 13 bytes per object (vertex, parent, label, a bit per root). Retained sizes are
    summed in reverse preorder.
  - The worker computes them on first use (decision 5): a retained column, an object's retained size
    (`DominatorIndexBuilder`). A request is checked first, so a bad sort column, filter or type costs nothing (review
    M7), and the working arrays are checked against the budget before the reverse index is built for them (review M1).
    With an index cache they are computed straight into the sidecar `<snapshot>.dom.hsidx` (decision 3); the working
    arrays are then collected and the sidecar opened in the mode `ReverseIndexBuilder.ChooseMode` picks with what the
    worker commits at that moment. When the sidecar cannot be written, one another process just wrote (the UI and the
    CLI on one dump) is used; otherwise, and without an index cache, they are computed in managed arrays, checked again
    with the outputs counted. Every large computation of the worker (the root path tree, the dominators, the retained
    sizes per group) reserves its memory in one `MemoryReservations` before allocating (review M2), so two first uses at
    once cannot both pass against one reading; a refusal is InsufficientMemory with a next step that fits the snapshot
    (reopen mapped only for an in-memory snapshot with an index cache, review M11). Each collects its working arrays
    however it ends (review M3). A live in-memory graph is not switched to mapped (decision 4, ADR-015; Backlog). The
    sidecar records `DominatorTree.SemanticsVersion` (`PathRoots.SemanticsVersion` and the algorithm's own), so one
    computed under older rules is rebuilt.
  - Caveats shown beside retained sizes (`DominatorIndexInfo.Note`, review M6): an incomplete snapshot misses
    references, and a gcdump's survivors whose root it did not record retain nothing here; both can overstate the
    retained sizes of objects that share them. For a gcdump the note gives their count and bytes (`DominatorIndex.
    UnreachableBytes`). Treating those survivors as sources is a Backlog item, to be done with gcdumps at scale.
  - A group's retained size (`GroupRetention`, decision 6) is a lower bound: the retained sizes of its objects that no
    other object of the group dominates, found in one stackless walk of the dominator tree (4 bytes per object while it
    runs, a cursor per vertex; it built the tree's children itself, 12 bytes per object, until 2.4 kept them) counting
    each group's objects on the current path; computed once per grouping and kept with the snapshot. Objects held only
    by several of a group's objects together are not counted; the exact retention of a set counts them (below).
  - The retention explorer (sprint 2.4, ADR-027). The tree's children are kept in object index order (a counting
    pass); the rows a query shows are put in retained order there (`RetainedOrder.Top`: the largest k of a node's
    children with a heap of k, ties by index, so the order never varies). `DominatorTreeRows` flattens the tree for a
    paged grid: the virtual root's children (the biggest retainers) or a focus object and its children, each expanded
    row's children below it, as objects or grouped by type (with its module) or namespace. Siblings retain disjoint sets,
    so a group's retained bytes are the exact sum of its members'. A list shows 100 children and a "more" row that sums
    the rest; expanding the "more" row shows the next 100. Grouped lists sum only the groups present, an open group's
    largest members are picked from the list without copying it, and each grouping's type tables are built once per
    snapshot. Row keys survive re-runs and a worker restart: an object's is its address (`o:<hex>`), a group's and a "more"
    row's extend their list's key; Contracts owns the formats, the kinds and the limits (`DominatorTreeKeys`,
    `DominatorRowKinds`, `DominatorTreeLimits`, mirror-tested, ADR-016). An answer holds at most 50,000 rows and says when
    it was cut; at most 10,000 keys are open per request; levels below 1,000 are not followed (no recursion can exhaust
    the stack); the focus's trail keeps its top 3 and nearest 20 dominators and says how many there are. The tree is
    re-queried on every expansion: the worker keeps nothing per view, the grid pages it like any result set (8, the 2.3.6
    review), the keys carry the state, and the request names the row to keep the user's place on (`RevealKey`), whose row
    the answer gives (`RevealIndex`). `IcicleLayout` lays out the same tree as an icicle: the focus (or the whole reachable
    heap) as the top bar, each node's children side by side in retained order, as wide as their retained bytes, 6 levels
    by default, leaving out bars under 0.1 percent of the top with their subtrees (at most 20,000 bars). `SetRetention`
    answers "objects retained by this set" for a type: one reachability pass from `PathRoots.Sources` over the forward CSR
    that never enters the set (a bit per object and a stack in 64 KB chunks: at most 5 bytes per object), with its memory
    reserved like every large computation; whatever the dominators reach and the pass does not is retained, the set's
    own reachable members included, counted by type. The worker returns the group's lower bound beside it, so the gap is
    visible, and counts the members no root reaches apart. An unknown type is refused before anything is computed.
  - Measured in the 2.4 review (Release, 32 GB machine, the same day as a run of 2.3.7's code for comparison): Medium
    dominators with the children 6.4 to 6.9 s in memory (the children 0.7 s; 2.3.7's code 8.2 s that day), peak 3,291 MB;
    11.4 s mapped (children 1.1 s), peak 697 MB; 1,097 MB kept, reopened mapped in 0.42 s; retained by type 2.6 to 3.2 s
    (2.3.7's code 5.6 s). DenseHeap 7.3 s in memory (children 0.6 s), peak 3,877 MB (4,289 MB, over the budget, until the
    working arrays were collected before the children are allocated); 10.6 s mapped, peak 658 MB; 1,031 MB kept. Storing
    the children sorted by retained size first took Medium's dominators to 13.4 s and its peak to 3,747 MB. Through the
    `dominators` benchmark's set phase (the type entry with the most objects, and its lower bound): Medium 5.2 s for 16M
    `System.Byte[]`, peak 3,300 MB; DenseHeap 3.0 s, peak 3,876 MB (no higher than the dominators'). Through the
    Release CLI on Medium: set retention of `SessionRecord` (3.2M objects) 3.3 s, a tree query 0.03 s once the dominators
    are open (grouped by type with two levels open too), the first `retainers` run 31 s with the dominators computed and
    saved, the next 2.2 s. In the UI smoke run on the Small dump the tree, the icicle, an expansion and a focus each answer
    in well under 0.1 s once the dominators exist.
  - Measured after the review fixes (`dominators`, Release, 32 GB machine; mapped runs the worker's path with an index
    cache, computing into sidecars; in memory runs its path without one, managed outputs; the graph, the reverse index
    and the dominators are held together): Medium (57.5M objects) reverse index 0.7 s and dominators 3.2 s in memory
    (peak 3,289 MB), 1.3 s and 4.3 s mapped (peak 699 MB; the reverse index build 30 MB); 658 MB kept, reopened mapped in
    0.13 s; retained per type 2.0 to 2.2 s. DenseHeap (54.1M objects, 164M references) 0.8 s and 3.7 s in memory (peak
    3,875 MB), 2.0 s and 5.0 s mapped (peak 661 MB); 619 MB kept; retained per type 1.0 to 1.2 s. Before the review the
    working arrays took 21 bytes per object: peaks 3,702 and 4,285 MB in memory (DenseHeap over the 4 GB budget, as 1.9
    predicted), 1,118 and 1,080 MB mapped. The 1.9 prototype took 7.4 s (Medium) and 10.5 s (DenseHeap) for the
    dominators and retained sizes. In the UI smoke run on the Small dump (5.5M objects) the dominators took 1.45 s.
  - Under memory pressure (after the 2.3 review M14, `dominators --mode mapped --max-working-set-mb N`, a hard cap on the
    process's working set as a stand-in for a 16 GB machine under load): with the working set capped at 2 GB the
    reverse index and dominators take as long as uncapped (Medium 5.9 s, DenseHeap 8.0 s); capped at 1 GB, 7.9 s and
    11.1 s against 5.8 s and 7.8 s uncapped, with 1.7 and 1.9 times the page faults (1.9M and 2.7M). Private bytes stay at
    about 700 MB. No thrashing: the extra faults are pages of the mapped graph and sidecars read again. Trimmed pages
    went to the standby list of a 32 GB machine, so these were soft faults; on a machine really short of memory they
    are disk reads, an estimated 3 GB more reads on Medium at 1 GB, still well inside the 60 s budget on an SSD. A real
    16 GB machine remains a Backlog check.
- **Field names on paths**: not stored. Retention paths resolve the referencing field on demand from the
  dump (full dumps only, capability `FieldValues`).
- **Retention paths** (built in sprint 2.2, `Analysis/Retention`): a path starts at a path root, an object held by a
  retaining root or a frozen object (never collected, 6.1); weak handles never start one. At equal distance a better
  root explains an object first: static fields, then thread stacks, handles, the finalizer queue, other roots, frozen
  objects, so a static's value is explained by its field and not by the runtime's storage handle.
  - Where paths start is defined once (`PathRoots.Sources`, with `PathRoots.SemanticsVersion` for anything kept that
    depends on it; review M2): 2.3's dominators join their virtual root to exactly these objects.
  - One object (`RetentionSearch`): whether a root reaches it comes from the root path tree below, which is exact
    (review H2). A breadth-first search backward over the reverse index (2.1), pruned to objects the tree reaches, finds
    the paths: it stops at path roots, finishes the distance at which it found 10 roots (the closest kept, better roots
    first) and visits at most 1,000,000 objects; when it stops before any root, the tree's own path is shown instead and
    the result says the search stopped early. The paths go on the wire as one tree from the object toward its roots,
    built path by path (review H1): a run of objects of one type is one node with a folded count, a path of more than 64
    segments keeps its ends around one "(N objects not shown)" node, segments merge only when they are the same objects,
    and the tree stops before 2,000 nodes (the result says so). Key path steps are cut to 64 the same way. Fields are named on full dumps for at most 1,000 steps within the referrers' element budget; a step with
    no field over a dependent-handle edge is marked as such. What "no root reaches it" means depends on the snapshot's
    kind (review M3): garbage not yet collected in a full dump; a root the gcdump did not record in a gcdump, which
    holds only survivors. The first object query of a snapshot therefore builds the root path tree as well as the
    reverse index (about 1 to 2 s more on Medium and DenseHeap; review L17).
  - The objects of a type (`RootPathTree`, `KeyPaths`): one breadth-first search forward from all path roots at once,
    queued best root first, gives every object its shortest path from a root (4 bytes per object, kept in memory with
    the snapshot as a derived slot, plus 4 per object while built; the worker refuses with InsufficientMemory when those
    8 bytes per object do not fit what is left of its share, and returns the build queue to the OS afterwards without
    compacting; review M1). A type's objects are grouped by the type sequence of
    their path (repeats of one type folded, so a long list is one step) and the kind of root (and the field, for
    statics): the 20 largest groups are its key retention paths, the rest summed, objects no root reaches counted
    apart. Signatures are interned per path and remembered for the shared ancestors only, at most 8 million of them,
    with one walk stack reused (review M5).
  - Framework steps (types in `System.` or `Microsoft.` namespaces between other steps) are flagged; the UI and the CLI
    fold runs of them unless asked to show them.
  - Measured after the review fixes (`retention`, Release, 32 GB machine; the benchmark holds the graph, the reverse
    index, the type aggregation and the tree together): Medium (57.5M objects) root path tree 0.8 to 1.2 s (219 MB), key
    paths of `System.Byte[]` (16M objects, 108 paths) 1.6 to 1.7 s, of `System.String` (6.6M) 0.8 s, one object's paths
    0 to 6 ms, an unreachable object's 0 ms; peak 2,802 MB with the graph in memory, 1,197 MB mapped. DenseHeap (54M
    objects, 164M references, 50,006 dependent handles) tree 1.6 to 2.3 s, key paths 0.3 to 0.4 s, one object's paths 52
    to 176 ms (about 570,000 objects visited after the one root, since no closer roots exist), an unreachable object's
    1 ms; peak 3,320 MB in memory, 1,416 MB mapped. In the UI smoke run on the Small dump the tree took 0.2 to 0.4 s. Not
    measured on a 16 GB machine.
- Worst case with all 2.x indices for 50M objects: about 74 bytes per object (with 2.2's root path tree and 2.4's dominator
  tree children), about 3.7 GB.
- Measured in sprint 1.9 (`phase2-indices` benchmark, a prototype of the reverse CSR, dominators from a virtual root
  joined to every retaining root and frozen object, and retained sizes; Release, Server GC, 32 GB machine; each phase
  starts after a compacting collection and reports its own peak private bytes). Semi-NCA dominators:

  | Graph | Mode | Reverse CSR | Dominators | Retained sizes | Total | Kept indices | Peak private bytes |
  |---|---|---|---|---|---|---|---|
  | Medium fixture, 57.5M objects, 1.14 references each | in memory | 0.7 s | 3.6 s | 3.8 s | 9.5 s | 1,128 MB (20.6 B/object) | 3,514 MB |
  | Medium fixture | mapped | 0.8 s | 3.5 s | 3.8 s | 9.0 s | 1,128 MB | 2,058 MB |
  | DenseHeap fixture, 54.1M real Roslyn objects, 3.03 references each | in memory | 0.9 s | 8.6 s | 1.9 s | 12.9 s | 1,450 MB (28.1 B/object) | 4,119 MB |
  | DenseHeap fixture | mapped | 1.0 s | 7.5 s | 2.7 s | 12.2 s | 1,450 MB | 2,360 MB |
  | Synthetic 50M, 3 random references each | in memory | 20.1 s | 184.6 s | 49.7 s | 256.9 s | 1,335 MB (28 B/object) | 4,121 MB |
  | A doubly linked list of 1M nodes held from both ends | in memory | 0.0 s | 0.0 s | 0.1 s | 0.1 s | 23 MB | 104 MB |

  Cooper-Harvey-Kennedy (iterative to a fixpoint) gives the same dominators where it converges, but needs about one
  pass per node on a list reachable from both ends (a deque, a circular list): 30,002 passes and 7 s for 30,000 nodes,
  not converged after 1,000 passes on 1M nodes (review H1). Real heaps hold such lists (timer queues, linked lists).

  Decisions (sprint 1.9):
  - Dominators use semi-NCA (Georgiadis, the Lengauer-Tarjan family): one pass over a depth-first preorder with path
    compression, near-linear on every measured shape (its nearest-common-ancestor climb has quadratic theoretical worst
    cases, none seen in heaps or the synthetic shapes). Retained sizes are summed in reverse preorder, reusing the
    dominator pass's vertex order; the depth-first stack and the compression path use pooled or chunked buffers.
  - Computing needs about 28 bytes per object of working arrays (preorder numbering, parents, semidominators, labels,
    ancestors) on top of the graph and the reverse CSR; the kept indices are 8 to 12 bytes per object plus 4 per
    reference. A real dense heap computed in memory peaks just over the 4 GB budget (DenseHeap, 4,119 MB) and well under
    it with the graph mapped (2,360 MB, no slower here). So the indices are computed with the graph mapped whenever the
    graph, the reverse CSR and the working arrays together exceed the budget, the memory mode is reconsidered when the
    indices are computed (not only at open), and the kept indices are index cache sections that follow the memory mode
    (ADR-015). The paging cost of a mapped computation on a 16 GB machine is to be measured when 2.x builds it.
  - Time budget: under 60 seconds for a Medium-scale real heap (9 to 13 s measured), as a background job with progress
    and cancellation, computed once per snapshot and cached. Random references without locality (the synthetic worst
    case) are slow everywhere (257 s) and not representative of heaps.

### 6.5 Memory modes (ADR-015)
- **In-memory**: columns are managed arrays. Fastest. Used while the projected worker footprint fits in the
  memory budget.
- **Mapped**: columns are read-only memory-mapped views of the index cache. The OS pages data in on demand and
  can drop it under pressure, so the worker's committed memory stays small. Used when the projected footprint
  does not fit, or when forced by configuration.
- The memory budget (`MemoryBudget.Current`) defaults to 50% of total physical memory, capped by the memory available
  at that moment (read live from the operating system, with a GC hard limit as an extra cap), at open
  time. A request can force a mode; a configurable fraction is a backlog item. The synthetic job checks against the
  same budget. On a 16 GB laptop running the target API and an IDE, a Medium snapshot will usually open mapped; on
  32 GB it opens in memory.
- First load builds in memory, then writes the cache. If the projected build footprint exceeds the budget,
  the Application layer warns before starting (ADR-004). The projection counts the graph columns, the builder's
  transient buffers and ClrMD's own caches; the ClrMD loader caps ClrMD's data cache explicitly.
- When a snapshot switches to mapped mode after its first load, the worker drops the built arrays and runs one
  blocking, compacting gen2 collection with `GCLargeObjectHeapCompactionMode.CompactOnce`, so the memory really
  returns to the OS. Measured in 1.2 (`load-dump`, Medium): 0.5 s, private bytes down to 59 MB after the drop and
  66 MB with the graph mapped.
- The worker's `SnapshotStore` applies this policy: a valid index cache is opened in the mode the budget chooses;
  otherwise the dump is loaded, the cache written, and the graph either kept in memory or switched to mapped. After a
  load, the mode is chosen once the load's garbage is collected, counting the graph's own memory as available (it
  would be freed by going mapped), so a first load and a later reopen decide the same way. The clean-up collection
  runs only after dump loads (and failed or canceled ones), never after a cache hit, because it pauses queries on
  other snapshots. When the cache cannot be written the graph stays in memory, with a note. A request can force
  either mode.
- The Application layer may start the worker with `DOTNET_GCHeapHardLimit` derived from the budget, so an
  oversize load fails with a clear out-of-memory error instead of paging the laptop to a standstill.

#### Lifetime of mapped data
A span over an unmapped view is an access violation that kills the worker, so mapped data has an explicit
lifetime rule (implemented in sprint 0.5: `LeaseTracker`, `ISnapshot.AcquireLease`). A mapped snapshot owns its views through the graph's owner. Every query takes a lease
(`using var lease = snapshot.AcquireLease()`), works on spans only inside that scope and never stores a span or a
pointer beyond it. Disposing a snapshot (explicitly or by LRU) returns at once; the mapping is released when the last
lease is returned. `Column<T>` wraps `ReadOnlyMemory<T>`; a native backing is a `MemoryManager<T>` over the mapped
view, owned by the snapshot, so the column's shape does not change between 0.2 and 0.5.

### 6.6 Limits
- At most 2^31 - 1 objects and 2^31 - 1 references per snapshot (32-bit indices; a span holds at most 2^31 - 1
  elements). The Medium tier needs at most ~50M objects and a few hundred million references. The Large tier
  (backlog) would move to 64-bit edge offsets and chunked, disk-backed building.
