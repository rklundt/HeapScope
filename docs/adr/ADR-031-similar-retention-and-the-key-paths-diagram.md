# ADR-031 Similar Retention and the key paths diagram (added in sprint 3.2)

- Context: key paths (2.2) group objects by their one shortest path from a root, so objects held by the same two
  collections can split across groups by which one the search reached first, and objects held in several ways look
  like objects held in one. dotMemory's Similar Retention groups objects by how they are retained and shows each
  group's most different paths; its Key Retention Paths diagram merges the paths into one graph.
- Decision: a member's retention pattern is the set of key path signatures of its direct referrers that a root reaches
  without passing through the member (a back pointer from its own child retains nothing; checked up to 256 steps up the
  referrer's path), its own shortest path always included, its root when it is one, at most 16 referrers per member;
  members with the same pattern form a group. A group shows its first member's path and the path sharing the fewest steps
  with it; every member has every path, so the paths overlap and the diagram counts a group once. Groups rank by objects,
  bytes, then first member, and are numbered from 1 wherever a person reads them (app, CLI, logs). A group set
  (`ObjectSetKind.RetentionGroup`) is named by its pattern's fingerprint (the SHA-256 of its paths' root keys and type
  names, 8 bytes) with the rank as a hint, and resolved by grouping the inner set again; a pattern this snapshot does not
  have is refused, never replaced by another group of the same rank. The diagram is built by the front end from the key
  paths: a node per root (kind and description), per type and per target (the set's title for a set), a gap node per
  long path, layered by first depth. Interfaces are a derived index of each snapshot (a `DerivedData` slot: its own
  lease, shared progress, dropped on close): read through up to three objects of each type from ClrMD (the type's and its
  base types' interfaces and the interfaces those extend) plus generic instantiations decoded from the module's metadata,
  taken from the dump or, when the dump does not hold the image, from the module's file on this machine if its image size
  matches. A type counts under each interface, so interface rows overlap: no retained column, no growth gate over them,
  and a type whose interfaces could not be read is on its own "(interfaces not read)" row, never "(no interface)". The
  grouping is offered only where every snapshot is a full dump.
- Consequences: Similar Retention needs the root path tree and the reverse references (both built once per snapshot) and
  reserves 4 bytes per member, 200 bytes per possible pattern (at most 1,000,000 patterns; more is refused with a
  message to narrow the set) and the path signature memo (48 bytes per remembered object, at most 8 million), compacting
  after. A member with more than 16 referrers is grouped by the first 16 (counted in the result). Interface grouping
  costs one dump read per type on first use, and a module file read per module for generic interfaces; a module moved or
  replaced since the dump (its time stamp or size differ from the dump's module index) gives no generic interfaces.
  Generic arguments come from the type's name, every generic segment in order; a type whose arguments cannot be known
  is unread. COM objects show their metadata interfaces only. The back pointer check climbs 256 steps of the referrer's
  shortest path: a back pointer deeper in a chain still counts, and a child also held elsewhere whose shortest path runs
  through the member is skipped (an exact test is in the Backlog).
