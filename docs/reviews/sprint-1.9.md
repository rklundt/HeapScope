# Review: Sprint 1.9 Phase 1 hardening

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, the change set since `sprint-1.8`). The sprint runs
under the Phase 1 autonomy the product owner extended to it ("I agree on all parts. Make it so"): Moderate findings were
decided by the lead engineer and are marked "decided autonomously". None of the reviewers could run git; the lead
engineer ran the comparisons they asked for (below).

Totals after dedupe: 1 High, 7 Moderate, 24 Low (findings raised by more than one reviewer are merged and keep the
highest severity).

## High / Urgent

- **H1 The dominator algorithm prototyped would not finish on common heap shapes** (gc-specialist). Cooper-Harvey-
  Kennedy iterates to a fixpoint; its pass count grows with the back edges a path crosses. The prototype measured only a
  random graph, close to its best case for passes. Measured after the review: on a doubly linked list reachable from both
  ends (a deque, a circular list, timer queues) it needs one pass per node: 3,002 passes for 3,000 nodes, 30,002 passes
  and 7 s for 30,000, not converged in 1,000 passes on 1M nodes (quadratic overall). The reviewer's lists entered from one
  end converge in 2 passes when nodes are visited after their depth-first parent; the two-ended list is the bad case.
  **Fixed:** the prototype implements semi-NCA (the Lengauer-Tarjan family, one pass over a depth-first preorder with path
  compression) next to Cooper-Harvey-Kennedy, with chain, back-reference and two-ended list shapes and an agreement check
  (identical dominators on random graphs, chains and two-ended lists wherever Cooper-Harvey-Kennedy converges); semi-NCA
  runs the 1M-node two-ended list in under 0.1 s. ARCHITECTURE 6.4 chooses semi-NCA for Phase 2. Status: fixed;
  confirmation below.

### Confirmations

Each fix went back to the reviewer that raised it; all were confirmed on re-reading the code.
- H1: confirmed by gc-specialist, including a line-by-line check of semi-NCA (the preorder walk, semidominators in
  reverse preorder with root targets at 0, the iterative path compression, the nearest-common-ancestor climb reusing the
  parent array, retained sizes in reverse preorder) and of Cooper-Harvey-Kennedy over preorder. It accepted the
  correction that one-ended lists converge in about two passes and the two-ended list is the bad case, and withdrew the
  Workstation GC remark. senior-dev also checked the algorithms.
- M1, M2, M3: confirmed by gc-specialist; M1 by senior-dev.
- M4, M5, M6 and the documentation Lows: confirmed by solution-architect; M5 and M7 by senior-dev.

## Moderate (decided autonomously)

- **M1 The memory peak mixed build transients and garbage with the index cost** (gc-specialist, senior-dev). **Fix now:**
  each phase starts after a compacting collection (with large object heap compaction) and has its own peak sampler; the
  untrimmed postorder copy is gone; keep-alives cover what Phase 2 keeps. The benchmark already ran Server GC (its project
  sets it), not Workstation as one review assumed.
- **M2 "Holds for real heaps" rested on a sparse fixture; mapped mode and a time budget were missing** (gc-specialist,
  solution-architect). **Fix now:** DenseHeap (54.1M real Roslyn objects, 3.03 references each) and mapped mode measured:
  Medium 9.5 s, peak 3,514 MB in memory, 9.0 s and 2,058 MB mapped; DenseHeap 12.9 s, peak 4,119 MB in memory (just over
  the 4 GB budget), 12.2 s and 2,360 MB mapped. Decisions in 6.4: computation follows the memory mode (mapped when graph,
  reverse CSR and about 28 bytes per object of working arrays exceed the budget), the mode is reconsidered when indices
  are computed, kept indices are cache sections, a 60 s time budget on Medium-scale real heaps; paging on a 16 GB machine
  is measured when 2.x builds it.
- **M3 Frozen objects were not joined to the virtual root** (gc-specialist; solution-architect as Low). Section 6.1
  requires them to count as alive. **Fix now:** `RootTargets` adds every frozen object.
- **M4 ADR-023 left out what moving capture to the worker costs** (solution-architect). **Fix now:** consequences added
  (layering and the architecture test, a capture RPC with a protocol bump, priority against lossy buffering, isolation
  with a child process option, memory and the load gate, elevation, scheduling) and the measurement's caveats (one run
  per size, the CLI rather than the UI, growth faster than the object count).
- **M5 The CLI JSON change had no tests and one flag could not describe a mixed diff** (solution-architect, senior-dev).
  **Fix now:** `diff` reports `BaselineKind`, `CurrentKind`, `BaselineIncludesUnreachable` and
  `CurrentIncludesUnreachable` (`IncludesUnreachable` only when both are); `summary` reports `Kind`; test
  `Cli_json_says_which_kind_each_snapshot_is_and_whether_it_counts_unreachable_objects`; DEVELOPER.md documents it.
- **M6 Kind checks were still spread outside `SnapshotKinds`** (solution-architect). **Fix now:** `SnapshotKindHandler`
  gained `ExplainMissingFact` and `NamesWhenComparedWith`; `InspectionAvailability` and `TypeDiffSource` ask
  `SnapshotKinds.Describe(kind)`; `TypeDiffSource` has no default kinds; the comment names Contracts'
  `CapabilityDescriptions` as the one other place that words explanations per kind (for front ends, ADR-016).
- **M7 `SnapshotKindsTests` did not test the full dump's notes or an unknown kind** (senior-dev). **Fix now:** a theory
  per kind and completeness, the per-kind explanations and name forms, and `For((SnapshotKind)99)` refused.

## Low / Trivial (listed, not fixed unless noted)

- L1 A cache whose metadata names a kind with no handler throws before its graph is disposed (solution-architect,
  senior-dev).
- L2 Docs: 5.3 said the vendored warning goes to the console, ADR-010 said two vendored changes (solution-architect).
  Fixed, as docs must describe what was built.
- L3 The new code map lacked `GrowthThreshold`, `DumpSizeEstimator`, `ParentProcessWatcher`, `CliHost`, `DacLocator` and
  `LocalOnlyFileLocator` (solution-architect). Fixed, same reason.
- L4 `PartialSnapshotFile` uses `DumpCapturer.PartialSuffix`; the `ObjectValueKind` mapping sits in `ObjectFormatter`, not
  `ContractMapping` (solution-architect, trivial).
- L5 `GcDumpCapturer` discards the partial file only on cancellation and IO errors; another exception leaves it until the
  sweep (senior-dev).
- L6 `PartialSnapshotFile.Commit` catches only `IOException`; the "already exists" message names the final path when only
  the partial file exists (senior-dev).
- L7 `gate.ps1`: Windows PowerShell 5.1 can turn native stderr into errors under `Stop`; the summary regexes take the
  first match; failed test names are cut at the first space (senior-dev). The gate ran clean three times on this machine.
- L8 The lossy-buffering line is logged at Warning on every .NET 10 capture (senior-dev, gc-specialist).
- L9 The vendored change 4 call sites lack a `HeapScope change` marker; its field sits between change 1's comment and the
  constructor (senior-dev).
- L10 `SmokeRunner`: a leftover summary block above `RunSmokeLiveAsync`, blank line details; `App.xaml.cs` has a double
  blank line (senior-dev).
- L11 `ColumnSort.Columns` is unused (senior-dev).
- L12 `Shared.xaml` is merged in several dictionaries, so it is parsed more than once (senior-dev).
- L13 The prototype's algorithms have no unit tests; 2.3 should start from known-answer tests (diamond, cycle, self loop,
  a root reachable only through another root) (senior-dev). The agreement check between the two algorithms covers them
  in the benchmark.
- L14 `RepoRoot` fails as a `TypeInitializationException` when the solution is not found (senior-dev).
- L15 The depth-first stack and per-node state cost about 8 bytes per object; a pooled stack and the preorder numbers as
  the visited marker would save it (gc-specialist).
- L16 The crafted gcdump test changes the top-level graph's type tag only, not a nested position (gc-specialist).
- L17 ADR-023 measurement caveats (gc-specialist). Added with M4.
- L18 Keep-alives in the prototype (gc-specialist). Fixed with M1.
- L19 `ColumnSortTests` depends on dictionary key order; `Validate` throws without a parameter name (senior-dev,
  trivial).
- L20 The mixed-diff note on namespace grouping and the `TypeDiffSource` defaults (folded into M6).
- L21 The wire contract was not diffed by the reviewers (solution-architect). Checked by the lead engineer: every
  declaration line of the five DTO files equals sprint-1.8's `WorkerDtos.cs` as a multiset; the protocol version is 6.
- L23 New from the confirmations: `RetainedSizes` recomputes the preorder; the stack and the compression path grow to
  the chain depth; with both algorithms, semi-NCA's result stays alive during the other's phase; the error for an unknown
  shape omits `twoends` (gc-specialist, senior-dev). Phase 2 notes in 6.4.
- L24 The section 10 row said the target was met while DenseHeap in memory peaks 23 MB over the budget; the theoretical
  worst case of the nearest-common-ancestor climb was not noted (gc-specialist). Fixed in the docs.
- L22 The refactors were not compared with sprint-1.8 by the reviewers (senior-dev). The behavior is covered by the
  unchanged Phase 1 tests (all pass), the smoke runs on the Small and Medium dumps with the old and the new main window,
  and the gate.

## Test runs and measurements

- Full gate before the review: pass (725 tests, zero warnings); one run failed once in
  `LoaderKnownAnswerTests.Cancellation_stops_the_load_within_two_seconds` under full load and passed three isolated runs
  and the next gate; it is in the gate history.
- Smoke runs: Small open passes; Medium open and compare passes every check but trips the 200 ms lag gate at times with
  the old and the new main window alike (565 and 602 ms in one run each): the known High backlog item.
- Phase 2 index prototype and gcdump capture memory: ARCHITECTURE 6.4 and ADR-023.
