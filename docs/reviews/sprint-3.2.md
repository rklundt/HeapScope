# Review: Sprint 3.2 Retention views

Changes since `sprint-3.1`. Four perspectives, run in parallel: solution-architect, senior-dev, gc-specialist, and a
support engineer (a general reviewer with a read-only brief, so the logs can back up any result a user contests).
Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go with your reco for moderate
recommendations"), marked "decided autonomously". None of the reviewers had a shell; each read the files directly.

Totals after dedupe: 7 High, 15 Moderate, 24 Low (3 Moderates and 5 Lows from the re-review of the fixes). A finding raised by more than one reviewer is merged and keeps the
highest severity.

## High / Urgent

- **H1 A back pointer from a member's own child counted as a way the member is retained** (gc-specialist). A referrer
  reached only through the member (`child.Owner = member`, a delegate pointing back, a doubly linked node) was added to
  the pattern, so members held the same way split by how many children point back. Fix: a referrer whose shortest path
  passes through the member (checked up to 256 steps up its path) is skipped; self references were already skipped.
  Test: `SimilarRetentionTests.A_back_pointer_from_a_members_child_retains_nothing_and_does_not_split_its_group`.
  Status: Fixed; confirmation below.
- **H2 Interfaces missed inherited, generic and array interfaces** (gc-specialist). ClrMD lists a type's own
  non-generic InterfaceImpl rows only. Fix: the reader walks the base types and each interface's base interfaces, and
  decodes generic instantiations from the module's metadata (`InterfaceNames`: from the dump when it holds the image,
  else from the module file on this machine when its image size matches, local only). Tests: the RetentionPatterns
  fixture gained `PatternDerived : PatternBase, IEquatable<PatternDerived>` (`PatternBase : IPatternMarker`);
  `RetentionViewsTests.Interface_rows_count_every_implementing_type_and_open_as_sets` checks the inherited
  `IPatternMarker`, `System.IEquatable<Fixtures.Leaks.PatternDerived>`, `IList<PatternItem>` (from `List<PatternItem>`)
  and every disposable fixture type's full count under `System.IDisposable`. Status: Fixed; confirmation below.
- **H3 Similar Retention's memory was mostly unreserved** (gc-specialist; solution-architect and senior-dev as
  Moderate). Only 4 bytes per member were reserved against the signature memo (up to 8 million entries, about 380 MB at a
  resize) and the pattern tables; nothing compacted after. Fix: `SimilarRetention.WorkingBytes` reserves 4 bytes per
  member, 200 per possible pattern (capped at 1,000,000 patterns, more refused with a message to narrow the set) and the
  memo's cap at 40 bytes per entry, in both the query and a group set's resolution, with `MemoryReservations.Collect()`
  after. The per-member pattern array is gone (M7). Status: Fixed; confirmation below.
- **H4 The Similar Retention diagram double counted a group** (support engineer, solution-architect). A group's paths
  each carry the whole group, and the diagram summed them. Fix: `KeyPathDiagram.Build(..., overlapping: true)` takes the
  most objects of any path through a node or edge; `RetentionGroupDto` documents that its paths overlap. Test:
  `SetViewModelTests.Similar_retention_lists_the_groups_...` (the target of a two-path group of 2 shows 2). Status: Fixed;
  confirmation below.
- **H5 A growth gate over interface rows counted a type once per interface** (senior-dev). `diff --group interface
  --fail-on-growth` summed overlapping rows, so the exit code could be wrong. Fix: refused at parse time (exit 64, with
  the reason); the gate's log line names its grouping. Test: `RetentionViewsTests.The_cli_shows_...` (three refusals
  exit 64). Status: Fixed; confirmation below.
- **H6 The interface table read the graph without a lease and could outlive its snapshot** (solution-architect). A
  private dictionary held a shared task that read `TypeIndices` and `Addresses` after the first caller's lease ended,
  could be re-added after close, and walked every object without a cancellation check: a close during the read could
  crash the worker on a mapped graph. Fix: the table is a `DerivedData` slot (`SnapshotStore.GetInterfacesAsync`): its own
  lease, shared progress, the snapshot's lifetime token, dropped on close; `TypeInterfaces.Read` checks the token every
  64K objects. Status: Fixed; confirmation below.
- **H7 A type whose interfaces could not be read was shown as implementing none** (solution-architect; gc-specialist,
  senior-dev and the support engineer as Moderate). Fix: up to three objects per type are tried; a type still unread is
  on its own "(interfaces not read)" row (`TypeSummaryColumns.UnreadInterfaces`), and the log names the first 20 with the
  reason. Test: `SimilarRetentionTests.Interfaces_are_read_through_the_next_object_when_one_fails_...`. Status: Fixed;
  confirmation below.

### Confirmations

Each reviewer re-read the fixes of the findings it raised (read only):
- gc-specialist: H1, H2 and H3 confirmed ("No High remains"), with M7 and the referrer cancellation check; it raised
  M13 to M15 and L20 to L24 below.
- solution-architect: H4, H6 and H7 confirmed, and M2 and M6; two Lows (a test tying the mirrored row names, module
  files in the pattern fingerprint) were fixed at once.
- senior-dev: H5 confirmed (the gate and the retained refusal exit 64 before a dump opens; the log names the grouping).
- support engineer: H4 confirmed ("nothing is still wrong for this finding"); roots keyed by kind, frozen and description.

## Moderate (all decided autonomously)

- **M1 Group numbers differed between the app (from 1), the CLI and the logs (from 0)** (support engineer, senior-dev).
  Fixed: numbered from 1 everywhere (`RetentionGroupDto.Number`), the CLI option is `--similar-group N` (from 1; a
  negative number exits 64), and the logs say "group N".
- **M2 A retention group set was named by its rank only** (solution-architect, gc-specialist). Fixed: the spec carries
  the pattern's fingerprint (`RetentionGroupDto.Key`) and the rank as a hint; a pattern the snapshot does not have is
  refused with the next step, never another group.
- **M3 Only the five largest groups had their paths logged** (support engineer). Fixed: every other listed group at
  Debug, each with its first type and type count (`Provenance.Describe(RetentionGroupDto)`).
- **M4 Interface grouping dropped the retained column silently, and a sort by retained bytes failed** (support
  engineer). Fixed: the note says why there are no retained sizes and what to do; the sort falls back to bytes.
- **M5 Refusals lacked a next step** (support engineer). Fixed: the gcdump refusal names the side in Compare and says to
  group otherwise or capture a full dump; the retained refusal says to leave out `--retained` or group otherwise, after
  the gcdump check; the missing group and the unknown interface say how to list them.
- **M6 Interface grouping was offered on gcdumps** (solution-architect). Fixed: the types grid and Compare offer it only
  when every snapshot is a full dump, and fall back to types otherwise. A per-grouping capability table in Contracts is
  deferred (Backlog).
- **M7 One array per member in the grouping loop** (gc-specialist). Fixed: patterns are sorted and deduplicated in a
  pooled buffer and looked up as spans (`GetAlternateLookup`); only a new pattern allocates.
- **M8 Retention edge cases untested** (gc-specialist, senior-dev). Fixed: back pointers, self references, a member that
  is a root, a member held by a root and a holder, a dependent handle, and the group lookup by key. A weak-handle-only
  referrer is covered by the root path tree (weak handles are not retaining roots) and is in the Backlog as a test.
- **M9 The diagram merged unrelated gaps and labeled a set's target with one type** (senior-dev). Fixed: a gap step is
  a node of its own path; a set's target is named by the set's title, a group's by "Group N".
- **M10 Test gaps in the integration tests** (senior-dev). Fixed: the group set compared by address with ClrMD's
  objects reached only through the dictionary; every disposable fixture type counted exactly. The reader's null and
  failure paths are tested in Analysis.
- **M11 Failed interface reads hidden from Compare and support** (support engineer, senior-dev): fixed with H7, and an
  interface set logs its first 20 matching types.
- **M12 Similar Retention not reported with its index and rule** (solution-architect, as Low): fixed in part, the log
  carries the rule version and the reverse index mode; the result DTO's index info is in the Backlog.

### From the re-review (decided autonomously)

- **M13 The back pointer test is an approximation** (gc-specialist). It climbs the referrer's shortest path 256 steps:
  a back pointer from deeper in a chain counts again, and a child that a static also holds but whose shortest path
  runs through the member is skipped. Deferred to the Backlog: an exact test (the member dominates the referrer, or
  entry and exit numbers over the shortest-path tree); ADR-031 states the limit.
- **M14 Nested types of generic types got made-up interface rows** (gc-specialist). `Dictionary<K, V>+KeyCollection`
  gave `ICollection<T0>`. Fixed: the generic arguments of every segment of the name are used in order; an argument still
  unknown makes the type unread, never a made-up name. Tests: `InterfaceNamesTests` and the integration check that no
  row holds an unknown argument.
- **M15 A module file was matched by its size only, and a metadata mismatch failed the whole table** (gc-specialist).
  Fixed: the file must match the dump's module index (time stamp and image size); a metadata error marks that type
  unread.

## Low (listed, not raised)

1. A dependent-handle step looks like a field reference in paths (from 2.2; gc-specialist).
2. The 16-referrer limit picks the lowest object indices (counted and documented; gc-specialist).
3. Same-named interfaces from different assemblies merge into one row (gc-specialist).
4. COM objects show only their metadata interfaces (documented in ADR-031; gc-specialist).
5. Diagram toggles are not logged (support engineer).
6. Root nodes were keyed by description only (fixed in passing with M9: kind, frozen and description).
7. A negative `--group` was ignored (fixed with M1).
8. The interface scan skipped cancellation (fixed with H6).
9. The largest groups' text was built by hand (fixed with M3).
10. The interface table could be re-added after close, and progress went to its first caller only (fixed with H6).
11. `summary --group interface --retained` exited 1, not 64; the CLI did not say interface rows overlap (fixed: exit 64
    and a note).
12. `KeyPathDiagram.NodeAt` is used only by tests; `MorePaths` was never bound (removed); the two `MaxReferrers`
    constants are not tied by a test.
13. The diagram is rebuilt on every read; `ShowFramework` rebuilds it twice; `ReferrersText.Incomplete` is reworded by
    `Replace`.
14. Back edges bend past the control's inset and can be clipped; the control does not redraw on a theme change.
15. `TypePathsRequest` with both a type and a set lets the set win silently (solution-architect).
16. Interfaces are not kept on disk, so a reopen reads them again (solution-architect).
17. The referrer loop of one member had no cancellation check (fixed in passing: every 64K referrers).
18. `interfaces!` without a comment in `TypeDiff` (fixed with a comment).
19. Several public types per new file (senior-dev).
20. Module metadata images stay in memory for the snapshot's lifetime, unreserved (gc-specialist).
21. The memo's resize peak was over its reservation (fixed: 48 bytes per entry).
22. The (group, type) set is not counted in the reservation (gc-specialist).
23. A group looked up by key when its rank misses fingerprints every pattern (gc-specialist).
24. Compare shows an overlapping "matching rows" total for interface rows; the note explains it (senior-dev).
