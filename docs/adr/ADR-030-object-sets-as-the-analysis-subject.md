# ADR-030 Object sets as the analysis subject (added in sprint 3.1)

- Context: Phase 2 analysed one type's objects at a time; dotMemory's model is the object set, where any selection
  (several types, a namespace, a comparison row, an inspection finding, marked objects, what a set retains) becomes a
  subject with every view. The product owner found HeapScope "well under powered" after Phase 2 and agreed to Phase 3.
- Decision: a set is described by an `ObjectSetSpec` (what it selects, not its members) that the client keeps and sends
  with each query; the worker resolves it in one snapshot into its members (sorted object indices and a bit per object)
  and caches a few resolved sets and inspection tables. There is no set handle on the wire: a spec survives result set
  eviction and worker restarts, and the analysis path replays it. An inspection row's baseline is held by the client as
  a snapshot handle and bound to the current id at each call. Leak detector rows list the objects they counted; other
  inspection rows resolve through their hidden columns (a type in a scope, or one object). A set's views are Types (no
  retained column: the Retained view is exact), Instances, Generations, Dominators (the members no other member
  dominates) and Retained.
- Consequences: specs are small, but a resolved set costs 4 bytes per member and a bit per object of the graph; it is
  built inside a reservation of that much and cached (at most 8 sets and 256 MB of them, never one over half, least
  recently used out); only leak detector tables are cached for their rows (128 MB, member arrays counted). One
  resolution per set runs at a time. What a set retains is computed with the same memory reservation as set retention;
  a set's dominators reserve 37 bytes per object (two chunked walks, exact arrays, the sort). A detector row's members
  are the objects whose bytes it counts (an event's subscriber delegates and their targets; a timer's queue entry and
  its `System.Timers.Timer`). Compare's New, Dead and Survived objects came in 3.3
  (ADR-032). Sets match types by name and module like every other query (1.5 M3 stays in the Backlog).
  An `InspectionRow` spec depends on the inspection's row order, which is deterministic for one snapshot and rule version.
