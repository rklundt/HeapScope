# ADR-024 Retention semantics shared by paths and dominators (added in sprint 2.3)

- Context: retention paths (2.2) and dominators (2.3) both answer "what keeps this alive", and the 2.2 review (L6)
  asked for one written rule set before a second analysis relied on it.
- Decision: one definition of what is alive by itself, `PathRoots.Sources`: objects held by a retaining root (weak handles
  and dead ref-counted handles never are; 6.1) in priority order (static fields, thread stacks, handles, the finalizer
  queue, other roots), then frozen objects, which are never collected. Retention paths start at these objects; the
  dominators' virtual root has exactly these successors. Dependent handles are edges from primary to secondary, so a
  `ConditionalWeakTable` value is retained by its key. An object these sources do not reach is unreachable: garbage not
  yet collected in a full dump, an object whose root the gcdump did not record in a gcdump. A type's (or namespace's,
  or assembly's) retained size is the lower bound of `GroupRetention`.
- Versioning: `PathRoots.SemanticsVersion` changes with these rules; anything kept that depends on them records it (the
  dominators sidecar records `DominatorTree.SemanticsVersion`, which includes it) and is rebuilt when it changes.
- Consequences: a change to root semantics (a new root kind, collectible statics, a new kind of handle) is one change in
  `PathRoots`, a version bump, and a rebuild of the kept dominators; paths and retained sizes cannot disagree about
  what is alive. The known answers check paths against their path shapes and retained sizes against an independent
  ClrMD walk that starts from the same sources (sprint 2.3: the first, middle and last item of every rooted fixture,
  and the frozen literal, equal it).
