# Review: Sprint 2.4 Retention explorer UI

Changes since `sprint-2.3.7` (about 100 files). Four perspectives, run in parallel: solution-architect, senior-dev,
gc-specialist, and a support and system analyst (a general reviewer with a read-only brief, kept since 2.3.7 so the logs
can back up any result a user contests). Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go
with your reco for moderate recommendations"), marked "decided autonomously".

Totals after dedupe: 5 High, 17 Moderate, 33 Low (the re-review of the fixes added 2 Moderates and 9 Lows). A finding raised by more than one reviewer is merged and keeps the
highest severity. H5 was found by the lead engineer while measuring for the review; the gc-specialist saw its fix land
mid-review and confirmed it.

The reviewers confirmed:
- layering holds: no Core, Analysis, Storage or Capture types in the UI, the view models or the CLI; the new DTOs hold
  only primitives, strings, lists and Contracts enums; every tree, icicle and set computation runs in the worker
  (solution-architect);
- set retention is correct under ADR-024: dependent handles are edges, frozen objects are sources, unreachable members
  are excluded, a member that is a root source counts as retained; "the set retains itself" agrees with per-object
  retained sizes (gc-specialist);
- the stored children order is deterministic across a sidecar reopen and nothing depends on it; older sidecars are
  refused (gc-specialist);
- no field values or string contents reach the log from the new code; icicle segments and expanded keys were logged as
  counts, so no line can flood (support);
- no em or en dashes and no missing license headers in the changed files (senior-dev).

## High / Urgent

- **H1 Set retention reserved no memory and grew a doubling list** (gc-specialist, solution-architect; senior-dev as
  Moderate). `QueryRetainedBySetAsync` ran a bit per object, a `List<int>` stack (two copies while it doubled, 402 MB at
  once on Medium) and a second `GroupRetention` pass (4 bytes per object) with no reservation, against ARCHITECTURE 6.4
  and 9: two requests, or one beside a dominators build, could pass the worker's budget and crash it instead of refusing.
  Fixed: the request reserves `(SetRetention + GroupRetention).WorkingBytesPerObject` per object through the store's
  `MemoryReservations` and collects afterwards; the stack is chunked (64 KB chunks, off the large object heap, dropped as
  they empty). Status: Fixed, confirmed.
- **H2 The focus trail had no bound** (gc-specialist). Every dominator up to the root was sent, one button each in a
  non-virtualized panel, and resent on every expand: a focus at the end of a long chain froze the UI (10K) or sent over
  100 MB (1M). Fixed: the trail keeps the top 3 and the nearest 20 (`DominatorTreeLimits.TrailHead`, `TrailTail`), the
  answer says how many there are (`AncestorDepth`), and the view shows "N more" between them. Test: a 10,000-node chain.
  Status: Fixed, confirmed.
- **H3 After an expand or collapse the old list's row was selected** (senior-dev; solution-architect as Moderate). The
  selection ran in the query's apply step, before the new rows were bound, so it picked the old row: the grid could not
  select it, keyboard expand and collapse stopped after one key, Left never collapsed (stale `IsExpanded`), and a row
  past the first page lost its place. Fixed: the request names the row to keep (`RevealKey`), the worker answers its row
  (`RevealIndex`), and the view model selects it from the new rows after they are bound (`OnRowsApplied` in
  `GridQueryViewModel`), only while they are still the grid's; the grid scrolls it into view (`ScrollToSelection`).
  Toggle, Expand and Collapse decide from the open keys, not the row. Tests: the selected row is the new list's own
  instance; a headless Right then Left without selecting again. Status: Fixed, confirmed.
- **H4 The acceptance criterion was proven on another fixture** (solution-architect). The criterion names LeakyApi; the
  test used the LeakyConsole dump, with no recorded deferral. Fixed: `Every_LeakyApi_leak_is_three_expansions_or_fewer_
  from_the_biggest_retainers` starts LeakyApi with every leak, drives it, dumps it and checks every leak (an item that is
  itself top-level counts as zero expansions). Measured: the same chains as the console fixture (three leaks need 0
  expansions, the unbounded dictionary 1, the event handlers 3). Status: Fixed, confirmed.
- **H5 Sorting every node's children cost seconds and memory** (lead engineer, found while measuring for the review;
  gc-specialist confirmed). Storing each node's children sorted by retained size took the Medium dominators from 8.2 s
  (2.3.7's code on the same machine that day) to 13.4 s, scattered `GroupRetention`'s walk, and raised the run's peak to
  3,747 of the 4,096 MB budget. Fixed: children are stored in index order (a counting pass, 0.7 s on Medium) and only
  the rows a query shows are ordered (`RetainedOrder.Top`, a heap of k: O(m log k)). Medium: dominators 6.4 to 6.9 s,
  retained by type 2.6 to 2.7 s (2.3.7's code: 5.6 s), peak 3,291 MB. ADR-027 records it. Status: Fixed, confirmed.

## Moderate

- **M1 "Show in dominator tree" from a type row usually showed nothing, silently** (solution-architect, senior-dev,
  support). A leak's item type is rarely a top-level dominator, so its group did not exist. Fixed now (decided
  autonomously): the request asks where the group landed; when it is not there the tab says so ("No X object is itself
  a top-level retainer...") and points to "Objects retained by this set" and an object's own link; the dead key is
  dropped. A query placing a type's objects in the tree goes to the Backlog.
- **M2 The tree's wire vocabulary lived in Analysis** (solution-architect). Kinds were `enum.ToString()` compared as
  literals; key forms were written twice and only partly in Contracts. Fixed now (decided autonomously):
  `DominatorRowKinds`, every key form in `DominatorTreeKeys`, `DominatorTreeLimits`; a mirror test checks kinds,
  groupings, every key form and the limits (ADR-016).
- **M3 2.3.7 M7 was half done** (solution-architect, senior-dev, support). The tab and both CLI commands hard-coded
  "exactly"; the set action had four names; the sidecar three phrasings. Fixed now (decided autonomously):
  `ValueBoundTexts.Exact`, `For(bound)` and `SetAction` ("Objects retained by this set") in Contracts, the UI and CLI
  render from the column's `Bound`, one phrase for dominators read from the sidecar, the set summary carries the
  dominators caveat. Deferred to the Backlog: a `ValueBound.Approximate` per result for incomplete graphs, and caveat
  codes in place of free text (an ADR-024 amendment).
- **M4 Grouped trees cost per click with the type table, not the rows** (solution-architect, gc-specialist). Three arrays
  sized by every group per list, a LINQ sort of all groups, an expanded group's members copied (64 MB under a large
  array), the group tables rebuilt per query. Fixed now (decided autonomously): sums over the groups present only, the
  group tables cached per graph and grouping, an expanded group's largest members picked from the list with a filter
  (no copy), and a cancellation check every 64K children.
- **M5 The tree's limits were not part of the contract** (solution-architect, support). Private constants, a CLI that
  could exceed them (`--depth 2 --top 200`) and fail with a parameter the user never set, a cut tree noted only in table
  output. Fixed now (decided autonomously): `DominatorTreeLimits` in Contracts; the worker's refusals name a next step;
  the CLI stops when the tree is cut or the open keys would pass the limit, and says so on stderr for every format,
  naming the row limit; `Batches` and the shown count never pass the row limit.
- **M6 Docs and budgets were not re-measured** (solution-architect, gc-specialist). Fixed now (decided autonomously):
  `dominators` on Medium and DenseHeap in both modes with the children kept, set retention and the tree on Medium;
  ARCHITECTURE 6.4, 7, 10 and 12, ADR-027 and DEVELOPER updated (measurements below).
- **M7 The log did not show what the user looked at** (support; solution-architect Low). Each expand re-logged the same
  first ten rows; the trail and open keys were counts; the UI logged no action; the icicle ran under no activity. Fixed
  now (decided autonomously): the tree line logs the grouping, the trail (types, addresses, retained), the first 20 open
  keys, the revealed key and its row, and the rows under the list just opened; each user action on the tab (expand,
  collapse, focus, grouping, a link from another tab, a set) logs one line under its own activity, which the tree, icicle
  and set queries join.
- **M8 "Objects in the set" meant reachable members only** (support). Fixed now (decided autonomously): the answer counts
  all members (`AllObjects`), and the UI, the CLI and the log say "N reachable objects (M more that no root reaches
  retain nothing)".
- **M9 Focusing an unreachable object gave no next step** (support). Fixed now (decided autonomously): the refusal says
  why for the snapshot's kind (garbage or weakly held in a full dump; root not recorded in a gcdump) and points to the
  biggest retainers; the icicle no longer repeats it. The object view's button stays enabled (it learns reachability
  only when its Retention tab loads): Low, listed.
- **M10 Expanding a long chain recursed without a bound** (gc-specialist). Up to 10,000 open levels, two frames each: a
  stack overflow kills the worker. Fixed now (decided autonomously): levels below `MaxDepth` (1,000) are not followed.
  Test: a 3,000-node chain fully expanded.
- **M11 Exact set retention was not checked independently** (gc-specialist). Fixed now (decided autonomously): the
  integration test compares every leak type's answer with a ClrMD walk (reachable now less reachable without the set),
  and the unit tests add a dependent handle, a frozen source, a weak handle and a reference back into the set. All equal.
- **M12 Several type entries with one name and module** (gc-specialist). The set and the tree's Type grouping take all
  entries of a name and module; the summary shows each entry. Deferred to the Backlog (decided autonomously): naming
  one entry in the request is additive; the lower bound shown beside the exact figure is the set's own, as labelled.
- **M13 A bar whose row was not loaded kept the previous row selected** (senior-dev). The buttons then acted on another
  row. Fixed now (decided autonomously): such a bar selects no row, and the buttons are disabled without one (CanExecute);
  a double-click focuses on the bar. Revealing a bar's row by expanding its path goes to the Backlog.
- **M14 Inspection rows with a scope asked about the whole type** (senior-dev). Fixed now (decided autonomously):
  "Objects retained by this set" is off for scoped rows (Backlog: sets narrower than a type); "Show in dominator tree"
  shows a row's object when it names one.
- **M15 Test gaps and the fake's fidelity** (senior-dev). Fixed now (decided autonomously): the fake builds cells by
  column name with real child counts, the trail from parent keys and the reveal index; tests for the trail and its
  crumbs, leaves, the focus row, a bar with no loaded row, a type with no top-level group, keyboard collapse, the row
  limit, grouped "more" rows, and scoped inspection rows.

- **M16 The grouped-mode fix put hashing and a delegate in per-object loops** (gc-specialist, re-review). A dictionary
  update per child and a capturing delegate per child of an open group, twice, against DEVELOPER 4. Fixed now (decided
  autonomously): dense pooled sums per group, reset only where touched; `RetainedOrder.Top` takes a struct filter
  (`GroupMembers` reads the type column as a span, no delegate); an open group's "more" row is its known totals less what
  is shown, so the list is read once for the sums and once per open group.
- **M17 Set retention's peak memory was never measured** (gc-specialist, re-review; DenseHeap projected at about
  4.15 GB). Fixed now (decided autonomously): the `dominators` benchmark gained a set retention phase (the type entry with
  the most objects, and its lower bound, as the worker computes them): Medium 5.2 s for 16M `System.Byte[]`, peak
  3,300 MB; DenseHeap 3.0 s, peak 3,876 MB, no higher than the dominators' own. Both within the 4,096 MB budget.

## Confirmations

The fixes went back to the reviewers who raised them (2026-10-26), who re-read the code and tests:
- solution-architect: H1 and H4 confirmed; M1 to M3 (its parts), M4, M5, M6 confirmed. Leftovers listed below (L25 to L33).
- senior-dev: H3 confirmed (the new list's own row, `OnRowsApplied`, `ScrollToSelection`, keys deciding from the open
  set, the view test pressing Right then Left on the grid's own selection); M1, M13, M14, M15 and its part of H1
  confirmed; no new High or Moderate.
- gc-specialist: H1, H2 and H5 confirmed, the in-memory budget confirmed as met after the working arrays are collected
  ahead of the children; M10 and M11 confirmed; raised M16 and M17 (fixed above).
- H1 to H5 are Fixed and confirmed.

## Low / Trivial (listed only)

- L1 `retained-by` errors were not logged (support): the worker now refuses an unknown type before computing, which is
  logged; done in passing with M5.
- L2 An unmatched type name is logged verbatim by the request line (support).
- L3 The algorithm version is logged as a packed number ("dominator tree 65538") (support).
- L4 `Provenance` writes `*Address` properties in decimal (support).
- L5 The CLI command line logs hex addresses and option choices by length (support).
- L6 Collapsing a row keeps its descendants' keys (support, solution-architect, senior-dev); keys of another grouping are
  now dropped.
- L7 The smoke drill logs no address for the row it expands and focuses (support).
- L8 `retainers` JSON lacked `--depth` and `--top` (support): now echoed.
- L9 DEVELOPER lacked the `--address` errors (support): now listed.
- L10 `RetainedBySetViewModel` logged under the Retainers category (support): its own now.
- L11 The first top-level rows could log the kind of root holding them (support).
- L12 The tree's result set took a snapshot lease it does not need (solution-architect): removed.
- L13 The icicle has no truncated flag at 20,000 bars (solution-architect, gc-specialist).
- L14 Retainers targets bypass the navigator's history (solution-architect): for 2.7.
- L15 Two installed versions rebuild each other's dominators sidecar (solution-architect).
- L16 `retained-by` read cells by position (solution-architect): by name now.
- L17 Toggle decided from a stale row; Enter on the focus row re-queried (senior-dev): fixed with H3.
- L18 `_expanded` and selection state were not reset on a failed query (senior-dev): reset now.
- L19 `FindLoadedRow` fetches as a side effect (senior-dev).
- L20 Kinds as literals and a module-less type group losing its links (senior-dev): fixed with M2.
- L21 The context menus act on the selected row even from the header (senior-dev).
- L22 Left and Right were always marked handled; the icicle reacted to any button (senior-dev): fixed.
- L23 The "more" sum had no cancellation check (senior-dev): fixed with M4.
- L24 Frozen members count as retained by their set (gc-specialist); consistent with per-object retained sizes.
- L25 Expanding a "more" row lost the selection (senior-dev, re-review): the first row of the new batch is selected now.
- L26 Left and Right were still marked handled on a leaf (senior-dev, re-review): Expand and Collapse have CanExecute now.
- L27 The view test focused the grid again before Left (senior-dev, re-review): removed.
- L28 The chunked stack allocated a chunk at every crossing of a chunk boundary (senior-dev, gc-specialist): one spare
  is kept.
- L29 Stale comments: children "sorted by retained size" in `DominatorIndexBuilder`, "a type that matches nothing
  answers with no rows" in `IWorkerService` (gc-specialist, solution-architect): fixed.
- L30 A row at the depth limit shows an expander that does nothing, with no note (gc-specialist).
- L31 The positive case of "Show in dominator tree" from a type with a top-level group is untested (the fake ignores
  grouping) (senior-dev).
- L32 A duplicate-strings row's "Show in dominator tree" focuses on its sample string (senior-dev): a tooltip could say so.
- L33 The acceptance check accepts any row of the item type at the depth reached, not only one under the chain
  (solution-architect); a row past the first page now has a reveal test (`A_row_expanded_past_the_first_page_stays_
  selected`); `ValueBoundTexts.Marker` is unused (solution-architect).

## Measurements

Release, 32 GB machine, 2026-10-26. 2.3.7's code was run the same day for comparison (dominators on Medium 8.2 s in
memory, retained by type 5.6 s), since the machine was slower than when 2.3 recorded 3.2 s.

| Run | Before the review | After |
|---|---|---|
| Medium dominators in memory (`dominators`) | 13.4 s, peak 3,747 MB | 6.4 to 6.9 s (children 0.7 s), peak 3,291 MB |
| Medium dominators mapped | not run | 11.4 s (children 1.1 s), peak 697 MB; 1,097 MB kept, reopened in 0.42 s |
| Medium retained by type | 4.1 s | 2.6 to 3.2 s |
| DenseHeap dominators in memory | not run | 7.3 s, peak 3,877 MB (4,289 MB before the working arrays were collected ahead of the children) |
| DenseHeap dominators mapped | not run | 10.6 s, peak 658 MB; 1,031 MB kept |
| Medium set retention (`retained-by`, 3.2M objects) | 3.3 s | 3.3 s, now with its memory reserved |
| Medium tree query, dominators open | 0.03 s | 0.03 s, also grouped by type with two levels open |
| Set retention in memory (`dominators`' set phase) | not measured | Medium 5.2 s for 16M `Byte[]`, peak 3,300 MB; DenseHeap 3.0 s, peak 3,876 MB |
| UI smoke, Small dump, Retainers drill warm | lag 50 ms, operation 32 ms | lag 58 ms, operation 62 ms; exit 0 |

Gate notes: one full run failed `LoaderKnownAnswerTests.Cancellation_stops_the_load_within_two_seconds` (2.7 s against
2 s) while the new LeakyApi acceptance test ran beside it; the loader is unchanged this sprint and the test passed three
times alone (2.0 s). A load-sensitive timing bound, like the Backlog's dispatcher lag item. The UI smoke run after the
fixes: open phase lag 58 ms, operation 52 ms; Retainers drill warm lag 58 ms, operation 62 ms; exit 0. Its log shows
one trace id from the UI's "expand" line through the worker's tree line (trail, open keys, revealed row, rows under it)
to the answer line.
