# ADR-018 Diffs compare per-type totals; no object identity across dumps (added in sprint 1.4; amended by ADR-032)

- Context: dotMemory's comparison splits objects into survived, new and dead, using its profiler's allocation
  tracking. Two dumps carry no object identity: compaction moves objects between snapshots and freed addresses are
  reused, so matching by address would report wrong survivors with confidence. Dumps and gcdumps have no allocation
  stream.
- Decision: a snapshot diff matches rows by type name plus module file name and shows each side's objects and bytes with
  signed deltas, new and gone rows, and how many distinct types (method tables) each side summed into the row. Method
  tables sharing a key are summed (the types view keeps them apart, sprint 1.3), and a type row whose number of copies
  changed is marked "copies", so a stale load context next to its reloaded copy stays visible even when the summed
  counts are flat. Results say whether the counts are exact (`ExactCounts`, false when a side is a sampled gcdump).
- Consequences: growth is per type, which is what leak hunting across replayed traffic needs (the known answers grow by
  exact counts). Both sides count unreachable objects the GC has not collected yet, so a CI gate should compare dumps
  captured after a full GC in the target. Drilling into a row opens that type's objects in either snapshot (1.5), not
  "the new ones". An allocation-tracking live session (2.x at the earliest) could add identity later as a separate
  source.
