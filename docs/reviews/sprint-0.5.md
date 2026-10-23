# Review: Sprint 0.5 Index cache format

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-0.4`). Phase 0
runs in autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided
autonomously".

Totals after dedupe: 2 High, 8 Moderate, 9 Low.

## High / Urgent

### H1 Optional 2.x sections cannot be added to an open mapped cache
- Reviewer: solution-architect.
- Location: ARCHITECTURE.md 7; MappedMemoryManager.cs; IndexCacheWriter.cs (`File.Move` over the target).
- What: the plan was to add reverse reference and dominator sections to the same file, but a mapped graph keeps the
  file open, so it cannot be rewritten; appending in place would break the header-last crash safety.
- Why it matters: mapped mode is the 16 GB case where 2.1 and 2.3 matter most; it would force a redesign then.
- Fix: decided now and recorded in ARCHITECTURE.md 7 and ADR-006: optional indices live in sidecar files
  (`<cache>.rev.hsidx`, `<cache>.dom.hsidx`) in the same format, each naming the base cache by its identity (the
  header checksum, which covers every section checksum; exposed as `IndexCacheOpenResult.Identity`, tested stable
  across modes). The base file is never rewritten. The public opened-cache API and incremental writer are scoped
  into 2.1.
- Status: **Fixed** (design and identity). Confirmation: see below.

### H2 Mapped reader could read outside the view
- Reviewers: senior-dev (High), solution-architect and gc-specialist (Moderate).
- Location: IndexCacheReader.cs, MappedMemoryManager.cs.
- What: the directory was validated only for known ids, with an overflowing bound check and no checks for negative
  counts; the file was opened twice (validate, then map), so a replaced file could be mapped with the old
  directory; short reads were ignored; several decode failures threw instead of returning a reason.
- Why it matters: reading outside a mapped view is an access violation that kills the worker; "never a crash" is an
  acceptance criterion.
- Fix: one handle (shared for reading only) from the first header byte to the mapping; every directory entry is
  validated (known ids, no repeats, element size, non-negative counts, page-aligned offsets past the header,
  overflow-safe bounds); mapped ranges are re-checked against the view length; short reads are `Truncated`;
  string decoding is overflow safe; null metadata is `Malformed`; a final catch maps argument, overflow, JSON and
  data exceptions to `Malformed`. New `MalformedCacheTests`: 13 hostile or damaged cases, each in both modes, with
  checksums recomputed so the shape checks are what catches them.
- Status: **Fixed.** Confirmation: see below.

## Moderate

### M1 `GraphSnapshot.Dispose` blocked for 30 s and threw, leaking the mapping
- Reviewers: solution-architect, senior-dev. Decision: **fix now** (decided autonomously). Disposal never blocks or
  throws: it refuses new leases and the last released lease disposes the graph; idempotent and thread safe
  (Interlocked in `HeapGraph` and `MappedIndexFile`). Tests: deferred release, concurrent dispose and release,
  and a mapped file that stays open until a result-set-style lease is released.

### M2 Mapped open hashed every page through the mapping
- Reviewers: gc-specialist, solution-architect. Decision: **fix now** (decided autonomously). Sequential
  verification with a pooled 4 MB buffer before mapping. Measured in a fresh process: mapped reopen 0.6 s
  (was 1.4 s), working set +202 MB (was +1.6 GB), private +9 MB.

### M3 Reopen numbers were warm and best case
- Reviewers: gc-specialist, solution-architect. Decision: partly **fix now** (fresh-process reopen with mapped
  first, working set reported), cold cache and antivirus **deferred** to the backlog (decided autonomously; a
  standby purge needs administrator rights). Section 7 and 10 say so.

### M4 Memory budget read stale or wrong figures
- Reviewer: gc-specialist. Decision: **fix now** (decided autonomously). `MemoryBudget.Current` reads live physical
  memory (GlobalMemoryStatusEx); a GC hard limit only caps it. Test checks live available memory is in range.

### M5 No public API for opened caches and optional sections
- Reviewer: solution-architect. Decision: **defer to 2.1** (decided autonomously), recorded in its scope with the
  incremental writer (L6).

### M6 Orphaned temporary files and undocumented failure when replacing an open cache
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). Temporary files of the cache older than 10
  minutes are removed before writing; replacing an open cache throws `IndexCacheInUseException`, keeping the old
  cache and leaving no temporary file. Tests for both.

### M7 Reader shape checks untested; truncation and header damage tested in one mode
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). Covered by `MalformedCacheTests` (H2).

### M8 Decoding paths could throw or return a broken success
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously), merged into H2.

## Low / Trivial (recorded only)
- L1 (solution-architect) Metadata section serializes Core's `SnapshotInfo` directly. (Backlog item.)
- L2 (solution-architect) No incremental writer for the Large tier. (Scoped into 2.1 with M5.)
- L3 (senior-dev, gc-specialist) Benchmark budget check ignored open failures. (Fixed with M3: both successes are
  part of the gate.)
- L4 (senior-dev, gc-specialist) Reader allocated every section after the first checksum failure. (Fixed with H2.)
- L5 (gc-specialist) Mapped verification checked cancellation only between sections. (Fixed with M2: per chunk.)
- L6 (solution-architect) Incremental writer API. (See L2.)
- L7 (senior-dev) Read-only fallback tested with a file standing in for the folder, not a deny-write ACL.
- L8 (senior-dev) Test cleanup swallowed only `IOException`. (New test classes also catch
  `UnauthorizedAccessException`.)
- L9 (senior-dev) Found while fixing: the static writer test hook leaked into tests running in parallel; it is now
  flow-local (`AsyncLocal`).

## Confirmations
- H1 confirmed by solution-architect: sidecar files in the same format naming the base by its identity; the base
  is never rewritten; identity is the header checksum (covers fingerprint, directory and every section checksum)
  and is tested stable across modes; the last lease unmaps. Its Moderate findings handled as it accepts; it asks
  that the cold-cache measurement happens before the 1.2 budget sign-off (carried into the 1.2 scope).
- H2 confirmed by senior-dev: one file handle throughout, every directory entry validated with overflow-safe
  bounds before any offset is used, mapped ranges re-checked, short reads truncated, decode failures mapped to
  reasons, `MalformedCacheTests` covers 13 cases in both modes. Its Moderate findings handled as it accepts; noted
  residual risk: a leaked lease keeps a mapping open silently (a log on close with outstanding leases is a cheap
  later addition, backlog).
