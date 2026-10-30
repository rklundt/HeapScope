# ADR-032 Objects of two snapshots matched by address (added in sprint 3.3, amends ADR-018)

- Context: ADR-018 compares per-type totals only, because dumps carry no object identity: collections move objects, and
  freed addresses are reused. dotMemory's comparison shows New, Dead and Survived objects per type and opens them as
  sets; the product owner asked for that depth in Phase 3.
- Decision: within one run of a process, an object keeps its address until a collection moves it. Two complete full
  dumps of one process (same process id and name, the current captured later) are matched by one merge of their sorted
  addresses: a current object Survived when the baseline has, at its address, an object of the same method table and
  type key, the same size, the same heap area (small object heap, LOH, POH, frozen), the current object in the same or
  an older generation (a collection that demotes an object, rare, makes a real survivor Dead and New); New
  otherwise. A baseline object with no such object at its address is Dead. A match whose baseline frozen objects (which
  never move or die in one run) are mostly not found is refused as "not one run" (a reused process id, such as a
  container's pid 1; frozen objects exist from .NET 8 on, so on earlier runtimes only the low-survivor caution
  remains). gcdumps (objects after a forced collection, no process id), incomplete dumps and dumps of other
  processes are refused with the reason. Compare asks for it with a switch; counts, column tooltips and set titles say
  "by address". New and Survived sets live in the later snapshot, Dead sets in the earlier, each naming the other
  snapshot (`ObjectSetSpec.Baseline`, which is "the other snapshot", bound to the current ids at each call).
- Consequences: what the counts mean. Gen0 and Gen1 collections move their survivors (except regions promoted in place),
  so objects that were young in the baseline usually count as Dead and New; Gen2 (swept by background collections), LOH
  (unless compacted), POH and frozen objects keep their addresses, and that is where Survived is trustworthy. A new object
  of the same type and size allocated where a dead one was, in the same or an older generation, still counts as
  Survived: the check catches a different size, area, method table or a younger generation, not every reuse. Full dumps
  hold unreachable objects, so New and Survived include garbage not yet collected. A forced GC (or a gcdump captured)
  between the two dumps moves most objects: the result then carries a caution when under a tenth of the objects kept
  their address. New is never less than a type's growth. Matching costs a bit per object of each snapshot and one pass,
  and runs again for each diff and compared set (keeping it per pair is in the Backlog). The capture advice of a growth
  gate (force a full GC first) and of New and Dead (do not force one between the dumps) differ: DEVELOPER.md says which
  goes with which.
