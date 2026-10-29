# Review: Sprint 3.5 Leak detection depth

Changes since `sprint-3.4`. Four perspectives, run in parallel: solution-architect, senior-dev, gc-specialist, and a
support engineer (a general reviewer with a read-only brief, so the logs can back up any result a user contests).
Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go with your reco for moderate
recommendations"), marked "decided autonomously". None of the reviewers had a shell; each read the files directly.

Totals after dedupe: 3 High, 15 Moderate (1 from the re-review), 21 Low (7 from the re-review). A finding raised by more than one reviewer is merged and keeps the
highest severity.

## High / Urgent

- **H1 The unreachable set contradicted the app's own retention views** (solution-architect, support engineer;
  senior-dev and gc-specialist as Moderate). It was the complement of the leak detectors' reachability, which does not
  start from the finalizer queue, while paths, dominators and retained sizes (ADR-024's `PathRoots`) do. Objects waiting
  for their finalizer, and what they hold, were in a set titled "objects no root reaches" yet had paths and retained
  sizes. Fix: the set is the complement of `Reachability.FromPathRoots` (the retention views' roots, finalizer queue
  included), so no member has a path or retains anything; the leak detectors keep `Reachability.For`. The wording
  (contract, tooltip, CLI help, docs) says objects waiting for their finalizer are not in it, and the resolution log says
  how much only the finalizer queue holds. Tests: `LeakDetectorTests.The_unreachable_set_is_what_the_retention_views_
  roots_do_not_reach`; `DetectorTests.The_unreachable_set_is_the_objects_no_root_reaches` (exact count and bytes against
  ClrMD's walk). Status: Fixed; confirmation below.
- **H2 Type growth compared gcdumps and full dumps as if they named types alike** (all four reviewers). A gcdump names
  nested types without their declaring type and may be sampled; framework nested types lose their namespace. Fix:
  `type-growth` compares two full dumps only: it requires a complete graph, exact counts and root details (no gcdump has
  root details, even a complete one, which the re-review caught after a first fix that required only the first two), and
  refuses with the reason unless both snapshots are full dumps; it decides "framework" by module as well as namespace, refuses a baseline captured later (no rows, "not compared"), and its basis has both snapshots'
  totals and each row's module and bytes then and now. Test: `LeakDetectorTests.Types_that_grew_...`. Status: Fixed;
  confirmation below.
- **H3 Pending async methods counted the framework's own waiting work** (gc-specialist; solution-architect, senior-dev and
  support engineer as Moderate). A healthy server with 1,000 open connections fired on Kestrel's connection loops. Fix:
  methods of framework types are left out and logged as "framework method waiting by design" with their counts; the
  user's own waiting method is still found below them. Test: `LeakDetectorTests.Async_methods_waiting_...` (3,000
  framework boxes left out). Status: Fixed; confirmation below.

### Confirmations

Each reviewer re-read the fixes of the findings it raised (read only):
- solution-architect: H1 and H3 confirmed, and M1 to M3. H2 not confirmed at first (a complete gcdump passed the first
  fix), then fixed by the kind check and root details. Lows recorded: `Reachability.For` keeps its own root rule rather
  than deriving it from `PathRoots` (L19); objects held only by Debug frames get no caveat on the set (L17); no worker or
  integration test of the set's refusals (L18).
- support engineer: H1 confirmed, and M3, M4, M6 to M8, L9, L10. H2 not confirmed at first (the same gap), then fixed. Two
  new Lows fixed at once: a backlog spread over many small types was not reported (now one "(other types: N)" row, L15);
  two doc comments had swapped places (L16).
- gc-specialist: H3 confirmed, H1 (its M3) confirmed, the decided items as described. H2 not confirmed at first (the same
  gap), then fixed. One new Moderate fixed at once (M15).
- senior-dev: H2, M2 to M7 and the Lows confirmed. The same new Moderate (M15), fixed at once.
- Second round, after the kind check, root details and the member arrays: all four confirmed H2 and M15 (and the support
  engineer L15 and L16), with two new Lows recorded (L20, L21).

## Moderate (all decided autonomously)

- **M1 An incomplete dump's unreachable set would include live objects** (solution-architect; gc-specialist as Low).
  Fixed: refused for a dump without a complete graph (as ADR-032 does), with a `CapabilityNotSupportedException`.
- **M2 Tests did not prove the set** (solution-architect, senior-dev). Fixed: exact count and bytes against ClrMD; the CLI
  must exit 0 and list a late garbage type; a gcdump does not offer the set in the view model; a unit test of the walk.
- **M3 How the deferred "finalized between snapshots" would extend** (solution-architect). Fixed in the Backlog item: it
  is an upper bound outside gen2, LOH and POH (moved objects count as dead), and a reservation callback in
  `InspectionContext` comes first.
- **M4 LeakyConsole hung with `--detected FinalizerBacklog`** (senior-dev): its last wait for finalizers never returned.
  Fixed: that leak grows after the wait, and is refused with `--waste`, whose own backlog blocks the thread first.
- **M5 The new detectors' counts were checked as a range** (senior-dev). Fixed: exactly every item.
- **M6 No cancellation in the async loop; type growth hashed two strings per object** (senior-dev). Fixed: a check every
  64K boxes; counts per type index in one pass, folded by key afterwards. A timing on Medium is in the Backlog.
- **M7 The unreachable set's log could not answer "that much garbage?"** (support engineer). Fixed: alive objects and
  bytes, the part only the finalizer queue holds, unreachable bytes, and whether the graph is complete.
- **M8 Basis lines named only the top three** (support engineer; senior-dev as Low). Fixed: every reported row with its
  counts and bytes, then the five largest below the threshold; type growth caps its list at 50 with "and N more".
- **M9 A baseline captured later produced rows under a wrong note** (support engineer). Fixed: not compared; a baseline
  from another process keeps its rows with the warning.
- **M10 Type growth's wording overstated what it measured** (support engineer). Fixed: the note says each row's bytes and
  set are all of the type's objects now (Compare's new objects narrow them down), and that a cache filling up grows by
  design.
- **M11 The finalizer backlog asserted a cause one dump cannot show** (support engineer, gc-specialist). Fixed: the cause
  says a queue that does not drain means a blocked or slow thread; the note says to confirm with a second dump compared
  by address; the basis counts the objects registered for finalization. Reading the finalizer thread's state from the dump
  is in the Backlog.
- **M12 Some state machine boxes were missed silently** (gc-specialist; solution-architect, senior-dev, support engineer
  as Low). Fixed: the debugger's `DebugFinalizableAsyncStateMachineBox` matches; the basis counts the box types matched;
  unreadable states are noted; pooled `ValueTask` boxes (no task state) are documented as not counted.
- **M13 Type growth counts young objects, so load alone can look like growth** (gc-specialist). Deferred (decided
  autonomously): requiring growth in gen2, LOH and POH changes what the fixture leaks show; in the Backlog with a column of
  old-generation growth.
- **M14 The detectors copy their members several times on a large heap** (gc-specialist). Fixed in part: type growth
  no longer hashes per object, and keeps members for reported rows only (see M15). The finalizer queue as a bit set and a
  reservation for detector runs are in the Backlog.

- **M15 The first M14 fix sized a member list for every application type** (gc-specialist, senior-dev; re-review), about
  4 bytes per reachable object before the threshold check. Fixed: rows carry no list; only reported rows get an exact
  array of their census count, filled in one pass over the type indices.

## Low (listed, not raised)

1. Finalizer backlog rows carried the `Reachable` scope (fixed: `Finalizable`).
2. Pending async rows name the first box type and its module (System.Private.CoreLib), not the state machine's.
3. In Debug builds the class state machine object is outside the row's bytes (the note now says "the state machine
   boxes' own").
4. Overloads of one method merge into a row; instantiations of a generic declaring type split apart.
5. The gcdump refusal of the unreachable set gave the wrong reason (fixed); finalizer backlog's gcdump refusal still
   speaks of root details.
6. The gcdump refusal was an `ArgumentException` (fixed: `CapabilityNotSupportedException`).
7. ARCHITECTURE's Contracts code map did not list the new kind (fixed).
8. Section 10 has no timings or memory figures for the new detectors (Backlog).
9. `!` without a comment and a side-effecting predicate passed to `TypeMask` (fixed).
10. The CLI ignores `--module` with `--unreachable` and accepts `--compared-with` with it.
11. Decision 5's count of known answers was wrong (fixed).
12. The clean workload's finished async calls finish on the thread pool after they are stored.
13. The synthetic test did not cover faulted, canceled or unreadable tasks (fixed).
14. With .NET 10's opt-in runtime async, compiled methods have no box at all (documented in ADR-034).
15. A finalizer backlog spread over types of fewer than 100 objects each was not reported (re-review; fixed: one row).
16. Two doc comments in `WorkerService.Sets.cs` had swapped places (re-review; fixed).
17. Objects held only by Debug frames show as unreachable with no caveat (re-review).
18. No worker or integration test exercises the unreachable set's gcdump or incomplete refusals (re-review).
19. `Reachability.For` keeps its own root rule rather than deriving it from `PathRoots` (re-review).
20. A gcdump refused by type growth's or the finalizer backlog's capabilities gets the root details explanation, not
    the real reason (different type names, survivors without roots) (re-review).
21. Type growth's fill pass keeps each row's position in a dictionary; a counter per row would avoid the hashing
    (re-review).
