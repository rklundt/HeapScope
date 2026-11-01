# ADR-006 On-disk index cache

- Context: parsing a Medium dump takes minutes; investigations reopen the same snapshots many times.
- Decision: persist the parsed graph next to the snapshot, versioned, invalidated on format change.
- Consequences: a binary format to maintain; sensitive data on disk (section 12). Decided in sprint 0.5: the header is written last and
  the file renamed atomically; every section is verified on open; a mapped cache stays open and cannot be replaced;
  optional 2.x indices live in sidecar files that name the base cache by its identity (section 7).
- Amended in sprint 2.3: a sidecar may be computed in place through a read-write mapping of its temporary file
  (`MappedIndexFileWriter`), with the same header-last, flush and rename guarantee, and records the version of the rules
  it was computed under, so one from older rules is rebuilt like one from an older loader.
