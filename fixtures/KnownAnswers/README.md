# Known answers

One JSON file per leak or semantic fixture in `fixtures/Leaks` (ADR-013). They state what an analysis of a dump of
LeakyConsole or LeakyApi must find. Integration tests (`KnownAnswerTests`, `LeakyApiGrowthTests`,
`DisabledLeakTests`) verify every statement against real dumps with ClrMD; HeapScope's own analysis features are
then tested against the same files.

## Format version 1

| Field | Required for | Meaning |
|---|---|---|
| `formatVersion` | all | `1`. Bump on any incompatible change. |
| `name` | all | Leak name (`ILeak.Name`) or semantic fixture name; equals the file name. |
| `kind` | all | `leak` or `semantics` (later: `inspection`, `clean`). |
| `description` | all | What the fixture does. |
| `types` | all | Fixture-owned types and instances per item (`perItem`). A dump of N items has exactly `N * perItem` instances. `types[0]` is the item type. |
| `census` | leaks | Every type an item creates, including BCL types. `exact: false` means other code creates the type too (or it is recreated, like dictionary nodes on resize), so only a lower bound on growth holds. `shallowBytes` is the x64 instance size where fixed. |
| `roots` | leaks, rooted semantics | How items are kept alive: `kind` uses HeapScope's `RootKind` names; for static fields `declaringType` and `field`; `itemType` when the root retains a type other than `types[0]`. The named roots are the only retainers of their items. |
| `runtimeRoot` | leaks | How the runtime itself roots the static storage: GC root kind and the container that references the static value. |
| `growingCollection` | leaks | The object that grows with the leak (what a "growing collection" detector names). |
| `pathShape` | leaks, rooted semantics | Expected retention path from the first root to an item (grammar below). |
| `minRetainedBytesPerItem` | leaks, rooted semantics | Lower bound of the retained size of one `types[0]` instance: `base + payloadMultiplier * payloadBytes`. Retained size is the dominator definition: bytes that become unreachable when that instance is removed. |
| `presentButUnreachable` | late garbage | Types present in the dump that no retaining root reaches. |
| `heapKinds` | runtime kinds | For each listed type, the object in `field` must be in the given GC segment kind. |
| `notes` | all | What the analysis must and must not conclude. |

## Path shape grammar

A path shape is an ordered list of steps matched against the actual path as an ordered subsequence (other types
may appear between steps):
- The first step is `DeclaringType.field` and names the root.
- Every other step must be contained in the type name of a path element after generic argument lists are
  removed (`System.Collections.Generic.Dictionary<System.Int32, X>+Entry[]` becomes
  `System.Collections.Generic.Dictionary+Entry[]`); compiler-generated names keep their empty `<>`
  (`<>c__DisplayClass`).
- A step ending in `?` is optional (for example a multicast invocation list that exists only with more than one
  subscriber).
- `(dependent handle)` marks an edge that exists only through a dependent handle (ConditionalWeakTable).

## Test parameters

The shared test dump uses 2,000 items per leak with a 32 byte payload and 64 event publishers (so invocation lists
are multicast), 500 semantic items, 20 runtime kind items and 300 late garbage items.
