# HeapScope architecture: Snapshot abstraction and capability model

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 5" still name the right section.

## 5. Snapshot abstraction and capability model

### 5.1 Abstraction (HeapScope.Core)
```csharp
public interface ISnapshot : IDisposable
{
    SnapshotInfo Info { get; }                 // kind, source path, process name/id, runtime version,
                                               // pointer size, capture time
    SnapshotCapabilities Capabilities { get; } // what this snapshot can answer
    HeapGraph Graph { get; }                   // compact object graph, types, roots, strings
    bool TryGetService<TService>(out TService? service) where TService : class; // optional services
}
```
- Loaders produce a `HeapGraph` through `HeapGraphBuilder` and wrap it in an `ISnapshot`.
- Capabilities that need the original file (field values, string contents) are exposed through optional
  services obtained with `snapshot.TryGetService<IObjectReader>(out var reader)`. A gcdump snapshot
  simply does not provide them.
- `GraphSnapshot` (an `ISnapshot` with no services) strips `FieldValues`, `StringContents` and `ThreadStacks`
  from whatever capabilities it is given, so it can never claim what it cannot answer (synthetic graphs, tests).
  Since sprint 1.5 every full dump, first load or index cache, is a `DumpSnapshot`: the graph plus a
  `ClrMdObjectReader` over the dump file, which opens the dump with ClrMD only when something is inspected, serializes
  its reads and is disposed with the graph after the last lease. Before it opens the dump it checks the file's
  fingerprint against the one the snapshot opened with, and fails with `SnapshotChanged` when the dump was replaced
  (review H5). Multi-dimensional arrays are read by indices and named `[i,j]`; a reference's names keep the first
  three paths and a count ("[0], [1], [2] and 7 more"), so an array of one shared object stays linear (review H1, H4).
  It reports `FieldValues` and `StringContents`;
  `ThreadStacks` stays missing until a feature needs it. `CapabilityDescriptions.ExplainMissing` takes the snapshot
  kind, so a full dump says "not available in this version yet" instead of "capture a full dump".
- The reader returns typed values (sprint 1.6, `ObjectValue`): kind, the number, character, boolean or text, the
  address, an array's dimensions and element type, an enum's constants, a box's value. It also reads one value by a
  dotted path through structs and referenced objects (`ReadField`, used by the collection inspections) and many
  strings at once (`ReadStrings`, short cuts only, one lock acquisition per 4,096 strings). Full text of any size is
  streamed in pieces of one pooled 128 KB buffer (`ReadStringPieces`, the lock released after 4,096 strings or 16M
  characters) and hashed incrementally (`StringHashes`, used by duplicate strings and the equal-strings scope): no
  string is built, so equal-size large payloads cost no memory beyond the buffer (sprint 1.6 review H2). The worker's `ObjectFormatter` turns values into display text in the invariant culture: strings quoted and
  cut at 1,000 characters with "...", `null`, chars in single quotes, booleans lower case, enums as `Name (value)` and
  flag combinations as `Read, Write (3)`, boxed primitives as `12345 (boxed int)`, arrays as `int[5]` or `int[2,3]`,
  references to non-objects as `0x... (not a valid object in this dump)`; compiler backing fields are named by their
  property; nested structs are child rows; arrays list their first 100 elements, then a "N more" row.
- Since 1.6 the loader records free space per heap kind (`SnapshotInfo.FreeBytes`, the GC's free objects, stored in
  the index cache metadata) and marks arrays of references (`TypeKind.ReferenceArray`): a reference array's non-null
  slots are its outgoing references. It records both as `SnapshotFacts` on `SnapshotInfo`; index caches written before
  1.6 have none, so sparse arrays is unavailable there ("reopen the dump with Rebuild") and fragmentation notes that
  free space is unknown. Objects ready for the finalizer thread are the `FinalizerQueue` roots; the finalizable
  inspection and scope count them with the registered ones.
- A full dump whose heap could not be read completely (unreadable objects, or a segment walk that covered noticeably
  less than the segment, a segment whose memory is not in the file, or a file shorter than its own minidump directory
  says, sprint 1.8) loses `CompleteGraph` and `ExactCounts` and carries a note. ClrMD failing to create the heap at all is
  refused with `SnapshotFormatException`.
  A minidump without heap memory (MINIDUMP_TYPE without full or private read-write memory) is refused at open.

### 5.2 Capabilities
`SnapshotCapabilities` is a `[Flags]` enum. Feature matrix:

| Capability | Meaning | Full dump | gcdump |
|---|---|---|---|
| `ObjectGraph` | Objects, types, references, roots | yes | yes |
| `CompleteGraph` | Every object is present (not sampled) | yes | only when the gcdump was not sampled |
| `ExactCounts` | Counts and sizes are exact, not scaled estimates | yes | only when not sampled |
| `ObjectAddresses` | Real object addresses | yes | yes (as recorded) |
| `Generations` | Gen0/1/2, LOH, POH, frozen per object | yes | no |
| `FieldValues` | Read primitive and nested struct field values | yes | no |
| `StringContents` | Read string text (needed for duplicate strings) | yes | no |
| `RootDetails` | Static field names, thread ids for stack roots | yes | partial (root kind only) |
| `ThreadStacks` | Managed thread stacks | yes | no |

Rules:
- Features are never faked or approximated silently: a capability a snapshot lacks is explained in the words of the
  Contracts copy of `CapabilityDescriptions` (for example "Field values are not stored in a gcdump. Capture a full
  dump to inspect fields."), which the availability list below carries.
- Since sprint 3.8 (peer review P7) the optional features a front end offers come from the snapshot descriptor's
  availability list, not from the kind or the capability flags: one rule per feature in the worker (`FeatureRules`,
  from the capabilities, the loader facts and the kind handler) fills the list at open and refuses the feature's
  requests with the listed reason. The unreachable set needs a full dump with a complete object graph (a truncated
  dump's lost references would show live objects as garbage); interface grouping and field values need a dump reader
  with `FieldValues`; string values a reader with `StringContents`; generations `Generations`; the finalizable scope
  the `FinalizerRegistrations` fact. The UI shows the listed reason; the CLI shows it for generations and otherwise relays
  the worker's refusal, which carries the same text. An id the worker did not list counts as unavailable. What differs by kind (whether a snapshot holds garbage, what an unreached object means, whether objects
  can be matched by address) is a property of the kind handler (`SnapshotKinds`), not a check of the kind.
- Analysis entry points declare their required capabilities; calling one without them throws
  `CapabilityNotSupportedException` and the CLI reports it with a non-zero exit code.
- Diffs across snapshot kinds are allowed on type summaries; the result carries the intersection of both
  snapshots' capabilities (`ExactCounts` only when both are exact). A gcdump names a nested type without its declaring
  type (`Entry<System.Int32, X>[]` for `Dictionary<System.Int32, X>+Entry[]`), so a diff of a full dump and a gcdump
  matches the full dump's names in that form (`TypeNameForms.AsGcDumpName`). Each row keeps the name each side used:
  it opens on the full dump side by the full dump's own name, and a row that joins several full dump types under one
  gcdump name (for example several `<>c` classes of one module) cannot open one type's objects and says so. Across
  kinds the "copies" marker is off: mapped names join types, so type counts per row say nothing about copies.

### 5.3 gcdumps (sprint 1.8)
- A gcdump is what dotnet-gcdump and PerfView write: the capture opens an EventPipe session with the GC heap snapshot
  keywords, the runtime runs one full blocking collection and reports every surviving object, its type, size and
  references, and the roots. HeapScope captures them with the vendored dotnet-gcdump code (`GcDumpCapturer`, 30 second
  default timeout, no file left on failure or cancellation). On .NET 10 the session cannot use lossless buffering (that
  needs .NET 11), so a very large heap can lose events; nodes that were referenced but never described then make the
  load drop `CompleteGraph` and `ExactCounts`.
- The file is a graph of nodes. Nodes with addresses and a description are objects; nodes without an address, and COM
  callable wrappers (`[CCW 0x... for type X]`, described at the native IUnknown address), are pseudo nodes that form the
  root tree, and the loader maps their names to root kinds (`RootClass`): `[local vars]` to thread stack roots,
  `[static var Type.field]` to static field roots, `[strong Handles]`, `[pinning Handles]`, `[finalizer Handles]`,
  `[COM/WinRT Objects]` and CCWs to the handle kinds, `[Dependent Handles]` to a retaining other root (section 6.1),
  `[other Handles]` and the rest to other roots. Weak handles are not written. .NET 10 does write static roots, but
  names them by the value's type, not the declaring type (`Dictionary<...>.s_cache`), so their detail reads
  `s_cache (Dictionary<System.Int32, X>)`. Runtime callable wrappers (`[RCW X]`) are objects: the loader restores the
  name, removes the 1,000 bytes the writer adds for the native part, and skips the child at the COM object's IUnknown.
  Nodes that were referenced but never described (lost events), and `UNKNOWN 0x...` types (lost type events), make the
  load drop `CompleteGraph` and `ExactCounts`. The writer stores sizes as int; the loader reads them unsigned, which
  recovers objects up to 4 GB.
- The writer keeps at most 10 million nodes and reports success when it stops there. HeapScope's capture fails instead
  ("more objects than a gcdump can hold"), so it never writes a silently partial gcdump; a capped gcdump from another
  tool loads as incomplete. A cancelled capture ends its session (vendored change 3) and never waits more than 10
  seconds for it. The vendored code's lines are written to the HeapScope log (Debug, warnings at Warning), including its
  lossy-buffering warning on .NET 10 (vendored change 4, sprint 1.9).
- Every object's heap kind is `Unknown` (no generations), and there is no reader: no field values, no string text, no
  free space, reference-array marks or finalizer registrations (`SnapshotFacts.None`), so the inspections and scopes
  that need them (finalizable objects since review H5) are unavailable with the gcdump explanation. Strings and arrays
  are recognized by name. Reading the file reports progress and honors cancellation (`ObservedReadStream`).
- A gcdump is untrusted input: its stream names the types to create. TraceEvent 4.0's deserializer creates only types
  registered with it (the reader registers its own seven) and refuses any other name ("must be registered with the
  deserializer"); a crafted file naming another type is refused before that type is created (test
  `A_crafted_gcdump_naming_an_unknown_type_is_refused_without_creating_it`, sprint 1.9).
- Index caches record the version of the loader that built them (`IndexCacheMetadata.LoaderVersion`,
  `ISnapshotLoader.Version`: full dumps 2 since 1.8, gcdumps 1); the worker rebuilds a cache from an older version with
  a note, so a fix in what a loader records reaches snapshots cached before it (review M7).
- Type names: the writer joins generic arguments without a space, writes compiler names' `<>` as `[]`, splits arrays
  above 1 KB and 10 KB into nodes named `T[] (Bytes > 10K)`, and drops the declaring types of nested types. The loader
  undoes the first three (`TypeNames.Normalize`); the last cannot be undone. Types without a method table are told apart
  by name and module.
- Sampled gcdumps (PerfView can write them; HeapScope and dotnet-gcdump never sample) are loaded as recorded, without
  scaling; they lose `CompleteGraph` and `ExactCounts` and carry a note. Every gcdump carries a note that it holds the
  survivors of a collection and has no generations, field values or string text.
