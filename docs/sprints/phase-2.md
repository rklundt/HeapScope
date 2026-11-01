# HeapScope sprints: Phase 2 (heavy hitters)

Closed sprints moved here from [SPRINTS.md](../SPRINTS.md) in sprint 3.8; each keeps its goal, scope,
acceptance criteria, decisions, tasks and review summary as they stood when it closed. Reviews are in
[docs/reviews/](../reviews/).

## Sprint 2.1: Reverse references

**Goal**: answer "who references this object or type".

**Scope**: incoming-reference CSR built in the worker and cached as an optional section; queries by object and
by type. From the 0.5 review: the optional section is a sidecar file naming the base cache identity; a public
opened-cache API (`IndexCacheFile`, `TryGetSection`) and an incremental section writer come first.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): an incoming references grid per object and per type.

From the 1.2 review: `IndexCacheWriter.Write` returns the cache identity; the worker keeps it per snapshot and the descriptor carries it, so a reopen can tell a changed file and sidecars can name their base. Reachability treats frozen objects as alive (6.1).

**Acceptance criteria**: synthetic tests with known in-degrees; Medium build time and memory recorded within
budget; cached section reused on reopen.

**Dependencies**: 0.5, 1.2.

**Task breakdown**
1. [x] Incremental section writer: `IndexCacheFileWriter` (temporary file, sections one at a time, header last, flush and
   rename) used by `IndexCacheWriter`, whose `Write` now returns the cache identity; the header's last reserved field
   becomes `BaseIdentity` (0 in a base cache, no format change). Tests: the existing writer tests unchanged (identical
   bytes), the returned identity equals the reader's, a disposed uncommitted writer leaves no file.
2. [x] Opened-cache API: `IndexCacheFile` opens any HeapScope index file (the base cache's directory checks, shared with
   `IndexCacheReader`), in memory or mapped, with `TryGetSection<T>`, `Identity` and `BaseIdentity`; sidecar sections
   `InEdgeStarts` and `InEdges`; reason `BaseMismatch`; `IndexCacheLocator.GetSidecarPath`. Tests: a sidecar round trip
   in both modes, a wrong base identity, a missing or unknown section, a checksum mismatch, a base cache refused as a
   sidecar.
3. [x] Core `ReverseReferences`: built by counting in-degrees and filling (referrers of each object ascending, one entry per
   reference), from columns for a cache, `GetReferrers`; `HeapGraph.Attach` disposes an index with the graph after the
   last lease. Tests: chain, diamond, cycle, self loop, duplicate references, a seeded random graph against a naive count.
4. [x] Analysis `IncomingReferences`: per object (distinct referrers with their reference counts) and per type (referrers
   grouped by their type: referring objects, references, targets referenced, referrer bytes). Tests: known answers on
   synthetic graphs, types matched by name and module.
5. [x] Worker: the cache identity kept per snapshot and carried by the descriptor; the reverse index as a derived slot,
   opened from its sidecar or built on first use, written as a sidecar when the base cache was written, kept in the
   snapshot's memory mode (mapped when it does not fit the budget), build time and memory logged. Tests: built once and
   shared, the sidecar reused on reopen, a stale sidecar (other base) ignored and rebuilt, both memory modes.
6. [x] Contracts and RPC (protocol 7): `QueryReferrersAsync` (an object's referrers: field, address, type, bytes,
   references) and `QueryTypeReferrersAsync` (a type's referrers by type, sortable). Tests: worker tests on synthetic and
   fixture snapshots, unknown address and type.
7. [x] Application and CLI: `SnapshotSession` methods; `heapscope-cli referrers <snapshot> --address | --type [--module]`
   with table, csv and json. Tests: CLI integration on the Small dump and a gcdump.
8. [x] UI: the object view's references as Outgoing and Incoming tabs (incoming loaded when first shown); the instances
   view's "Referenced by" opens a referrer types grid whose rows open that type's objects. Tests: view model tests;
   `--smoke-open` on the Small dump.
9. [x] Known answers: incoming references of LeakyApi leak objects (referrer types per leak) in the integration tests.
10. [x] Measurement: the reverse index on the Medium dump (build, write, reopen from the sidecar, both modes; time and
   memory) recorded in ARCHITECTURE 10.
11. [x] Docs: ARCHITECTURE (6.4, 7, code map, protocol), DEVELOPER, CLAUDE.md.

**Decisions** (Phase 2 autonomy granted by the product owner on 2026-10-24: "go - and yes autonomous - one sprint at a
time", so the sprint stops after its close; each default below was decided autonomously)
1. The reverse CSR keeps one entry per reference, so a referrer holding an object in two fields appears twice; the
   object grid shows each referrer once with a References count and its field paths joined.
2. Incoming rows of a full dump name the referrer's field that holds the object, for up to 1,000 referrers (the array walk
   capped as for outgoing references); beyond that and for gcdumps the column is empty and the totals say why.
3. The per-type grid groups referrers by their type (name and module, ADR-018's key) with referring objects, references,
   targets referenced and referrer bytes; opening a row shows that type's objects (all of them; "only those referencing
   the type" goes to the backlog).
4. Roots are not rows of the incoming grid in 2.1; 2.2 explains roots in plain language. The object summary already says
   when a root holds it.
5. The reverse index is built on first use (an incoming query), not at open. It is written as a sidecar only when the
   base cache was written; it follows the snapshot's memory mode and goes mapped when it would not fit the budget; a
   sidecar naming another base identity is ignored and rebuilt (a rebuilt base cache has a new identity).
6. The descriptor carries the base cache identity (1.2 review); the protocol version goes to 7.
7. Self references count as incoming references.
8. The frozen-objects-alive rule (6.1) applies when reachability is computed (2.2, 2.3); 2.1 computes none.
9. Like Phase 1 sprints, Moderate review findings are decided with their recommendation and logged; the sprint stops and
   reports after `/close-sprint`.

**Review summary** (`docs/reviews/sprint-2.1.md`): 0 High, 11 Moderate, 20 Low after dedupe. Decided autonomously: 9
Moderates fixed now, M4 (label dependent-handle referrers) deferred to 2.2, and M2's build-into-the-sidecar part to 2.3.
Fixed:
- M1: in-memory sidecar sections allocate by element type, so a section holds 2^31 - 1 elements, not 2^31 bytes.
- M2: going mapped maps the sidecar first and keeps the built index if that fails; no compacting collection (the release
  takes 7 to 14 ms).
- M3: the index's mode charges what the worker already commits and honors a forced mode.
- M5: field naming walks at most 2M array elements per query.
- M6: worker tests for every index path, the shared build and the mode choice.
- M7: progress goes to every waiter and the UI shows it.
- M8: an unknown type or sort column is answered before the index is built; the CLI exits 1 on an unknown type.
- M9: results say when the graph is incomplete.
- M10: a reopen refuses a rebuilt cache.
- M11: the release pause, the DenseHeap mapped peak (1,695 MB) and a Release worker run (build 1.45 s, peak commit 2,644 MB
  on DenseHeap) are measured, and the budget claim is qualified (32 GB machine).

Measured: Medium index 470 MB, built in 0.6 to 0.8 s; DenseHeap 831 MB in 0.8 to 1.7 s; reopen from the sidecar 0.1 to
1.1 s. Tests: 792 in the final gate (from 731 at 1.9), all passing; the first gate had one load flake in a worker IPC test.

---

## Sprint 2.2: Retention paths

**Goal**: explain why an object or type is alive.

**Scope**: shortest paths to GC roots, root kinds in plain language, top N distinct paths, framework noise
collapsed, field names resolved on demand for full dumps.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): a merged paths tree from the object toward its roots with a reachable roots count; the top distinct paths presented as key retention paths; plain-language root kinds.

From the 2.1 review (M4, deferred here): dependent-handle edges are ordinary CSR edges, so a `ConditionalWeakTable` key
shows as a plain referrer of its value with an empty field. Record which edges come from dependent handles (a sorted
list of pairs, a loader version bump) so referrers and retention paths label them "(dependent handle)", and check the
label on the ConditionalWeakTable known answer.

**Acceptance criteria**: each LeakyApi leak's path matches its known-answers path shape and root kind.

**Dependencies**: 2.1.

**Task breakdown**
1. [x] Dependent-handle edges recorded: `HeapGraphBuilder` keeps the sorted (primary, secondary) pairs it merged into the
   CSR, `HeapGraph.DependentHandles` and `IsDependentHandleEdge`, a base cache section and format version 2 (older caches
   are rebuilt once). Tests: builder and synthetic graphs, cache round trip in both modes, a version 1 cache refused.
2. [x] Referrers label dependent-handle edges "(dependent handle)" when no field holds the object (2.1 review M4). Tests:
   worker source test; the ConditionalWeakTable known answer in the integration tests.
3. [x] Analysis: plain-language root descriptions (`RootDescriptions`); `RetentionSearch`, a backward breadth-first search
   from one object over the reverse index that stops at retaining roots and frozen objects, finding up to N roots with
   their shortest paths merged into one tree; `RootPathTree`, one forward breadth-first search from every retaining root
   (static fields first) and every frozen object, giving each object its shortest path from a root; `KeyPaths`, a type's
   objects grouped by the type sequence of their shortest path (repeats of one type folded, framework steps marked).
   Tests: chain, diamond, cycle, self loop, unreachable objects, a frozen holder, a static field preferred over the
   storage handle, a dependent-handle step, repeated types, search budgets.
4. [x] Worker and Contracts (protocol 8): `QueryObjectPathsAsync` (the merged tree with fields named on full dumps and the
   key paths among the roots found) and `QueryTypePathsAsync` (key paths with object counts, bytes and an example object,
   plus the unreachable objects); the root path tree as a derived slot with shared progress. Tests: worker tests on
   synthetic snapshots; unknown address and type.
5. [x] Application and CLI: `SnapshotSession` methods; `heapscope-cli paths <snapshot> --address <hex> | --type <name>
   [--module] [--max N] [--format text|json]`. Tests: CLI integration on a full dump and a gcdump.
6. [x] UI: the object view's Retention tab (key paths and the merged tree toward the roots, loaded when first shown) and
   the instances view's "Why alive" subject (a type's key paths, a row opens its example object). Tests: view model tests;
   `--smoke-open` on the Small dump.
7. [x] Known answers: every rooted leak's item path matches its path shape and root kind, including the
   ConditionalWeakTable step "(dependent handle)"; a type's key paths name the leak's root; late garbage is unreachable.
8. [x] Measurement: the root path tree and the key paths on Medium and DenseHeap (time, memory), an object's paths query.
9. [x] Docs: ARCHITECTURE (6.1, 6.4, 7, code map, protocol 8, section 10), DEVELOPER, CLAUDE.md.

**Decisions** (Phase 2 autonomy, one sprint at a time: the product owner said "go" for 2.2 on 2026-10-24; each default
below was decided autonomously)
1. A shortest path counts references. Only retaining roots and frozen objects (never collected, 6.1) start paths; weak
   handles never do. When several roots are equally close, static fields come first, then thread stacks, handles, the
   finalizer queue, other roots and frozen objects, so a static's value is explained by its field and not by the
   runtime's storage handle.
2. An object's paths: up to 10 roots found by the backward search, which visits at most 1,000,000 objects; the result says
   when the search stopped early. A root's own object has a path of length zero.
3. A type's key paths: the 20 largest groups by object count; the rest are summed in one line. Groups use the type
   sequence of the shortest path, with repeats of one type folded (a long linked list is one step) and the root's kind
   (and field, for statics).
4. Framework noise: steps whose type is in a `System.` or `Microsoft.` namespace and lie between two other steps are marked
   as framework steps; the UI and CLI fold a run of them into "(N framework objects)", which expands in the tree. The data
   keeps every step.
5. The root path tree (4 bytes per object) is kept in memory for the snapshot's lifetime, not written to disk in 2.2;
   building it needs one more 4 bytes per object for the queue.
6. Dependent-handle pairs go into the base cache (a section, format version 2). Existing caches are rebuilt once on their
   next open, as when the loader version was bumped.
7. Field names on path steps for full dumps, with the 2.1 budget of array elements per query; gcdumps have none. A step
   through a dependent handle with no field is labeled "(dependent handle)".
8. Unreachable objects (garbage not yet collected in a full dump) get no path and say so; a type's key paths count them on
   their own line.
9. The acceptance check runs on the shared all-leaks LeakyConsole dump, whose leaks are the same `Fixtures.Leaks` code
   LeakyApi runs (ADR-013).
10. As before, Moderate review findings are decided with their recommendation and logged; the sprint stops and reports
    after `/close-sprint`.

**Review summary** (`docs/reviews/sprint-2.2.md`): 3 High, 9 Moderate, 19 Low after dedupe.

All 3 Highs are fixed and confirmed:
- H1: an object's tree had no size or depth limit. The worker now folds runs of one type, cuts long paths around a gap,
  and stops at 2,000 nodes. Segments merge only when identical, and key path steps are capped at 64.
- H2: a search stopped at its limit called a live object unreachable. Reachability now comes from the root path tree,
  the search is pruned to reachable objects, and the tree's path is the fallback.
- H3: the CLI tree printed holders under the wrong parent.

All 9 Moderates were fixed, decided autonomously:
- the root path tree is checked against the memory budget and its queue released;
- one definition of path roots, with a semantics version;
- unreachable explained by snapshot kind;
- worker, root kind, gcdump CLI and view model tests;
- the key paths memo capped and its stack reused;
- cancellation per dequeue;
- measurements re-run.

Measured (32 GB machine), peak in memory and mapped:
- Medium (57.5M objects): root path tree 0.8 to 1.2 s, key paths up to 1.7 s, an object's paths at most 6 ms; peak
  2,802 MB and 1,197 MB.
- DenseHeap: tree 1.6 to 2.3 s; peak 3,320 MB and 1,416 MB.

---

## Sprint 2.3: Dominator tree and retained size

**Goal**: retained size per object and per type.

**Scope**: iterative dominator computation over the CSR, retained sizes, cached section.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): retained bytes columns wherever sizes appear; an "open objects retained by this set" action; a largest retained size tile.

From the 2.1 review (M2, L16, L17): build optional indices straight into their sidecar file (`IndexCacheFileWriter` as
the file-backed column allocator of 6.3) and check the projected peak (graph, reverse CSR, about 28 bytes per object of
working arrays) against the budget before starting, so a mapped computation never holds a managed copy; give each
sidecar layout a semantics version, so a dominators sidecar computed under older rules (retaining roots, frozen objects
alive, the virtual root) is rebuilt like a cache from an older loader.

**Acceptance criteria**: synthetic graphs with known dominators (including cycles and diamonds); known-answer
retained sizes for fixtures; Medium within memory budget in both modes.

**Dependencies**: 2.1.

**Task breakdown**
1. [x] Storage: sidecars record a semantics version (the header's reserved field, written by `Commit`, checked by
   `IndexCacheFile.Open`; a mismatch is refused and rebuilt; base caches and 2.1 sidecars keep 0, so the format version
   stays 2). `MappedIndexFileWriter`: sections planned up front, a temporary file mapped read-write, writable spans per
   section, then checksums, header last, flush and rename (the crash safety of section 7). The dominators layout
   (`.dom.hsidx`: ImmediateDominators, RetainedSizes). Tests: round trip in both modes, a semantics mismatch refused, a
   writer that never commits leaves no file, a crash before the header leaves no valid file.
2. [x] The reverse index is built straight into its sidecar (2.1 review M2): `ReverseReferences.Build` fills given spans;
   with an index cache it is written through `MappedIndexFileWriter` and then opened in its chosen mode, so a mapped index
   never holds a managed copy. Tests: the existing reverse index tests; the sidecar's bytes equal the previous writer's.
3. [x] Analysis: `DominatorTree` (semi-NCA over the CSR and the reverse index, iterative with no stack at all, a virtual root
   joined to `PathRoots.Sources`), retained sizes in reverse preorder, written into spans the caller gives;
   `RetainedByGroup`, the lower bound per type or group. Tests: chain, diamond, cycle, self loop, a root reached only
   through another root, two roots sharing an object, a list held from both ends, a frozen object, a weak handle, a
   dependent-handle edge, unreachable objects, seeded random graphs against a brute-force dominator check, the group
   lower bound on nested objects of one type.
4. [x] Worker and Contracts (protocol 9): the dominators as a derived slot (projected memory checked first, opened from or
   written into the sidecar, shared progress, its own lease); `IncludeRetained` on type summary and instances requests
   (a `Retained` column); `QueryRetainedAsync` (an object's retained size and immediate dominator). Tests: worker tests
   on synthetic snapshots, the sidecar reused on reopen, a stale semantics version rebuilt, the budget refusal.
5. [x] Application and CLI: `SnapshotSession` methods; `--retained` on `summary`, `instances` and `object`. Tests: CLI
   integration on a full dump and a gcdump.
6. [x] UI: a "Retained sizes" switch on the summary and instances views (a column, sortable, computed with progress); the
   object view's retained size and immediate dominator (a link). Tests: view model tests; `--smoke-open` on the Small dump.
7. [x] Known answers: every rooted fixture item's retained size equals the independent ClrMD computation and is at least
   its `minRetainedBytesPerItem`; a leak type's retained lower bound covers its items; late garbage is unreachable.
8. [x] Measurement: Medium and DenseHeap in memory and mapped (time, peak private bytes, sidecar size, reopen).
9. [x] Docs: ARCHITECTURE (6.4, 7, code map, protocol 9, section 10, an ADR for the retention semantics shared by paths
   and dominators, 2.2 review L6), DEVELOPER, CLAUDE.md, the known answers README.

**Decisions** (Phase 2 autonomy, one sprint at a time: the product owner said "continue" for 2.3 on 2026-10-25; each
default below was decided autonomously)
1. Semi-NCA as chosen in 1.9. The virtual root's successors are exactly `PathRoots.Sources` (retaining roots, then frozen
   objects). An object no root reaches has no dominator (-1) and a retained size of 0; an object dominated only by the
   virtual root (a root's own object, or one held by several roots) has -2.
2. Kept per object: the immediate dominator (4 bytes) and the retained size (8 bytes), in the sidecar `.dom.hsidx`. Its
   semantics version combines `PathRoots.SemanticsVersion` and the algorithm's own version, so a sidecar computed under
   older rules is rebuilt.
3. With an index cache, both optional indices are computed straight into their mapped sidecar file and then opened in
   the chosen memory mode; without one they stay in managed arrays.
4. Before computing, the working arrays (13 bytes per object after the review's M4; 21 before) and any managed outputs
   are checked against what is left of the worker's share; when they do not fit, the worker refuses with InsufficientMemory and suggests reopening
   the snapshot mapped. A live in-memory graph is not switched to mapped mid-session (Backlog).
5. Computed on demand, not at open (4 to 13 s on Medium and DenseHeap): when a retained column or value is first asked
   for; later opens reuse the sidecar.
6. A type's (or namespace's, or assembly's) retained size is a lower bound: the retained sizes of its objects not dominated
   by another object of the same group. Shown as such. Exact retention of a set ("objects retained by this set") is 2.4.
7. The dominator tree drill-down and the biggest retainers tile are 2.4; retained deltas in diffs go to the Backlog.
8. gcdumps are computed the same way; an incomplete graph is noted, as for referrers.
9. The known-answer check compares each item's retained size with an independent ClrMD computation on the shared
   all-leaks dump.
10. As before, Moderate review findings are decided with their recommendation and logged; the sprint stops and reports
    after `/close-sprint`.

**Review summary** (`docs/reviews/sprint-2.3.md`): 2 High, 14 Moderate, 21 Low after dedupe.

Both Highs are fixed and confirmed:
- H1: the dominators benchmark deleted the whole `--out` folder; it now deletes only its own sidecars.
- H2: a null retained cell crashed the CLI's JSON output; a null cell is now JSON null, and 4.6 says any cell may be null.

Twelve Moderates were fixed, decided autonomously:
- the budget is checked before the reverse index is built, reserved while computing, re-checked on the fallback, and the
  sidecar opened after the working arrays are collected; a sidecar another process wrote is used (M1);
- one worker-wide `MemoryReservations` for concurrent large computations (M2), and a collection after every one (M3);
- the semidominators and ancestors live in the retained sizes output (13 bytes per object of working arrays instead of
  21; M4), and the path compression reverses pointers instead of keeping a buffer (M5);
- notes beside retained sizes for incomplete snapshots and a gcdump's unrooted survivors (M6);
- requests checked before any dominators (M7); a failed retained query no longer hides the grids' rows (M8);
- worker tests through `DominatorIndexBuilder` (M9), crash-safety tests of the mapped writer (M10), refusal advice that
  fits the snapshot (M11), and an oracle that treats frozen objects as alive, checked on three items per fixture (M12).

Deferred at the close: stopping a computation nobody waits for (M13), the 16 GB measurement (M14), and gcdump
survivors as extra sources (M6's second half). After the close, at the product owner's request: M13 fixed, M14 measured
under a working set cap (a real 16 GB machine stays in the Backlog), M6's second half kept in the Backlog with a revisit
trigger and the note now giving the bytes affected (review file, "After the close").

Measured after the review (32 GB machine): Medium reverse index and dominators 3.9 s in memory (peak 3,289 MB) and 5.6 s
mapped (peak 699 MB); DenseHeap 4.6 s (3,875 MB) and 7.0 s (661 MB). Every rooted fixture item's retained size equals an
independent ClrMD walk.

---

## Sprint 2.3.5: Cross-platform and sharing decision

**Goal**: decide, before 2.4 adds more UI, how HeapScope reaches Linux and macOS developers and how teams share results.

**Scope**: a spike and a decision, not a port. From the product owner (2026-10-25): the users to reach are Linux developers
(possibly macOS) and teams sharing results. Today everything but the WPF UI targets plain `net10.0`; the Windows-only parts
are the UI, capture (process discovery, dump writing, elevation) and a few native calls in Storage and Application. The
engine stays .NET (ClrMD has no equivalent elsewhere); the open question is the front end.
- Run the CLI and worker on Linux against a Linux core dump of LeakyConsole and check the known answers.
- List what capture needs on Linux (createdump, `/proc`) and macOS.
- Compare the UI routes with one view prototyped each: Avalonia (view models carry over, desktop on all three systems)
  and a TypeScript web front end over the worker (also opens a shared or hosted view for teams).
- Sharing results: what a shared result is (an exported analysis, not a dump), how it stays within the privacy rules
  (ARCHITECTURE 12: snapshot contents are sensitive), and how it relates to the HTML report export in the Backlog.

**Acceptance criteria**: an ADR choosing the UI route and the sharing model; Phase 2 re-planned from 2.4 on accordingly.

**Version**: 2.3.5 (between 2.3.0 and 2.4.0).

**Dependencies**: 2.3.

**Task breakdown**
1. [x] Portability survey: every Windows-only call outside the UI (capture's process discovery, elevation and disk space
   checks; the memory status calls, already guarded), the worker's path, the pipe (already current-user only on every
   system), the DAC locator's local-path rules on Linux and macOS. Written into the ADR.
2. [x] CI on GitHub's hosted runners (`.github/workflows/ci.yml`): Windows builds the solution and runs the fast tests;
   Linux (ubuntu-latest) and macOS (macos-latest, informational: an arm64 runner) build the engine, the worker and the
   CLI, run the engine's unit tests, start LeakyConsole, take a core dump of it with `dotnet-dump` (installed on the
   runner only), and run `heapscope-cli summary`, `instances --retained` and `paths` on it. Tests: the workflow passes on
   the sprint PR (Linux required, macOS reported).
3. [x] Small portability fixes the CI run asks for (the worker's executable name off Windows, the DAC locator's path
   rules, capture refusing clearly where it is Windows-only). Tests: unit tests for each; the CI run.
4. [x] Avalonia prototype (`spikes/AvaloniaGrid`, outside `HeapScope.sln`, not shipped): one window that opens a dump
   through the real `SnapshotSession` and shows the types grid and a type's objects with the existing data-virtualized
   row lists, sorting and the filter, on MIT packages only. Measured on the Small dump: first show, scrolling 1.5 million
   rows, the longest UI-thread block. Licenses of every package checked.
5. [x] Decision: ADR-025 (UI platform: Avalonia, amends ADR-001; the rule for Avalonia updates) and ADR-026 (sharing: an
   exported analysis, analysis only by default); Phase 2 re-planned with a port sprint before 2.4.
6. [x] Docs: ARCHITECTURE (ADRs, 12 privacy for exports), DEVELOPER (CI, the prototype), CLAUDE.md (the Avalonia update
   rule), SPRINTS.

**Decisions** (the product owner answered on 2026-10-25; the rest decided autonomously, marked so)
1. The Linux and macOS check runs on GitHub's hosted runners ("github"), not on this machine: no WSL or Docker here.
   It also starts the CI the Backlog asked for.
2. Avalonia packages may be downloaded for a throwaway prototype (MIT, about 50 MB). The web route is not prototyped:
   the product owner chose Avalonia after the comparison ("avalonia", "port before 2.4").
3. Port to Avalonia before 2.4, if the prototype performs and every package used is MIT; otherwise the product owner
   decides again with the prototype's results.
4. Before any Avalonia update, check that nothing HeapScope uses moved to a paid license (Avalonia sells "Accelerate"
   components; TreeDataGrid moved there). Only MIT components are used (product owner, 2026-10-25).
5. A shared result is an exported analysis, analysis only by default: type summaries, diffs, retained sizes, retention
   paths with type and field names; never string text or field values unless the person exporting opts in for that
   export (product owner: "Analysis only").
6. The prototype lives in `spikes/` outside the solution and is deleted after the port sprint replaces it (decided
   autonomously).
7. macOS runs on an arm64 runner while HeapScope targets x64 (ADR-001): its job is informational (continue on error) and
   its findings go into the ADR (decided autonomously).
8. CI is not yet a required check on the protected branches; making it one is the product owner's call once it is
   stable (decided autonomously).

**Review summary** (`docs/reviews/sprint-2.3.5.md`): 2 High, 13 Moderate, 20 Low after dedupe.
- High, both fixed and confirmed: the local path rule (ADR-017) did nothing on Linux and macOS, now `UnixLocalPath`
  (the mount holding the path, links resolved, types read from `/proc/self/mountinfo`); Linux and macOS cores had no
  truncation check, now `CoreFileHeader`.
- Moderate, fixed (decided autonomously): a DAC paired through `libcoreclr` is checked by its version string outside
  installed runtimes; the runtime version is named from the runtime's folder when ClrMD reads 0.0; CI asserts the known
  answers with `jq`; a core's capture time is marked as the file's time; the missing DAC message says where to open a
  dump of another system or architecture; Unix runtime roots; ADR-026 amended; capture refused off Windows is
  `CaptureFailure.NotSupportedOnThisSystem`; section 1, ADR-007, ADR-012 and CLAUDE.md updated; 2.3.6 to 2.6 re-planned.
- Deferred: the grid adapter's enumeration (2.3.6 scope), Compare's baseline with file-time captures, Unix file
  semantics tests and Capture.Tests on Linux, a Windows CI dump step (Backlog).
- Measured: CI on GitHub's runners reads a `dotnet-dump` core of LeakyConsole on Linux (required) and macOS arm64
  (informational) with the known answers: 2,000 objects per leak type, 64,000 bytes and 148 retained per cache entry,
  the path from `StaticCacheLeak.s_cache`, a complete graph, .NET 10.0.14. The Avalonia prototype on the Small dump:
  zero row list enumerations, 197 MB private (540 MB before the adapter), scroll stalls at most 36 ms, first show
  108 to 162 ms (WPF 140 ms). Local gate before the review: 932 tests, 0 failed, 0 warnings.

---

## Sprint 2.3.6: Avalonia port

**Goal**: the HeapScope UI runs on Avalonia on Windows, Linux and macOS, at parity with the WPF UI (ADR-025).

**Scope**: from 2.3.5 (ADR-025):
- Move the view models out of `HeapScope.UI.Wpf` into a platform-neutral UI library; a new `HeapScope.UI` (Avalonia,
  Fluent theme, MIT packages only) hosts the views. Every current view: capture, sessions, the types summary, compare,
  objects and inspection, inspections, monitoring, retention paths and retained sizes, the about box and attribution
  strip (unchanged, ADR-022).
- Grids keep the data-virtualized row lists through the `IDataGridCollectionView` pass-through proven in the prototype;
  no paid Avalonia component (the update rule, ADR-025).
- The render yield, power throttling (ADR-019, Windows only) and the UI smoke and lag gates move to the Avalonia app;
  UI tests move to Avalonia's headless test platform.
- CI: Linux and macOS also build the UI and run its headless tests.
- Layering: the view model library and the Avalonia UI project get their rows in ARCHITECTURE 3.1 and `LayerRules` (the
  architecture test refuses unknown projects).
- From the 2.3.5 review: the grid adapter's enumeration and `CopyTo` pass through to the row list, which makes a
  placeholder row per index; select all, copy and keyboard paths that enumerate must go through indices or the worker,
  and a probe test (as `RowListProbe` does) proves no grid path enumerates 1.5 million rows. The render yield moves
  into `IUiDispatcher` (Wpf, Avalonia, Immediate) instead of the static `TaskSchedulerExtensions.RenderYield`. Front
  ends ask `ICaptureService` whether capture is available (with the reason) before showing capture views.
- Capture stays Windows only (the views say so off Windows and point to opening a dump taken with `dotnet-dump`).
- Retire `HeapScope.UI.Wpf` and `spikes/AvaloniaGrid` once parity is reached; update THIRD-PARTY-NOTICES.

Added by the product owner before the start (2026-10-26, "add 1-7"):
1. Runnable builds per system: self-contained builds for `win-x64`, `linux-x64`, `osx-arm64` and `osx-x64` (the UI, the
   worker and the CLI), produced by CI as downloadable artifacts. macOS signing and notarization stay in the Backlog.
2. Owner-only files off Windows (2.3.5 review L5): sessions, logs, index caches and sidecars are created readable by the
   user only (files 0600, folders 0700); Windows keeps the profile's ACL.
3. Light and dark themes in Avalonia (the WPF themes' keys and colors), following the system unless `Ui:Theme` says.
4. The first-window time is measured in the smoke gate (the Backlog's cold start item gets an Avalonia number).
5. Compare's default baseline when a capture time is only the file's time (2.3.5 review M4): the snapshot opened first
   is the baseline, and the view says why.
6. A Windows CI step that captures LeakyConsole with the CLI and checks the known answers (2.3.5 review M3).
7. 2.3.5 review cleanups: L2 (one module key in `TypeGroups`), L11 (`ProcessName` through `RecordedPath`), L13 (the
   socket bound), L14 (one public type per file, guard order, `using`), L16 (attach pipe names checked for length).

**Acceptance criteria**: every WPF view and command has its Avalonia equivalent; the smoke gate (first show, scroll, the
longest UI-thread block) meets the WPF numbers on the Small dump on Windows; the UI opens a Linux core dump of
LeakyConsole on the Linux runner (headless) with the known answers; only MIT (or otherwise allowed) packages.

**Version**: 2.3.6.

**Dependencies**: 2.3.5.

**Task breakdown**
1. [x] Groundwork: Avalonia packages in `Directory.Packages.props` (Avalonia, Desktop, Themes.Fluent and DataGrid as in
   the prototype; Avalonia.Headless for tests and the headless smoke run), all MIT and checked by the update rule;
   Avalonia's build telemetry turned off for the repository (an empty `AvaloniaStats` target in
   `Directory.Build.targets`, and `AVALONIA_TELEMETRY_OPTOUT=1` in CI); THIRD-PARTY-NOTICES. Tests: the build log shows
   no `AvaloniaStats` task; a repository test checks the override exists.
2. [x] `HeapScope.UI.ViewModels` (net10.0): the view models, `IUiDispatcher` (with `YieldToRenderAsync`, replacing the
   static `RenderYield`), `IUserInteraction` made asynchronous (Avalonia's dialogs are), the immediate and automatic
   implementations; `ICaptureService.UnavailableReason` (null when capture works, else the reason) used by the Processes
   and Live view models.
   `HeapScope.UI.Wpf.Tests` becomes `HeapScope.UI.ViewModels.Tests` (net10.0, runs on Linux and macOS). The WPF app uses
   the library until it is retired. ARCHITECTURE 3.1 and `LayerRules` get the new rows. Tests: the moved view model
   tests; capture availability; the architecture test.
3. [x] `HeapScope.UI` (Avalonia, `HeapScope` executable): `Program`, `App` (host start and bounded stop, last-chance
   handlers, power throttling on Windows per ADR-019), `UiHost`, the services (dispatcher, dialogs, lag watchdog), light
   and dark themes, and the shell window: navigation, worker crash banner, jobs, status bar, back and forward (mouse
   buttons, Alt+arrows) and the attribution strip with the same text, link, place, surface and contrast (ADR-022).
   Tests (`HeapScope.UI.Tests`, headless): the window builds with no binding errors; the attribution checks move to
   the Avalonia markup; both themes define the same keys.
4. [x] Grids: the `IDataGridCollectionView` pass-through (from the prototype), through which no grid path enumerates
   or copies the list (a walk would still make a placeholder row per index, so walks are counted and every smoke run
   fails on one, review M3), worker-side sorting with header arrows, open by double-click or Enter, highlighted filter matches, columns from a result set's column list, column visibility from
   the view model, the heap kinds bar. Whole-list walks are counted (`VirtualCollectionViews.Enumerations`) rather than
   failed, so the tests can prove there are none without breaking the app if a grid path ever does one. Tests
   (headless, 1.5 million rows): first show, scrolling, select all, copy and keyboard moves enumerate nothing; a header
   click sends the column to the sort command.
5. [x] Pages: Home, Processes (capture availability and the `dotnet-dump` hint off Windows), Snapshots (the snapshot
   tab with types, instances, object, referrers, paths and retained sizes; compare; inspections), Live (the timeline
   chart), Jobs, About. Tests: each page builds headless with fake view models and no binding errors; the WPF commands
   and views listed against their Avalonia equivalents in the review file.
6. [x] Smoke and gates: the smoke runs (`--smoke-synthetic`, `--smoke-open`, `--smoke-compare`, `--smoke-trace`,
   `--smoke-live`) and the dispatcher lag checks on Avalonia; `--headless` runs them without a display; the first-window
   time is logged and gated; `build/gate.ps1` runs the Avalonia smoke. Measured on the Small dump against the WPF
   numbers (first show, scroll, the longest UI-thread block). Tests: the gate's smoke steps.
7. [x] Compare's baseline with file-time captures (added item 5). Tests: the compare view model picks the first opened
   and says why.
8. [x] Owner-only files off Windows (added item 2): one helper creates HeapScope's folders and files with user-only
   permissions (sessions, logs, index caches, sidecars, partial dumps). Tests: run on Linux and macOS in CI (skipped on
   Windows).
9. [x] Builds per system (added item 1): `dotnet publish` self-contained per runtime identifier with the worker and the
   CLI; builds are AnyCPU and a published build takes its runtime identifier's architecture (decision 10); a CI job
   builds the four, runs the three it can and uploads them as artifacts. Tests: the CI job; the Windows build passes the smoke gate.
10. [x] CI: Linux and macOS build the UI and run the UI tests; Linux runs the headless smoke on the LeakyConsole core
    dump; Windows captures LeakyConsole with the CLI and checks the known answers (added item 6).
11. [x] Review cleanups (added item 7): L2, L11, L13, L14, L16 of the 2.3.5 review, each with its test where one applies.
12. [x] Retire `HeapScope.UI.Wpf`, its tests and `spikes/AvaloniaGrid`; docs: ARCHITECTURE (3.1, code map, section 8
    threading, ADR-019 and ADR-025 amended), DEVELOPER (run, smoke, publish), CLAUDE.md, THIRD-PARTY-NOTICES, SPRINTS.

**Decisions** (decided autonomously from the recommended defaults unless marked otherwise)
1. Two projects: `HeapScope.UI.ViewModels` (platform neutral, CommunityToolkit.Mvvm) and `HeapScope.UI` (Avalonia,
   executable `HeapScope`); the tests split the same way. Namespaces drop `Wpf`.
2. The WPF app is deleted in this sprint once the Avalonia app reaches parity; the two are not shipped side by side.
3. Dialogs become asynchronous (`IUserInteraction`): Avalonia has no blocking dialogs; a small confirm window replaces
   `MessageBox`.
4. The theme follows the system (Avalonia's default variant) unless `Ui:Theme` is Light or Dark, as in WPF.
5. Avalonia's build telemetry is turned off for the repository: HeapScope sends nothing anywhere (ARCHITECTURE 12), and
   this extends it to its build. It ran during the 2.3.5 prototype builds before this was noticed (reported to the
   product owner).
6. Avalonia.Headless (MIT) is added for tests and for `--headless` smoke runs in CI; covered by the product owner's go
   on this scope ("UI tests move to Avalonia's headless test platform"). Its download is the only new one.
7. Builds are self-contained and not single-file (the worker is a separate executable), ReadyToRun on; CI uploads them
   on pushes to `develop` and `main` and on manual runs, kept 14 days; never anything but build output.
8. The smoke gate keeps its limits (the dispatcher budget, twice for the smoke). The first-window time is logged by every
   smoke run and fails it only above 30 s (a start that hangs): measured 2.2 s warm for a Release build here, the same as
   WPF, and 8 to 15 s for the first launch after a build while its files are scanned, so a tight limit would be noise
   (first planned at 2 s; revised with the measurement, decided autonomously).
9. Owner-only permissions apply to files HeapScope creates; a dump written by the runtime (Linux capture, later) and a
   `--out` folder the user chose keep their own permissions.
10. Builds are AnyCPU instead of x64 (found by CI: the x64 test assemblies could not load on the arm64 macOS runner, and
    an osx-arm64 publish mixed arm64 and x64 assemblies): each process runs as its machine's 64-bit architecture, and a
    published build takes its runtime identifier's (decided autonomously; ADR-001 and ADR-025 amended).
11. Headless smoke runs (CI) check that every step works and log the UI lags without gating them: on a two-core runner
    drawing in software the lag measures the machine, not HeapScope (decided autonomously; the Windows smoke gate is
    unchanged).
12. Avalonia's dispatcher has no operation hooks, so the longest UI operation is bounded, not timed: smoke runs probe
    every 10 ms and check an upper bound (the highest-priority probe's wait plus the longest gap between probe ticks)
    against the 100 ms budget (sprint 2.3.6 review, decided autonomously).
13. `Avalonia.Fonts.Inter` (a dependency of the headless package, its font under the SIL Open Font License) is kept out
    of the app's build output: the app uses the system font and the headless runs need no font of their own (decided
    autonomously).


**Review summary**: 1 High, 15 Moderate, 30 Low (docs/reviews/sprint-2.3.6.md). The High (the Linux and macOS builds
lost their executable modes in the artifact upload, and the package job never ran what it built) was fixed with
tarballs that CI unpacks and runs (CLI version, a worker ping, the worker's Server GC, a headless smoke), and confirmed
by solution-architect after a second pass (relative tar names for Git Bash on Windows). Moderates, decided
autonomously: 14 fixed (space reserved before mapped writes; a sound upper bound for the longest UI operation, decision
12; smoke runs fail on any whole-list grid walk and log UI memory; one module key for summary, diff and inspections;
the self-contained worker's own DAC folder instead of a folder beside it; the architecture check before the DAC
search; the worker's GC settings logged and section 9 corrected (DATAS is off with a heap count); a guarded window
close; attribution and message window tests with a hit-test fix; the grid dispatcher required; ReadyToRun for every
published executable; the WPF parity table; TreeView limits documented and the 2.4 plan corrected; the Inter font kept
out of the app, decision 13), 1 deferred to 2.4 (the grid adapter keyed by an interface). Lows listed. Measured
(Release, this machine): Small dump checked lag 102 to 105 ms and longest operation at most 64 ms, Medium 71 ms and at
most 69 ms (WPF, timed exactly: 44 to 68 ms); no whole-list grid walk; UI 219 to 229 MB private; published first window
1.0 to 1.3 s; builds (CI) 266 to 370 MB unpacked, 112 to 139 MB as tarballs, each run from its unpacked tarball
(worker ping, headless smoke) except `osx-x64`. Gate: 993 tests, 0 failed, 0 warnings (an earlier run hit the known
SessionLibrary concurrency flake under load; it passed 5 of 5 alone). CI green on all jobs.

---

## Sprint 2.3.7: Port peer review

**Goal**: a second, wider review of the Avalonia port (2.3.6 changed 173 files) from three perspectives, solution
architect, developer and support or system analyst, looking for refactoring items and for logging that can back up
every result HeapScope shows.

**Scope** (product owner, 2026-10-26: "Really look at this for refactor items and also assure logging telemetry is
solid. If someone contests a finding, we'll need logging to back up why result was proposed."):
- Review the port's change set (`sprint-2.3.5..sprint-2.3.6`) and the code it touches for refactoring: duplication,
  misplaced responsibilities, seams 2.4 will need, dead code from the WPF era.
- Result provenance in the logs: when a user contests a result (an inspection finding, a retained size, a retention
  path, a diff row, a growth alert, a refused capability, a DAC choice, a cache reuse), the local log shows why it was
  produced: the snapshot's identity and loader facts, versions, the rule and its thresholds, counts, timings, with ids
  that tie the UI, the CLI and the worker lines together. Never dump contents (ARCHITECTURE 12): no field values or
  strings. "Telemetry" here means local diagnostics only; HeapScope sends nothing anywhere.
- A support perspective: can someone diagnose a user's problem from the log folder alone (where logs are, how long they
  are kept, what a failure looks like, what is missing).
- Fix every High; Moderates follow the lead engineer's recommendation (product owner: "Go with your reco for moderate
  recommendations").

**Acceptance criteria**: docs/reviews/sprint-2.3.7.md with the three perspectives; every High fixed and confirmed;
every Moderate decided; a test or a logged run shows each fixed provenance gap; docs describe the logging that exists.

**Version**: 2.3.7.

**Dependencies**: 2.3.6.

**Task breakdown** (from docs/reviews/sprint-2.3.7.md; the review ran first, as this sprint's purpose)
1. [x] Log files per process (H1, M17): `heapscope-yyyyMMdd-<app>-<pid>.log`, each starting with a header (product and
   informational version, app, pid, system, architecture, runtime, self-contained or not, log folder); lines in ISO 8601
   with offset; scopes rendered; files kept 14 days; a file rolls at 50 MB; `Logging:File:MinimumLevel` (also passed
   to the worker). Tests: two providers on one folder keep every line; old files removed; the roll; scopes; the level.
2. [x] Snapshot identity (H2): one structured line when a snapshot opens (id, path, size, fingerprint, kind,
   capabilities, facts, runtime, capture time and its source, loader and cache writer versions, cache path and identity,
   from cache or rebuilt or reopened, memory mode, notes, load timings, loader diagnostics); incomplete segments and
   static roots kept in the cache metadata (optional fields); the client logs its handle with the worker id on open,
   reopen and a changed-file refusal. Tests: the line's fields on a fixture open; the metadata round trip.
3. [x] Result provenance (H3, M5): one structured line per answered query at the worker's RPC boundary (operation,
   snapshot ids, parameters without filter text, versions and thresholds, outcome counts and flags, elapsed);
   inspections get a version and parameters; duplicate strings count unreadable strings and say unreachable strings are
   included; the Compare view model logs how the pair was chosen. Tests: a captured logger sees each result line; the
   duplicate strings note.
4. [x] Who ran what (H4, M20): `ProductInfo.InformationalVersion` (with the commit); a start line for the UI, the CLI
   (with its command and options, never values) and the worker (protocol, system, architecture, runtime, path, rule and
   format versions); `JobTracker` logs each job's start and end with id, title, elapsed, status and error code; the CLI
   logs its exit code; worker start failures and the errors shown to the user are logged. Tests: job lines; CLI exit
   line.
5. [x] Correlation (M1, M20): an `Activity` per job, CLI command and grid query; StreamJsonRpc's activity tracing on both
   ends so worker lines carry the client's trace id; scopes in the worker's console log; forwarded worker lines prefixed
   with the worker pid and generation; result set re-runs logged. Tests: a worker line carries the client's trace id.
6. [x] Errors and refusals (M2): expected refusals logged at Information without a stack, with operation, snapshot and
   reason; errors created as codes logged; inspection availability reasons logged; the CLI console shows worker errors
   only; ARCHITECTURE 4.3 and 11 corrected. Tests: refusal logged without stack.
7. [x] DAC choice (M3): `DacChoice` (path, rule, folders searched, downloaded) returned through the loader's diagnostics
   and the reader, logged with the identity line, kept in the cache metadata; each rejected candidate's reason in the
   failure message; a download logged as a warning. Tests: the rule and the rejection reasons.
8. [x] Monitoring and the growth gate (M4): run start and end lines with the rules; a fire line with metric, source,
   baseline, threshold, step and rearm; the same in the timeline marker and the session record; the CLI gate decision
   logged. Tests: the rule decision; the gate line.
9. [x] Log content policy (M6): ARCHITECTURE 11 says what logs may hold (paths, names, counts, addresses; never values or
   strings) and that logs are local evidence, not for publishing; the instances filter text kept out of logged messages.
   Test: a refused filter's log line has no text.
10. [x] Instances filter drops (M10): unreadable strings and regex timeouts counted, returned as a note and logged.
    Tests: the counts.
11. [x] Smoke runner (M11): shared measuring and step helpers, the failed step named in the log, `ILogger<SmokeRunner>`,
    the CI-matched lines marked as a contract with event ids; the operation bound and the exit-code verdict extracted and
    unit tested.
12. [x] Shared patterns (M12, M15): a `WorkerException` base, `ResultSetHandle.ReleaseInBackground`, a section loader for
    `ObjectViewModel`; one `AddHeapScopeViewModels` used by the app and the tests.
13. [x] Support docs (M16): the log folder per system in README and DEVELOPER; the About page shows the log folder,
    versions and runtime, with a copy action; messages name the actual log folder.
14. [x] Early and fatal failures (M18, M19): `Main` logs failures before the host exists; Avalonia's warnings reach the
    log without message text; worker exit codes explained (out of memory kill, abort, unhandled exception); a worker
    last-chance line.
15. [x] Docs: ARCHITECTURE 11 (logging and provenance, the policy), 4.3, 12; DEVELOPER (logs, support); CLAUDE.md;
    SPRINTS 2.4 gets the deferred items (M7, M8, M9, M13, M14, typed fake rows).

**Decisions** (decided autonomously from the recommendations)
1. One log file per process rather than shared appends: no cross-process locking, and each file says who wrote it.
2. Provenance is logged now; a provenance record on the result DTOs (protocol 11) waits for the shared analysis export
   (ADR-026), which needs the same record.
3. Correlation uses W3C trace ids through StreamJsonRpc's activity tracing, so the protocol does not change.

**Review summary**: 4 High, 20 Moderate, 27 Low (docs/reviews/sprint-2.3.7.md; three perspectives: solution-architect,
senior-dev, support and system analyst). The Highs were all logging: processes overwrote each other's log lines, a
snapshot's identity was never logged, no result was ever logged, and there was no trail of versions, jobs or shown
errors. All four fixed and confirmed by the reviewers that raised them (two gaps from the confirmations fixed too:
retention path roots in the log, and host build failures). Moderates, decided autonomously: 14 fixed (trace ids from the
UI or CLI to the worker, refusals logged without stacks, the DAC choice with every candidate's outcome, monitoring rule
decisions and the CLI growth gate, rule versions and parameters, a log content policy, instance filter drops, the smoke
runner's steps and tested verdicts, a worker exception base and shared release and section helpers, one view model
wiring, support docs and the About page, early UI failures and Avalonia's warnings, worker exit codes, job and re-run
lines), 6 deferred to 2.4 (navigation targets, the dominator tree's worker pieces, exactness on the wire, column groups,
shared view controls, typed fake rows). Also found: the session lock's 10 second deadline caused the long-standing
SessionLibrary gate flake (now 30 s). Protocol 11. Measured (Release, Small dump smoke): longest UI operation at most 63
to 64 ms, lag 101 to 115 ms, UI 227 to 233 MB, about 80 log lines per smoke run. Gate: 1,025 tests, 0 failed, 0
warnings; CI green on all jobs.

---

## Sprint 2.4: Retention explorer UI

**Goal**: explore what retains memory.

**Scope**: drill-down dominator tree, biggest retainers, grouping by type and namespace, cross-links between
summary, diff, inspection and retention views.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): a dominator tree with tree and path modes plus an icicle chart (simpler than a sunburst, same data); click highlights a path, double-click zooms; a biggest retainers tile.

From 2.3.5 (ADR-025): built in Avalonia after the port (2.3.6). The tree is built on the free controls (a flattened,
data-virtualized row list with indentation in the DataGrid), not the paid TreeDataGrid. Avalonia's `TreeView` is not
virtualized and stays for small, capped trees only (2.3.6 review). A grid whose column set or row count changes on a
re-query gets an adapter keyed by an interface rather than by row type (2.3.6 review).

From the 2.3.7 peer review (deferred here, decided autonomously):
- M8 first: one navigation target (snapshot, type key with scope, object, retention node, diff row) and one
  `Navigate(target)` on the Snapshots view model that picks the tab, a subject interface with a typed trail (no more
  `List<object>` and reflection binding), tab enums instead of numbers, and the navigator passed to grids by
  constructor instead of settable commands; `SnapshotTab.axaml` split into a view per subject.
- M9: decide at the start, as a decision or ADR, how the dominator tree's children are found (a children section in the
  dominators sidecar, sorted by retained size, or a measured scan per expand), the request shape (expanded node ids;
  `NodeId`, `ParentId` and `Depth` columns) and the updated memory budget (ARCHITECTURE 6.4).
- M7: whether a value is exact or a lower bound travels on the wire (a column or result flag), with caveat codes whose
  texts live in Contracts; the CLI and the view models render from that one source.
- M13: column groups as one `VisibleColumnGroups` behavior and a generic cell row, with the grid adapter work above.
- M14: a note text control and a type grid toolbar control (grouping, filter, totals), used by the types grid,
  Compare and the dominator grid.
- M15 rest: typed fake rows keyed by the Contracts column names in the view model test fakes.

From 2.3 (decisions 6 and 7): the exact retention of a set ("open objects retained by this set": a reachability pass from
the roots that skips the set, since a group's retained column is only a lower bound), the biggest retainers tile and the
dominator tree drill-down build on the dominators sidecar (`Dominators`, `GroupRetention`).

**Acceptance criteria**: every LeakyApi leak is reachable from "biggest retainers" in three clicks or fewer.

**Dependencies**: 2.2, 2.3, 2.3.6.

**Task breakdown**
1. [x] Navigation first (2.3.7 M8): a `NavigationTarget` (snapshot, type key with scope, object, dominator node, diff
   row) and one `Navigate(target)` on the Snapshots view model that picks the tab; `INavigationSubject` with a typed trail
   (no `List<object>`, no reflection binding); `SnapshotTabs` and `ReferenceTabs` enums instead of numbers; the navigator
   passed to grid view models by constructor; `SnapshotTab.axaml` split into a view per subject. Tests: the existing
   navigator and view tests; a navigation target per kind lands on its tab and subject.
2. [x] Dominator children (2.3.7 M9, ADR-027): the dominator tree's children as a CSR (index order since the review, H5)
   (`DominatorTree.BuildChildren`), stored as two more sections of the dominators sidecar (semantics version 2, so older
   sidecars rebuild once) and in memory without one; kept bytes 12 to 20 per object; ARCHITECTURE 6.4 updated. Tests:
   children order and coverage on synthetic graphs; sidecar round trip; an old sidecar is refused.
3. [x] Dominator tree query (worker, protocol 12): a flattened, paged tree from the virtual root or a focus object,
   expanded nodes by key, children as objects or grouped by type or namespace (siblings' retained sizes are disjoint, so
   group sums are exact), at most 100 children per node plus a "more" row, rows with key, parent key, depth, kind, name,
   module, address, objects, retained, shallow bytes, share of the heap and child count. Biggest retainers are the root
   level. Tests: flattening, grouping sums, the "more" row, keys stable across re-runs.
4. [x] Exactness on the wire (2.3.7 M7): `ResultColumn.Bound` (exact or lower bound) set by the worker, with the
   texts in Contracts; the CLI and the view models render from it. Tests: the type summary's retained column is a lower
   bound, the instances' and the tree's exact.
5. [x] Exact retention of a set: "objects retained by this set" for a type (or a scope) by a reachability pass from the
   roots that skips the set; totals (exact) next to the group's lower bound, and the retained objects by type as a grid.
   Tests: synthetic graphs with shared and exclusive children; the known answers' leaks.
6. [x] Icicle chart query and control: the top levels below a focus with segments of at least 0.1 percent; click
   selects and highlights the path in the tree, double-click focuses. Tests: layout widths and the threshold; the control
   builds headless.
7. [x] Retainers view (UI): a Retainers tab with the biggest retainers, the dominator tree grid (indentation, expand and
   collapse with a click, Enter, Right and Left keys), grouping and the icicle; cross-links: type rows, diff rows and
   inspection rows open "show in dominator tree" and "objects retained by this set"; a tree node opens its object,
   its type's instances and its retention paths. Grid pieces from 2.3.7 M13 and M14: `VisibleColumnGroups`, a generic
   cell row, a note text control and a type grid toolbar. Tests: headless views bind cleanly; view model tests for
   expand, group, focus and the links.
8. [x] CLI: `retainers <dump>` (biggest retainers, `--group`, `--depth`, `--top`, `--format table|json|csv`) and
   `retained-by <dump> <type>` (exact set retention). Tests: CLI integration on the fixture dump.
9. [x] Acceptance and smoke: an integration test proves every LeakyConsole leak in the known answers is reachable from
   the biggest retainers in three clicks or fewer (open the row, expand, see the growing collection or its items); the
   smoke drill opens the Retainers tab, expands and focuses, checked against the budget. Typed fake rows in the view
   model tests (2.3.7 M15 rest).
10. [x] Docs: ARCHITECTURE (6.4, 4.4 protocol 12, the retention explorer, ADR-027), DEVELOPER (CLI commands), README,
    CLAUDE.md, SPRINTS.

**Decisions** (decided autonomously from the recommended defaults)
1. The children of the dominator tree are stored, not scanned per expand: a scan reads every object's dominator
   (57.5 million on Medium) per click; stored children cost 8 bytes per object once and make an expand a slice read
   (the review's H5: kept in index order; a query orders only the rows it shows).
   They live in the dominators sidecar (new sections, semantics version 2), so existing sidecars are rebuilt once.
2. Tree rows are keyed by address (objects) and by parent key plus group name (groups), so a re-run after a worker
   restart keeps the expansion; at most 100 children per node with a "more" row that expands by 100, and at most
   50,000 rows per answer.
3. Biggest retainers are the top-level dominators (the virtual root's children), by object or grouped by type; static
   field values are top-level dominators, so each leak's collection appears there directly.
4. Group rows sum their members' retained sizes exactly: siblings in the dominator tree retain disjoint sets.
5. Exact set retention is computed per request (a reachability pass, one bit per object plus a stack), never stored.
6. The icicle chart replaces the sunburst (research doc 7); it shows six levels below the focus by default.
7. Exactness travels as a column attribute (`ResultColumn.Bound`), not per row.
8. Protocol 12.
9. The Retainers view gets its own tab on the snapshot, next to Types, Compare and Inspections.
10. During the build (decided autonomously): set retention takes a type (name and module); a namespace or assembly
    scope is a Backlog item. "Show in dominator tree" from a type row opens the tree grouped by type with the type's
    top-level group open; from an object it focuses on the object, its dominators as a trail. The links are in a
    right-click menu on the types, Compare and inspection grids and in buttons on the instances and object views.
11. The tree is re-queried on every expansion (the worker keeps no view state; the keys are the state), and the client
    reads its rows by column name.
12. 2.3.7 M13 and M14: column groups are named by the view model (`VisibleColumnGroups`), one row type serves every
    result set grid (`CellRowItem`), and status and note lines are one `NoteText` control (21 copies replaced). The type
    grid toolbar was not extracted: the Retainers tab has no type filter, so no third copy appeared (Backlog).
13. 2.3.7 M15 rest: the new fakes are typed (`FakeTreeNode`, the set's rows); the older positional fake rows stay
    (Backlog).
14. The README has no command list (it points to `--help`), so it needed no change.

**Review summary** (`docs/reviews/sprint-2.4.md`): four reviewers (solution-architect, senior-dev, gc-specialist, and
a support and system analyst). 5 High, 17 Moderate, 33 Low after dedupe (the re-review of the fixes added 2 Moderates).
- High, all fixed and confirmed: set retention reserves its memory and uses a chunked stack (H1); the focus trail keeps
  the top 3 and nearest 20 dominators (H2); the toggled or linked row stays selected and in view in the new rows (H3,
  `RevealKey` and `RevealIndex`, `OnRowsApplied`, `ScrollToSelection`); the acceptance test also runs on a live LeakyApi
  dump (H4); the tree's children are kept in index order and only shown rows are ordered (H5: Medium's dominators 13.4
  to 6.4 s, peak 3,747 to 3,291 MB).
- Moderate (decided autonomously): 15 fixed now (the re-review's two included: grouped trees without hashing or
  delegates per object, set retention's peak measured): a message when a type has no top-level group; Contracts owns the
  tree's kinds, keys and limits; texts from the column's bound and one name for the set action; grouped trees cost what
  they show; published limits and CLI notes in every format; re-measured docs; provenance of what the user looked at
  and of each action; reachable versus all members; an unreachable focus's next step; a depth limit; set retention
  checked against ClrMD; a bar with no loaded row; scoped inspection rows; test and fake fidelity. Deferred: a
  `ValueBound.Approximate` and caveat codes, one type entry among several of a name (Backlog).
- Measured (Release, 32 GB machine): Medium dominators with the children 6.4 to 6.9 s in memory (peak 3,291 MB), 11.4 s
  mapped (697 MB), 1,097 MB kept; DenseHeap 7.3 s in memory (3,877 MB), 10.6 s mapped (658 MB); set retention of 3.2M
  objects 3.3 s through the CLI, peak 3,300 MB for 16M objects in the benchmark (DenseHeap 3,876 MB); a tree query 0.03 s; the UI smoke run's Retainers drill warm: lag 50 ms, longest operation 32 ms.
- Both acceptance runs: every leak of LeakyConsole and of LeakyApi is at most three expansions from the biggest
  retainers (three need none, the unbounded dictionary one, the event handlers three).

---

## Sprint 2.5: Allocation tracking

**Goal**: show where allocations come from.

**Scope**: EventPipe allocation sampling with call stacks, symbol resolution, top call stacks and types over a
time range.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): select a timeline interval, then types with back traces below and a call tree with total and own columns.

From 1.5 (product owner): built on the trace recorder added in 1.5 (no dotnet-trace install), with trace reading
(TraceEvent) moving from the developer tool into the product. Recorded between two snapshots, the allocations give the
diff the "new since the baseline" view that dumps alone cannot (ADR-018).

**Acceptance criteria**: LeakyApi's leaking allocation site appears in the top call stacks under LoadGen.

**Dependencies**: 1.7, 2.3.6 (the UI is built in Avalonia, ADR-025). Platform scope: EventPipe allocation sampling works on
Linux and macOS too, but the trace recorder attaches through capture's process discovery, Windows only until the Linux
capture item; the sprint says which systems it covers.

**Decisions** (all decided autonomously at /start-sprint, 2026-10-26; Phase 2 autonomy, one sprint at a time):
1. Source: the runtime's `GCAllocationTick` events (one about every 100 KB allocated, with the type name, the bytes
   since the last one and a call stack), recorded by a new `Allocations` trace profile. It works on .NET 6 and later
   with no agent. Sizes are estimates: each sample stands for the bytes allocated since the previous one, so columns
   say "sampled" and the CLI and UI say so once. .NET 10's randomized allocation sampling goes to the Backlog.
2. The worker reads the trace (ADR-007: heavy data stays out of the front end), through a new loader project
   `HeapScope.Loaders.Trace` (Core plus TraceEvent, like the other loaders). The model (`AllocationTrace`: frames, a
   deduplicated stack tree, types, and per-sample time, stack, type and bytes) lives in Core; the aggregations in
   Analysis. Protocol 13. A new ADR-028 records it.
3. TraceEvent needs an `.etlx` file to resolve stacks: the worker writes it to a private temporary folder and deletes it
   after reading; nothing is written next to the trace.
4. Managed frames are named from the trace's own rundown (no symbol downloads, ADR-017); runs of unnamed native frames
   collapse into one `[native code]` frame.
5. Views (an Allocations page, one tab per opened trace): a timeline of sampled bytes per second where dragging selects
   the interval; a Types grid; Back traces of the selected type (the allocating method, then its callers); a Call tree
   (top-down) with Total and Own; Top call stacks. Trees are served flattened from the open keys like ADR-027's tree
   (children by Total, 100 per page and a "more" row, a row limit).
6. Recording from the UI: the Processes page gets "Record allocations" (30 s by default, stoppable), saved into the
   session folder and opened when done; any `.nettrace` opens from the Allocations page's Open trace (its own picker,
   so the dump picker stays for dumps). The CLI's `trace` takes
   `--profile allocations`; a new `allocations <file>` command prints the top types and top call stacks of an interval.
7. Linking a trace to the Compare tab ("allocated between the two snapshots") needs the trace and the snapshots on one
   clock in the session record: Backlog, next to the 1.7 timeline item. Recording from the Live page goes with it.
8. Platforms: reading and every view work on Windows, Linux and macOS; recording from the UI uses process discovery
   (Windows only until the Linux capture item); the CLI records wherever the diagnostics port answers.
9. Logs (ARCHITECTURE 11): each query logs its interval, filters and the rows it answered with (type and method names,
   counts and bytes), so a disputed stack is traced from the UI action to the worker's answer; never event payloads
   beyond type names and sizes.

**Task breakdown**:
1. [x] Recording: `TraceProfile.Allocations` and `TraceKind.Allocations` (GC events at verbose with loader and JIT
   events, rundown on stop); CLI `trace --profile allocations`. Tests: providers unit test; an allocations trace of
   LeakyApi holds allocation ticks with stacks (integration).
2. [x] Model and reader: Core `AllocationTrace`; `HeapScope.Loaders.Trace` `AllocationTraceReader` (private `.etlx`
   deleted after, native runs collapsed, progress and cancellation, bad files refused with a plain message); layering
   rules updated. Tests: reader on a recorded trace, the `.etlx` is gone, a non-trace file is refused, architecture.
3. [x] Analysis: `AllocationProfile` (timeline buckets, types over an interval, top stacks, call tree and back-trace
   rows with paging and limits). Tests: synthetic traces with known answers.
4. [x] Contracts and worker: protocol 13; open, close, timeline, types, stacks and tree queries; memory reservation;
   provenance lines. Tests: worker tests, contract mirror.
5. [x] Application and CLI: the trace handle in the session; recording into the session folder; `allocations`
   command. Tests: application tests, CLI integration test.
6. [x] UI: the Allocations page (timeline with interval selection, Types, Back traces, Call tree, Top call stacks),
   Record allocations on the Processes page, `.nettrace` from the page's Open trace, a smoke step. Tests: view model and view
   tests, the smoke run.
7. [x] Acceptance and measurements: LeakyApi under LoadGen, its leaking allocation site in the top call stacks
   (integration); reading time and peak for a long trace. Docs: ARCHITECTURE (code map, 4.4, ADR-028, 10, 11, 12),
   DEVELOPER, CLAUDE.md.

**Review summary** (docs/reviews/sprint-2.5.md): four reviewers (solution-architect, senior-dev, gc-specialist, and a
support and system analyst). 4 High, 25 Moderate, 28 Low after dedupe; the re-review of the fixes added 3 Moderates and
5 Lows, all fixed.
- High, fixed and confirmed: managed frames without a name were shown as "[native code]" and merged, with nothing
  saying why (now `[unresolved frame in <module>]`, counted, with the rundown and TraceEvent's conversion problems in
  the descriptor, the log and the notes); the read's memory reservation rested on a misreported measurement (now
  measured with the worker's GC settings on three traces: 96 MB plus 3 bytes per file byte); a trace was read again
  after a worker restart without checking the file (now refused with `TraceChanged` when its size or write time
  changed); the CLI labeled all-types totals as one type's.
- Moderate (decided autonomously): 19 fixed now, among them one re-read per trace for concurrent queries, every stack
  answered in the log with its stack node, the allocation kind per sample with LOH and POH columns, exact arrays and
  pooled tree buffers, sorted samples and checked invariants, `.etlx` and partial-trace sweeps, a timeline bucket
  limit, cancellation through the fast queries and the CLI's read, honest recording cancel and failure messages, one
  meaning of %; 5 fixed in part with the rest in the Backlog or recorded (the conversion's cancellation, progress on
  the fast queries, type id and module, the CLI's type text in the request line, more race tests); 1 deferred (one flattened-tree builder for the dominator and
  allocation trees).
- Measured (Release, the `allocations` benchmark with the worker's GC settings): a 72 MB, 600 s trace (565,625
  samples) reads in 1.9 s with 227 MB of growth; whole-trace queries 2 to 13 ms. Acceptance: on LeakyApi under LoadGen
  the leaking allocation sites are in the top call stacks, through the reader, the worker and the CLI (a 12 s trace
  listed the five leaks' `Grow` methods as its top five stacks).

---

## Sprint 2.6: Smart leak detectors

**Goal**: name the leak, not just show the data.

**Scope**: event handler leaks, disposed-but-referenced objects, timers keeping objects alive, static
collections growing across snapshots, HttpClient misuse, common ASP.NET Core patterns. Fixtures first: a timer leak,
disposed-but-referenced objects, and a non-leaking LeakyApi workload (bounded cache with eviction, subscribe then
unsubscribe, dispose and release) for the "no detector fires" run.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): one card per detector (cause, fix, link to the objects), the 1.6 presentation; event handler leaks and the WPF binding, collection binding, dependency property and x:Name leaks.

**Acceptance criteria**: every LeakyApi leak is detected with its known answer; no detector fires on a clean
LeakyApi run.

**Dependencies**: 2.2, 1.4, 2.3.6 (the detector cards are built in Avalonia, ADR-025).

**Decisions** (all decided autonomously at /start-sprint, 2026-10-26; the user said "go ahead with sprint 2.6"):
1. Detectors are inspections (ADR-020) of a new kind, `Leak`: one card each on the Inspections tab, in their own group
   ("Leak detectors"), with cause, fix and rows that open the objects; `inspect` on the CLI runs them. No new page.
2. A baseline: the inspection request takes an optional baseline snapshot (another open snapshot, the earlier one), so
   a detector can measure growth; the Inspections tab gets a baseline picker and the CLI `inspect --baseline <dump>`.
   Matching across dumps uses stable keys (a static field's declaring type and name, a type name), never addresses
   (ADR-018). Protocol 14.
3. The detectors, each with its threshold logged as its parameters (2.3.7 review M5):
   - `growing-static-collections`: collections a static field holds directly (List, Dictionary, HashSet, Queue,
     Stack, ArrayList, Hashtable, ConcurrentDictionary, reference arrays; ConcurrentQueue and the others went to the
     Backlog at review, M5), counted by their own fields. With a baseline: grown by at
     least 1,000 elements and 10 %. Without: at least 10,000 elements, said to be large, growth unknown.
   - `event-handler-leaks`: an event field (a delegate field of a publisher type) holding at least 1,000 subscribers
     across its publishers, named `Publisher.Event`, with the subscribers' types.
   - `timer-leaks`: at least 500 active timers whose callbacks target one type.
   - `disposed-but-referenced`: at least 100 reachable objects of one type whose disposed flag is set (the field names
     the .NET guidelines and templates use).
   - `httpclient-instances`: at least 20 reachable `HttpClient` or handler instances (create one and reuse it).
   - `undisposed-service-scopes`: at least 500 reachable dependency injection scopes not disposed (ASP.NET Core).
   They read field values, so they need a full dump; on a gcdump they are unavailable with the reason (ADR-020).
4. Fixtures first: two new leaks outside the classic five (so the five's exact known answers stay): `TimerLeak`
   (timers with an instance callback never disposed, rooted by the runtime's timer queue) and
   `DisposedReferencedLeak` (objects disposed and still held by a static list); LeakyApi's "all" includes them. A clean
   workload (`Clean:Enabled`: a bounded cache with eviction, subscribe then unsubscribe, timers disposed, objects
   disposed and released) for the "no detector fires" run. Known answers gain `detections` (detector, key, minimum
   count); a `clean` known answer lists none.
5. Acceptance runs on two LeakyApi dumps each: a baseline after a warm-up and a current one after more load, all leaks
   enabled, then the clean workload alone.
6. Logs (ARCHITECTURE 11): each detector logs its rule version, thresholds, what it scanned and each row's key and
   counts, so a disputed finding is traced; the baseline used is part of the log line. Never field values.
7. Not in this sprint: WPF binding, collection binding, dependency property and x:Name leaks (a WPF fixture app is
   needed; Backlog); detectors on gcdumps (no field values).
8. Found while building (decided autonomously): a `System.Threading.Timer` nothing references is finalized and stops,
   so the timer leak uses started `System.Timers.Timer`s, the pattern that leaks; the new leaks get their own switch
   (`Leaks:Detected`, LeakyConsole `--detected`) so the five's exact census, paths and retention checks are untouched;
   the baseline picker preselects the latest earlier open snapshot of the same process; without a baseline the
   static collections detector leaves out `System.*`, `Microsoft.*` and `Internal.*` statics (the runtime's own tables,
   such as `CastHelpers.s_table`, fired on the clean run), and arrays count only when they hold references; the event
   handler detector reads a publisher type's field name once per delegate type (replaced at review, H2); the tab shows
   one list with group headings instead of two lists (one selection).
9. Changed at review (decided autonomously, `docs/reviews/sprint-2.6.md`): byte totals are bytes (subscribers'
   delegates and targets, timers' objects); the event detector counts field-like events only, names each delegate by
   its publisher's own field, and leaves out unreachable publishers, self-held handlers, compiler-generated holders and
   framework callback fields (a multicast in any field of the publisher's own is an event too); the timer detector counts periodic timers in the queue only and attributes static or
   lambda callbacks to the timer's state; disposed flags may be integers or auto-property backing fields; HttpClient
   counts distinct handlers owned by clients; the root scope is left out; reachability is computed once per snapshot
   and leaves out the finalizer queue; detector rows open the type's reachable objects (`InstanceScope.Reachable`), so
   the whole-type actions stay off; an unusable baseline is refused; a baseline chosen mid-run reruns the growth tile.

**Task breakdown**:
1. [x] Fixtures: `TimerLeak`, `DisposedReferencedLeak`, the clean workload in LeakyApi; known answers with
   `detections` and `Clean.json`. Tests: known-answer checks for the new fixtures with ClrMD.
2. [x] Contracts and worker: inspection kind and baseline flag on `InspectionInfo`, `BaselineSnapshotId` on the
   request, the baseline in `InspectionContext`; protocol 14. Tests: worker and contract mirror.
3. [x] Detectors in Analysis, each tested on synthetic graphs with a fake reader: the six of decision 3.
4. [x] Front ends: the Inspections tab's detector group and baseline picker; CLI `inspect --baseline`. Tests: view
   model and view tests, CLI integration.
5. [x] Acceptance: every LeakyApi leak detected with its known answer; nothing on the clean run (integration).
   Measurements on Medium (deferred: the sample dumps need a runtime no longer installed; measured on a 658 MB
   LeakyApi dump instead). Docs: ARCHITECTURE (ADR-020 amendment, 4.4, 10, 11), DEVELOPER, CLAUDE.md, the known
   answers README.

**Review summary**: 8 High, 24 Moderate, 33 Low after dedupe (four reviewers: solution-architect, senior-dev,
gc-specialist, support; `docs/reviews/sprint-2.6.md`). Highs fixed: counts reported as bytes; the event field name
cached per type pair; any delegate field counted as an event; one-shot, paused and lambda-callback timers counted or
misattributed; rows opening the whole type; a baseline change mid-run leaving tiles waiting; the disposed probe's
O(types x objects) scan; a gcdump baseline silently ignored. Moderates fixed now: reachability once per snapshot,
finalizer queue left out, delegate targets read as the runtime stores them, integer and backing-field disposed flags,
HttpClient factory clients, the root scope, a clean workload with look-alike patterns, an upper bound in acceptance,
rerun errors, every row and exclusion logged by name, elapsed time; from the re-review, native-int fields, clients
sharing one handler, events in private fields, publisher reads limited to types that can fire. All Highs confirmed by
the reviewers that raised them. Deferred: other collection types, leaky HttpClient and scope fixtures, in-flight scopes,
the picker refresh (2.7), derived-input descriptions, the shared layout table, threshold edge tests, an exact object
set for detector rows, the Medium measurement. Tests: 1,126 (3 skipped).

---

## Sprint 2.7: Navigation and polish

**Goal**: a coherent, keyboard friendly tool.

**Scope**: full navigation history, object bookmarks, consistent cross-view linking, keyboard support, CSV
export of any grid.

From the dotMemory research (`docs/research/dotmemory-ux.md` 7): an analysis path sidebar (branch from any earlier step), analysis tabs, object marks across snapshots, the full filter syntax, keyboard shortcuts, CSV export.

From 2.3.5 (ADR-026): CSV export is the first form of the shared analysis export, analysis only by default (no string
text or field values unless the person exporting opts in for that export).

From the 2.6 review: the Inspections tab's baseline picker refreshes as snapshots open and close and matches the
process by id and start time (M12); its group headings are not selectable (L13).

**Acceptance criteria**: every grid exports to CSV; every view reachable by keyboard; history survives view
switches.

**Dependencies**: 2.4.

**Decisions** (all decided autonomously, 2026-10-27, Phase 2 autonomy):
1. History becomes an analysis path per open snapshot, kept while the snapshot is open: switching snapshots, tabs or
   pages never resets it. It is a tree: opening something from an earlier step adds a branch beside the later steps
   instead of dropping them (dotMemory's Analysis Path). Back goes to the step's parent, Forward to the child visited
   last. Links into the Retainers tab are steps too, replayed when chosen. At most 30 steps per snapshot; past that the
   oldest leaf off the current path is closed. A step can be closed with its branch. The path is a sidebar on the
   Snapshots page.
2. Analysis tabs (several analyses side by side) are deferred to the Backlog: the branching path covers coming back to
   an earlier analysis, and each tab would hold its own worker result sets.
3. Bookmarks mark objects (snapshot, address, type, one of five colors). They are kept in `bookmarks.json` in the app's
   data folder (a root the tests replace), keyed by the snapshot file's full path, size and write time, so they come
   back when the snapshot is opened again; they hold no field values or string text. A Bookmarks list beside the path
   shows the marks of every open snapshot and opens them; marks of snapshots not open are kept. Types are not bookmarked.
4. Filter syntax (types and diff grids): space-separated terms must all match; `-term` leaves out names that match;
   `module:text` (or `m:text`) matches the row's module, and rows without a module (namespace groups) do not match it.
   Each term keeps the 1.3 rules (smart case, `*`, CamelHumps); highlighting shows the positive terms. The instances
   filter (string value, address) is unchanged.
5. Keyboard: one shortcut table (`Shortcuts`) drives the window and a shortcuts list (F1, also on the About page).
   The command key is Ctrl on Windows and Linux and Cmd on macOS. Command+1 to 7 open the pages in sidebar order;
   Alt+1 to 4 the Snapshots tabs; Alt+Left and Alt+Right (and the mouse buttons) move along the path; Command+F
   focuses the current view's filter; Command+E exports the focused grid (else the view's main grid); Command+D
   bookmarks the shown object or the selected row's object; Command+O opens a dump; Command+L focuses the path; F5
   refreshes the page; F1 shows the shortcuts list on the About page. Enter, double-click, Right and Left keep their
   1.5 and 2.4 meaning.
6. CSV export (the first form of ADR-026's export): every grid has "Export to CSV..." in its context menu and
   Command+E. It writes all of the grid's rows in its current sort and filter (a tree as currently expanded), not only
   those loaded, with the result's columns other than hidden link columns. Columns that carry values (the instances'
   Value, a duplicate string's text) are left out unless the person answers yes for this export, which then says so on
   its first line. Text cells that a spreadsheet would read as a formula get a leading apostrophe. UTF-8 with a BOM
   (spreadsheets read it). The file is written off the dispatcher; the log names the grid, rows, columns, the opt-in and
   the time, never the path or values.
7. `ResultColumn` gains `IsHidden` and `HasValues` (protocol 15), replacing the naming convention (Backlog: 1.5 L2,
   1.6 M2); the CLI's `--format csv|json` stays a local view and keeps its columns (ADR-026).
8. The Inspections tab's baseline picker (2.6 review M12, L13) is taken as planned. Dumps record no process start time,
   so an earlier capture of the same process matches by process id when both dumps record one, else by name; a
   baseline the person chose stays chosen while it is open, and selecting a snapshot chooses its default again.

**Task breakdown**:
1. [x] Result columns say what they are: `ResultColumn.IsHidden` and `HasValues`, protocol 15; every source sets them;
   the grids, the inspections tab and the CLI table use the flags. Tests: ContractMirrorTests, worker source column
   tests, InspectionsViewModelTests, CLI output tests.
2. [x] CSV writing in Application (`CsvExport`): streams a result set's pages (or in-memory rows), quoting, the formula
   guard, the values opt-in and its first line; the CLI shares the cell quoting. Tests: CsvExportTests (quoting,
   formulas, hidden and value columns, all pages fetched, cancellation).
3. [x] Export on every grid: `IGridExport` on each grid's view model, `GridExportService` (save picker, the values
   question, writing off the dispatcher, logging), `GridBehaviors.Export` adding the context menu item. Tests: view
   model export tests per source kind; a view test that every DataGrid in every view has an export.
4. [x] Filter syntax: `NameFilter` terms, negation and `module:`; the worker's types and diff sources pass the module;
   highlighting. Tests: NameFilterTests, worker filter tests.
5. [x] Analysis path: `AnalysisPath` (a tree of steps per snapshot) replacing the navigator's list; Retainers steps;
   the sidebar; closing a branch; the 30-step cap. Tests: AnalysisPathTests, NavigatorViewModelTests (history survives
   snapshot and tab switches), a view test of the sidebar.
6. [x] Bookmarks: `BookmarkStore` (file, keyed by snapshot identity), `BookmarksViewModel`, the list, Command+D and a
   context menu item on object rows. Tests: BookmarkStoreTests, BookmarksViewModelTests.
7. [x] Keyboard: the `Shortcuts` table, the window's handling (platform command key), focus of filters and the path,
   the F1 list. Tests: ShortcutsTests; a view test that reaches every page, tab and subject by keyboard only.
8. [x] Baseline picker: refreshes as snapshots open and close, matches the process by id (else name); headings
   not selectable. Tests: InspectionsViewModelTests, a view test.
9. [x] Docs: ARCHITECTURE (code map, protocol 15, ADR-029 for the analysis path and bookmarks, ADR-026 consequences),
   DEVELOPER, README (shortcuts, filter syntax, export), Backlog (analysis tabs).

**Review summary**: 3 High, 14 Moderate, 27 Low after dedupe (four reviewers: solution-architect, senior-dev,
gc-specialist, support; `docs/reviews/sprint-2.7.md`). Highs fixed: in-memory grid exports wrote command lines without
asking (columns now flag values in the view); overlapping filter terms drew wrong names (ranges merged); two windows
overwrote each other's bookmarks (each change re-reads the file under a lock). Moderates fixed now: export cancel, no
partial files, errors shown, the export tied to its result set in the log; bounds in CSV headers; the CLI's formula
guard; the default baseline ignores file times; bookmark toggle races and file problems; in-memory exports keep the
grid's sort; step and snapshot ids and filter shapes in the log; tests for the path's edges. Deferred: a column
projection on page requests (values never read for an export without them), releasing and measuring the result sets of
snapshots not shown, analysis tabs. All Highs confirmed by the reviewers that raised them; seven Lows the re-review
found in the fixes were fixed too (lock file kept, no path in the export's error line, neutral values wording).
Tests: 1,205 (3 skipped). Timing tests of untouched areas (a synthetic job's cancellation, a monitoring capture, a
session concurrency test) failed once each only in full parallel runs on a loaded machine and pass alone.
