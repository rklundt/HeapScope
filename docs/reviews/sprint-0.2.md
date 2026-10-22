# Review: Sprint 0.2 Snapshot abstraction and compact graph

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-0.1`). Phase 0
runs in autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided
autonomously".

Totals after dedupe: 0 High, 11 Moderate, 14 Low.

## High / Urgent
None.

## Moderate

### M1 No access to frozen addresses for the reference pass
- Reviewers: solution-architect, senior-dev. Location: src/HeapScope.Core/Graph/HeapGraphBuilder.cs.
- What: ARCHITECTURE.md 6.3 tells loaders to walk the sorted addresses, but the builder did not expose them.
- Decision: **fix now** (decided autonomously). `FrozenAddresses` added (valid between freeze and build); test
  drives a reference pass from it on unsorted input; documented in 6.3.

### M2 Benchmark did not exercise the loader path; budget headroom overstated
- Reviewer: solution-architect. Location: perf/HeapScope.Benchmarks, ARCHITECTURE.md 6.2 and 10.
- Decision: **fix now** (decided autonomously). `--by-address 1` mode added. It exposed a real problem: 150M
  random by-address lookups took **217 s** with plain binary search. Fixed with a bucketed `AddressIndex`
  (about 25 MB for 50M objects): **46 s** worst case. 6.2 now records machine RAM and states that the
  unsorted, by-address number binds first-load budgets.

### M3 `GraphSnapshot` could claim service-backed capabilities
- Reviewer: solution-architect. Location: src/HeapScope.Core/GraphSnapshot.cs.
- Decision: **fix now** (decided autonomously). It strips `FieldValues`, `StringContents` and `ThreadStacks`
  (`ServiceBackedCapabilities`); test added; 5.1 documents that cache-backed full dumps need their own
  `ISnapshot` (sprint 1.2).

### M4 `TotalSize` lazy cache could tear under concurrent reads
- Reviewers: solution-architect (Moderate), senior-dev and gc-specialist (Low). Location: HeapGraph.cs.
- Decision: **fix now** (decided autonomously). Computed once at construction; the graph is immutable.

### M5 Builder not exception safe mid-row or after a failed freeze
- Reviewers: senior-dev (Moderate), solution-architect and gc-specialist (Low). Location: HeapGraphBuilder.cs.
- Decision: **fix now** (decided autonomously). `AddReferenceIndices` validates the whole row before changing
  state; any exception thrown while state changes moves the builder to a Faulted phase, and later calls throw
  "The builder failed earlier; start a new build." Tests for both.

### M6 Dependent-handle merge paths untested
- Reviewer: senior-dev. Location: tests/HeapScope.Core.Tests.
- Decision: **fix now** (decided autonomously). Tests for out-of-order registration, skipped rows, trailing
  rows, no reference calls at all, several secondaries per primary and an unknown secondary.

### M7 `AddReferences` contract silent about dependent handles
- Reviewer: senior-dev. Location: HeapGraphBuilder.cs, SPRINTS.md decision 3.
- Decision: **fix now** (decided autonomously). The XML doc states that targets exclude dependent-handle
  targets; decision 3 now says what the builder guarantees and that 1.2 tests the loader side.

### M8 Unsorted freeze churned the LOH with full-length temporaries
- Reviewer: gc-specialist. Location: HeapGraphBuilder.cs `Permute`.
- Decision: **fix now** (decided autonomously). Columns are permuted in place by following permutation cycles
  (visited marked by complement); the only extra memory is the permutation. Unsorted peak working set went from
  3.0 to 3.15 GB down to about 2.7 GB. Unit test against a reference permutation.

### M9 Weak handles could be stored as retaining
- Reviewer: gc-specialist. Location: HeapGraphBuilder.cs `AddRoot`, `AddRootIndex`.
- Decision: **fix now** (decided autonomously). The builder strips `Retaining` from weak handle roots; test.

### M10 `HasFinalizer` meaning ambiguous
- Reviewer: gc-specialist. Location: Graph/Vocabulary.cs, ARCHITECTURE.md 6.1.
- Decision: **fix now** (decided autonomously). Defined as "registered for finalization and not suppressed,
  from the runtime's finalizable-object list, not the type"; enum doc and 6.1 aligned.

### M11 Allocator memory leaked on failed or cancelled builds
- Reviewers: gc-specialist (Moderate), senior-dev (Low). Location: HeapGraphBuilder.cs, IColumnAllocator.cs.
- Decision: **fix now** (decided autonomously). `HeapGraphBuilder` is `IDisposable`; disposing an unbuilt builder
  disposes the allocator's owner. Tests with an owning allocator for cancelled and built cases.

## Low / Trivial (recorded only)
- L1 (solution-architect) No lease member on `ISnapshot` yet. (Moved into the 0.5 scope.)
- L2 (solution-architect) `TypeTableBuilder.GetOrAdd` needs a name even for a known method table; add
  `TryGetByMethodTable` and accept null names for 1.2.
- L3 (solution-architect) Doc drift: `Free` in `TypeKind`, `Flags` row, 5.1 snippet. (Corrected in the docs
  update, required by the Definition of Done.)
- L4 (senior-dev) Null root address counted as dropped. (Address 0 is now ignored as part of the M9 change set.)
- L5 (senior-dev) Floor search duplicated in builder and graph; untested interior cases (large object, exact
  start, below first object).
- L6 (senior-dev) Cancellation checks missing in some full-length loops. (Added to the loops touched by M8.)
- L7 (senior-dev) Enum stability tests check one value per enum; `SnapshotKind` lost its test.
- L8 (senior-dev) Several files hold more than one public type.
- L9 (senior-dev) Synthetic address layout computed twice in `SyntheticGraphBuilder`.
- L10 (gc-specialist) `LargeObjectSize` has implicit padding; persisted bytes not deterministic. (Moved into the
  0.5 scope, which owns the persisted format.)
- L11 (gc-specialist) Object and edge limits should be `Array.MaxLength` based. (Applied in the builder rewrite.)
- L12 (gc-specialist) Benchmark peak is process-wide and unattributed per phase; no managed peak.
- L13 (gc-specialist) Static storage `object[]` handle roots marked non-retaining would appear unreachable; keep
  them retaining and rank `StaticField` roots first in paths, or flag them. (Noted in the 1.2 scope.)
- L14 (gc-specialist) Same as M5 from another angle (partial rows). Covered by M5.

## Confirmations
No High findings, so no confirmations were required.
