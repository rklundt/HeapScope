# Review: Sprint 3.1 Object sets

Changes since `sprint-2.7` that belong to 3.1 (the Live timeline's Open, Show in folder and Delete came in their own pull
requests before the sprint). Four perspectives, run in parallel: solution-architect, senior-dev, gc-specialist, and a
support engineer (a general reviewer with a read-only brief, so the logs can back up any result a user contests).
Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go with your reco for moderate
recommendations"), marked "decided autonomously". None of the reviewers had a shell; each read the files directly.

Totals after dedupe: 4 High, 15 Moderate, 26 Low (7 Lows from the re-review of the fixes). A finding raised by more than one reviewer is merged and keeps the
highest severity.

## High / Urgent

- **H1 A multicast event's row opened the shared multicast delegates, not the subscribers it counted** (gc-specialist).
  For an event whose field holds a multicast delegate, the detector's pending entry kept the multicast delegate as its
  value, and the row's member list recorded that value: a row of 300,000 subscribers on 1,024 publishers opened about
  1,024 objects. Fix: the pending entry carries the counted subscriber delegate and its target; a row's members are the
  objects whose bytes it counts (each subscriber delegate and its target), never the multicast delegate. Tests:
  `LeakDetectorTests.An_event_holding_many_subscribers_...` (2,400 distinct members, their bytes are the row's, no
  member is a multicast delegate); `DetectorTests.A_detector_row_opens_exactly_the_objects_it_counted_as_a_set` opens an
  event row of the LeakyApi fixture as a set. Status: Fixed; confirmation in the section below.
- **H2 The set's Dominators view could use several times the memory it reserved** (solution-architect; gc-specialist
  as Moderate). The walk used a doubling `Stack`, the top members were collected in doubling lists and copied by LINQ,
  and the rows were sorted by `OrderBy` after the reservation ended: about 50 to 60 bytes per top member against 8 per
  object reserved, so a namespace set with tens of millions of top members could take the worker past its budget.
  Fix: `SetDominators.Top` walks the dominator tree twice on the chunked stack (the 2.4 review's fix of the same
  pattern), marks the tops in a bit mask, fills exact arrays, and sorts with `Array.Sort` on packed keys, all inside a
  reservation of 37 bytes per object (raised from 28 at the re-review); the result source only indexes the sorted arrays. Tests: `ObjectSetTests`
  (`The_sets_dominators_are_the_members_no_other_member_dominates`, most retained first) and the integration test
  `What_a_set_retains_is_a_set_and_its_dominators_divide_the_retention`. Status: Fixed; confirmation below.
- **H3 The set dominators' rows read the graph without a lease** (senior-dev). The result source formats rows from the
  graph and the dominators on every page, but was stored without a lease, so a snapshot closed while its pages were read
  could have its mapped files read after disposal. Fix: the source keeps a lease of its own while it lives. Status: Fixed;
  confirmation below.
- **H4 A disputed set view could not be traced in the logs** (support; senior-dev as Low). Requests carrying a set
  logged its shape only by length, a nested set by its type name, a cached set nothing, and no line said which types,
  namespace, assembly or row a set was. Fix: the provenance writes a set as its shape: the first 20 type keys with their
  modules, the namespace or assembly, the inspection, row and baseline, and for objects their count and a short hash of
  the sorted addresses (never the addresses); a cache hit logs the set with its size; the resolved line adds how each
  type key matched (and the keys that matched nothing), and for an inspection row the rule version and whether the
  row's own objects or its hidden columns were used. Tests: `ProvenanceLogTests.A_set_is_logged_by_its_shape_...`.
  Status: Fixed; confirmation below.

## Moderate

All decided autonomously.

- **M1 The spec's computed texts were sent on the wire** (solution-architect, senior-dev, support). `Shape` and
  `CacheKey` were public properties of the DTO, which the JSON formatter serializes: an objects set of 100,000 addresses
  sent another 1.5 MB string, against ADR-016. Fix now: they are extension members (`ObjectSetSpecs`), never
  serialized; test `A_specs_texts_are_not_sent_and_every_kind_has_its_own_cache_key`. Fixed.
- **M2 Resolving a set took no memory reservation, and its cache was outside the budget** (gc-specialist,
  solution-architect). Fix now: every resolution builds inside a reservation of what one set can take; the cache keeps
  at most 256 MB of sets and never a set over half of that. Letting the reservations evict the cache before refusing
  is deferred to the Backlog (the cache is small and bounded). Fixed in part.
- **M3 Inspection tables were counted by number, not bytes** (solution-architect, senior-dev, gc-specialist). Detector
  rows' member arrays were not in `EstimatedBytes`, and 8 tables of 14 inspections pushed detector tables out. Fix now:
  member arrays are counted; only tables that list members are kept, within 128 MB. Fixed.
- **M4 A detector row and its set gave different figures** (gc-specialist). Event rows counted delegates and targets but
  opened delegates; timer rows count timers but open both timers of a `System.Timers.Timer`. Fix now: one rule, the
  members are the objects whose bytes the row counts (ARCHITECTURE 11 and ADR-030 say so). Fixed.
- **M5 Opening a scoped row copied a type's objects three or four times** (gc-specialist; solution-architect and
  senior-dev as Low). Fix now: `InstancesSource.Select` counts, then fills one exact array; `ObjectSet.OfIndices` takes a
  span. Fixed.
- **M6 A new set kind would share cache keys; the baseline binding covers inspection rows only** (solution-architect).
  Fix now: every kind has its own key and an unknown kind is refused (tested). Generalizing the baseline into "the
  other snapshot" for 3.3's dead objects and 3.5's growth is deferred to 3.3. Fixed in part.
- **M7 Two views of a new set resolved it twice** (senior-dev; gc-specialist and solution-architect as Low), and both
  copies were cached, also after the snapshot closed. Fix now: one resolution per set at a time, shared; adding a key
  replaces it; nothing is added for a closed snapshot. Fixed.
- **M8 A growing collection's row opened a set of one collection** (senior-dev): the row's counts are its elements.
  Fix now: a detector row that names one object opens that object, as before 3.1. Fixed.
- **M9 Selections quietly left out rows not loaded yet, and the type limit was checked only in the worker**
  (senior-dev). Fix now: the types and Compare grids refuse with a status saying why. Fixed.
- **M10 The CLI split `--type` on commas, which breaks generic names** (senior-dev). Fix now: repeat `--type`. Fixed.
- **M11 A set of many types scanned the type table once per type** (senior-dev). Fix now: one pass with a lookup,
  off the RPC thread, with cancellation. Fixed.
- **M12 Replaying an inspection row went through the RPC method again** (senior-dev): a guard inside a guard, a result
  set made to be released, and an internal error when the rows were not kept. Fix now: an internal core runs the
  inspection; its rows are used directly. Fixed.
- **M13 The inspection behind a set row was not logged when it ran again** (support). Fix now: the re-run is logged with
  its reason, and the resolved line names the rule version and the row's source. Fixed.
- **M14 Opening a set and switching its views were not tied to the worker's lines** (support). Fix now: both run in a
  `HeapScope.Sets` activity that the view's query starts inside, and log the snapshot and the set; Compare logs which
  snapshot it used and the types it left out; bookmarks log the snapshot. Fixed.
- **M15 Missing tests** (senior-dev, support). Added: the limits (too many addresses, no types, unknown types, too
  deep), provenance of set requests, the JSON shape, cache replacement and the table budget, a closed set opening no
  views, an event row as a set on the fixture. Deferred to the Backlog: a view model test of a set replayed after a
  restart (the integration test replays one after a worker restart), a ClrMD check of the Generations and Dominators
  views, and a test that a set's worker lines carry the UI's trace id. Fixed in part.

## Low / Trivial

Listed only (DEVELOPER.md 10); fixed in passing where noted.

- L1 The objects cache key joined every address into one string on the large object heap (gc-specialist). Fixed: the
  count and a hash.
- L2 The set dominators walk the whole tree even for a few bookmarked objects, and the Retained view and "open as set"
  compute the same retention twice (gc-specialist). Backlog.
- L3 A set's frozen members (literals, `RuntimeType`) count in its retained bytes though they are never collected
  (gc-specialist). Backlog: say them apart in the Retained view.
- L4 Compare's mixed selection quietly dropped the types only in the baseline (solution-architect). Fixed: they are left
  out with a status.
- L5 The namespace spec's contract said "empty for none" where the rows say "(global)" (solution-architect). Fixed in
  the contract's text.
- L6 The CLI printed its own text for a gcdump's generations and ignored a lone `--baseline` (solution-architect). Fixed.
- L7 The Contracts code map and `RetainedBySetResult.MatchedTypes` text lagged (solution-architect). Fixed.
- L8 Cache evictions were not logged (support). Fixed at Debug.
- L9 An unknown inspection inside a set was refused twice (support). Fixed with M12.
- L10 A set's member list logged as "Instances" (support). Fixed: "SetInstances".
- L11 Set retention logged a lower bound of 0 for sets that are not whole types (support). Fixed: "n/a".
- L12 The CLI logs `--address` values unlike the worker (support). Backlog.
- L13 A set's title can carry a row's text (a duplicate string); it never reaches a DTO or a log today (support). Noted:
  titles stay out of DTOs.
- L14 A closed set could still open views (senior-dev). Fixed.
- L15 The types grid's retained fallback dropped the set (senior-dev). Fixed.
- L16 Scoped inspection rows still could not offer what they retain (senior-dev; 2.4 M14). Fixed: through their own set.
- L17 The table cache did not move a table on use, and the key's case differed (senior-dev). Fixed.
- L18 The shortcut switches on view model types; selections build type keys in two places; the session repeats its
  query pattern (senior-dev). Backlog refactor.
- L19 An unused test variable; three types in one file (senior-dev). Fixed.

## Confirmations

Each reviewer re-read the fixes (commit 6bd61a9 and the docs after it), read only.

- H1: Confirmed by gc-specialist (the counted delegate and its target are the members; the tests would fail on the old
  code).
- H2: Confirmed by solution-architect and gc-specialist (chunked walks, exact arrays, the sort inside the reservation, the
  source indexes only). Both found the end of the computation holds 36 bytes per top against the 28 reserved; raised to
  37 bytes per object, with the comment corrected.
- H3: Confirmed by senior-dev (the source holds its own lease).
- H4: Confirmed by support (a set at any depth logs as its shape; cache hits log; names, matches and the inspection
  row's source are in the resolved line). Its corrections were applied: the documentation says the keys' matches as
  logged, unmatched keys are named with their modules, missing addresses count distinct ones, and DEVELOPER.md says how
  the `#hash` is computed.
- Moderates: all confirmed as fixed or fixed in part. M15 stays fixed in part: the single-flight logic has no
  concurrency test, `MaxTypes + 1` is checked in the client but not in a worker test, a set step replayed after
  eviction has no view model test, and the Generations and Dominators views have no ClrMD check; decided autonomously:
  to the Backlog (acceptance criterion 2 is met by the views agreeing with the whole-snapshot queries and set retention
  with ClrMD; criterion 3 by the integration test that replays a set after a worker restart).

New Lows from the re-review (listed, not raised):
- L20 A set over half the cache budget is never kept, so each of its views resolves it again (gc-specialist,
  senior-dev, support). Fixed in part: the resolved line says so; caching such sets' masks only: Backlog.
- L21 An event row's bytes count a shared target once per subscriber while its set counts it once (gc-specialist).
  Backlog.
- L22 The closed-snapshot check and the cache add are two steps, and `AddTable` has none (senior-dev, gc-specialist).
  Backlog.
- L23 Callers that join a running resolution get no progress and log no line of their own; a finished failed
  resolution can be handed once more (senior-dev, support, solution-architect). Backlog.
- L24 The detectors still build their member lists with `List<int>` (solution-architect). Backlog.
- L25 The set's path step id is not in the set's log line (support). Backlog.
- L26 A set's cache key and shape were computed several times per query (gc-specialist). Fixed: once per resolution.

## Gate

2026-10-28: Debug and Release builds with zero warnings; 1,238 tests, 3 skipped. In full parallel runs on this machine,
one or two timing tests of areas the sprint did not touch failed, different ones each run (a synthetic job's
cancellation, a hanging call's cancellation, the session library's concurrent records); each passes alone, as in 2.7.
The object set integration tests (8, on the leaks fixture and the LeakyApi detector run) pass.
