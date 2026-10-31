# HeapScope architecture: Index cache

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 7" still name the right section.

## 7. Index cache (ADR-006)

Implemented in sprint 0.5 (`HeapScope.Storage`: `IndexCacheWriter`, `IndexCacheReader`, `IndexCacheLocator`,
`SnapshotFingerprint`, `MemoryModeSelector`), hardened by its review.

- Location (`IndexCacheLocator`): `<snapshot path>.hsidx` next to the snapshot when that folder accepts a probe
  file; otherwise `%LOCALAPPDATA%\HeapScope\cache\<sha256 of the full path>.hsidx` (root configurable).
- Layout (format version 2 since sprint 2.2, little-endian):
  - Header region (4096 bytes): a 64 byte fixed header (magic `HSIDX`, format version, header region length,
    section count, snapshot fingerprint, XxHash3 checksum over header and directory), then the section
    directory (40 bytes per section: id, element size, offset, byte length, element count, XxHash3 checksum). Since
    3.7 (ADR-036) the region's last 32 bytes hold an HMAC-SHA256 of the rest with the user's key; with a key in effect
    (the worker's), a cache whose signature does not match is `Untrusted` and the dump is read again.
  - Sections, each aligned to 4096 bytes (ready for mapping): Addresses, TypeIndices, Sizes, HeapKinds,
    ObjectFlags, EdgeStarts, Edges, LargeSizes, Roots, the six type table columns, Strings (count, offsets,
    UTF-8 bytes), Metadata (UTF-8 JSON: `SnapshotInfo`, capabilities, writer version, dropped counts) and, since format
    version 2, DependentHandles (the dependent-handle pairs, 8 bytes each). A version 1 cache is refused and rebuilt.
  - Section payloads use exactly the in-memory element layout (unmanaged structs with explicit padding fields,
    no compression), so mapped mode (ADR-015) hands a view straight to a `Column<T>`. Writing the same graph
    twice produces identical bytes.
- Identity: the header checksum covers every section checksum, so it identifies the cache's content
  (`IndexCacheOpenResult.Identity`).
- Optional indices (reverse references in 2.1, dominators in 2.3: `app.dmp.dom.hsidx`, the immediate dominators and the
  retained sizes, and since 2.4 the dominator tree's children) go into **sidecar files** next to the base
  cache (`app.dmp.rev.hsidx` beside `app.dmp.hsidx`; `IndexCacheLocator.GetSidecarPath`), in the same header and
  directory format, each recording the identity of the base cache it extends in the header's `BaseIdentity` (reserved
  and 0 before 2.1, and still 0 in a base cache, so the format version did not change) and the snapshot's fingerprint.
  The base file is never rewritten to add them: a mapped base cache is open and cannot be replaced, and rewriting would
  break the header-last crash safety. A sidecar whose base identity or fingerprint does not match, or that is damaged,
  is ignored and rebuilt; a missing sidecar means "not built yet". The identity covers every section, the metadata
  section included (the writer version, the loader version and the path of the first load), so rebuilding the base
  cache with the same HeapScope build from the same dump at the same path keeps it and its sidecars stay valid, while a
  new HeapScope version or a moved dump makes them stale (they are rebuilt). Each kind of file has an
  `IndexFileLayout` (its sections and element sizes; every listed section present, no other allowed), so a sidecar
  never opens as a base cache or the reverse. Sprint 2.1 implements the pieces the 0.5 review asked for:
  `IndexCacheFileWriter` writes any index file section by section (temporary file, header last, flush, rename; an
  uncommitted writer deletes its temporary file; empty trailing sections extend the file to their offset), and
  `IndexCacheFile` opens any index file with the base cache's checks (sections read into arrays of their element size
  and verified in one pass in memory, so a section holds up to 2^31 - 1 elements like the graph's columns, review M1;
  verified sequentially, then viewed from a read-only mapping, when mapped) and hands sections out as columns
  (`TryGetSection<T>`). Sprint 2.3 adds `MappedIndexFileWriter`, the same file computed in place: its sections are planned
  up front, the temporary file is created at its final size and mapped read-write, the caller fills each section through
  a span (the pages belong to the file, so the OS can write them out under pressure and no managed copy exists), and
  `Commit` unmaps, hashes the sections with sequential reads, writes the header last, flushes and renames; it writes the
  same bytes as `IndexCacheFileWriter`. A sidecar also records the version of the rules it was computed under in the
  header's formerly reserved field (`IndexFileLayout.SemanticsVersion`, from the 2.1 review): a mismatch is refused like
  an older format and the sidecar is rebuilt; base caches and reverse references sidecars keep 0, so the format version
  did not change. `IndexCacheWriter.Write` returns the identity, and the worker's descriptor carries it
  (`CacheIdentity`, from the 1.2 review).
- Fingerprint (`SnapshotFingerprint`): snapshot length, last write time UTC and an XxHash3 of the first and last
  MiB; any difference invalidates the cache.
- Writing: sections go to a temporary file in the target folder, the header is written last, the file is flushed
  to disk and renamed over the target. A process that dies at any point leaves either the previous cache or a
  temporary file without a valid magic; a canceled or failed write deletes its temporary file, and temporary
  files of this cache older than 10 minutes (left by a crashed writer) are removed before writing. Replacing a
  cache that is open (for example mapped) fails with `IndexCacheInUseException` and keeps the previous cache.
- Opening (`IndexCacheReader.Open(path, expected fingerprint, mode)`): one file handle, shared for reading only, is
  used from the first header byte to the last section byte and becomes the mapping in mapped mode, so the file that
  was validated is the file that is used and nobody can replace it meanwhile. Magic, version, header checksum,
  every directory entry (known ids only, no repeats, element size, non-negative counts, page-aligned offsets past
  the header, overflow-safe bounds against the file length), fingerprint and every section checksum are verified
  before a graph is returned. Invalid caches return a reason (`Missing`, `Truncated`, `BadMagic`,
  `VersionMismatch`, `HeaderCorrupted`, `Malformed`, `FingerprintMismatch`, `ChecksumMismatch`, `IoError`,
  `BaseMismatch` for a sidecar, `Untrusted` since 3.7) and a plain-language detail, never an exception and never a read outside the file; they are rebuilt, never patched.
- Modes: in memory, every section is read into exact-size arrays and verified in the same pass (nothing more is
  allocated after a failure). Mapped, the sections are first verified with a sequential read (pooled 4 MB buffer,
  several times faster than hashing through the mapping and without growing the working set), then the
  per-object columns, edges and roots become views of a read-only mapping (`MappedMemoryManager<T>` behind
  `Column<T>`); the small tables (types, large sizes, strings) are copied. A mapped file stays open, and cannot be
  replaced, until the graph is disposed.
- Lifetime: disposing a snapshot (`GraphSnapshot` or `DumpSnapshot`) never blocks or throws. It refuses new leases and the last released lease
  disposes the graph (and unmaps the file), so queries and result sets that page over a snapshot can hold leases
  safely (6.5).
- Versioning: any layout change of the base file bumps the format version. Optional JSON fields in the metadata
  section do not: 1.2 added `DebuggableModules` and `UnreadableObjects`, which read as null and 0 from older caches.
- Measured (`index-cache`, synthetic 50M objects with 150M references, 1.6 GB file, reopened in a fresh process,
  mapped first, warm page cache): write 3.2 s; reopen mapped 0.6 s (+9 MB private, +202 MB working set),
  scanning all edges of the mapped graph 0.2 s; reopen in memory 0.6 s (+1.6 GB private). Cold page cache (standby
  list purged with administrator rights, run by the product owner on 2026-10-23): reopen mapped 1.0 s (+9 MB
  private), in memory 0.8 s. Antivirus scanning of a freshly written cache is not measured separately.
