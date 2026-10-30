# HeapScope sprints: Phase 3 (depth to match dotMemory) and the peer review sprints

Closed sprints moved here from [SPRINTS.md](../SPRINTS.md) in sprint 3.8; each keeps its goal, scope,
acceptance criteria, decisions, tasks and review summary as they stood when it closed. Reviews are in
[docs/reviews/](../reviews/).

## Phase 3: depth to match dotMemory

Planned 2026-10-28. The product owner found HeapScope "well under powered for what dotMemory offers" after Phase 2.
Phase 2 covered most of dotMemory's feature list (`docs/research/dotmemory-ux.md` 7) but each feature shallower. Phase 3
deepens the analysis in five sprints, ahead of 2.8 Packaging (which still waits for the audiences and channels). The
product owner agreed to this plan ("lets do your phase 3"); anything it leaves out is listed under Phase 4 candidates.
Phase 3 runs like Phase 2: autonomously, one sprint at a time, stopping after each close.

---

## Sprint 3.1: Object sets

**Goal**: any selection is a subject with every view, as in dotMemory's object set model.

**Scope**: an object set as the analysis subject: several types (multi-select in the types grid), a namespace or an
assembly, Compare's New, Dead and Survived counts of a row, an inspection's or a leak detector's row (exactly the objects
it counted), the bookmarked objects, and the objects retained by a set. Each set shows the views that apply: Types (with
the groupings), Instances, Dominators (the set's top-level dominators), Generations, and "objects retained by this set".
Sets join the analysis path as steps.

From the backlog: object set queries; set retention of a namespace or an assembly (2.4); sets narrower than a type (2.4
M14); detector rows opening exactly the objects they counted (2.6 M24); a types row opening only its own method table's
objects (1.5 M3); placing a type's objects in the dominator tree (2.4 M1).

**Acceptance criteria**:
- Each selection above opens as a set: several types, a namespace, an assembly, a Compare row, an inspection row, a leak
  detector row (exactly the objects it counted), the bookmarks of a snapshot, and what a set retains.
- On the LeakyConsole fixture each kind of set has known counts and bytes, and the Types, Instances, Generations,
  Dominators and Retained views of a set agree with the same numbers computed by hand from the graph.
- A set is a step of the analysis path and replays after its result sets are evicted or the worker restarts.
- The CLI's `set` command answers the same views as the app.

**Dependencies**: 2.4, 2.6, 2.7.

**Decisions** (all decided autonomously, 2026-10-28, Phase 3 autonomy):
1. A set is described by an `ObjectSetSpec` that the client keeps and the worker resolves into its member objects (ADR-030).
   There is no set handle on the wire: a spec survives result set eviction and worker restarts, and the analysis path
   replays it. The worker caches resolved sets per snapshot (a few, memory reserved like other large computations).
2. Spec kinds: types (one or more name and module keys), a namespace, an assembly, objects (addresses, up to 100,000), an
   inspection row (inspection, baseline, row), and the objects another set retains. The single-type scopes of 1.6
   (large object heap, finalizable, same text) stay on Instances; inspection rows reach them through the row kind.
3. A set's views: Types (by type, namespace or assembly, with filter and sort), Instances (every member, any type),
   Generations (objects and bytes per heap kind), Dominators (the members no other member dominates, with retained
   bytes; a row opens the Retainers tab at that object) and Retained (the exact "objects retained by this set").
   The Types view of a set has no retained lower bound column: per-group bounds need per-object groups, and the
   Retained view gives the exact figure. A Types row opens the set's members of that type.
4. Leak detector rows open exactly the objects they counted: inspections can list a row's members; the other
   inspection rows keep their exact scopes (same text, large object heap, finalizable).
5. Compare's New, Dead and Survived counts stay in 3.3: telling them apart needs object identity across snapshots,
   and addresses move (ADR-018). In 3.1 Compare rows open the type keys as a set.
6. 1.5 M3 (a types row opening only its own method table) stays in the backlog: sets match types by name and module
   like every other query. 2.4 M1 (placing a type's objects in the dominator tree) is answered by the set's Dominators view.
7. Logging: every set query logs the spec's shape (kind, key or address count, inspection and row) and the resolved
   members and bytes, so a disputed number traces to what was counted; never addresses' contents or string values.
8. Protocol 16.

**Task breakdown**:
1. [x] Contracts: `ObjectSetSpec` and its kinds, `Set` on `InstancesRequest`, `TypeSummaryRequest` and
   `RetainedBySetRequest`, a set dominators query, ADR-030, protocol 16. Tests: DTO round trips, protocol version.
2. [x] Analysis: `ObjectSet` (sorted member indices and a bit mask), aggregation over a set, set retention over an object
   mask, the set's top dominators. Tests: synthetic graphs with known answers for each.
3. [x] Inspections: a row's members (`InspectionTable.MembersOf`) for the leak detectors. Tests: each detector's rows
   list exactly the counted objects on the fixture.
4. [x] Worker: spec resolution with a per-snapshot cache and reservation, the four queries over a set, logging. Tests:
   each spec kind on the fixture, cache reuse, bounds (too many addresses, unknown types, unknown inspection row).
5. [x] Application and CLI: session methods and a `set` command (`--type`, `--namespace`, `--assembly`,
   `--address`, `--inspection` with `--row`, `--retained`; views `types`, `instances`, `generations`, `dominators`,
   `retained`). Tests: CLI output on the fixture.
6. [x] View models: a Set subject with its views, the path step and its replay, and the entry points (types grid multi
   select and group rows, Compare rows, inspection and detector rows, bookmarks, the retained result). Tests: each entry
   point opens the expected spec; replay after eviction.
7. [x] UI: the Set subject view, multi select in the types and Compare grids, the new commands and shortcuts. Tests: view
   tests by keyboard, no binding errors.
8. [x] Docs: ARCHITECTURE (code map, protocol, ADR-030, privacy of logs), DEVELOPER, README, this section.

**Review summary** (`docs/reviews/sprint-3.1.md`; four reviewers: solution-architect, senior-dev, gc-specialist and a
support engineer): 4 High, 15 Moderate, 26 Low (7 from the re-review).
- High, all fixed: an event row of a multicast event opened the shared multicast delegates instead of its subscribers
  (H1); the set's Dominators view could use several times the memory it reserved (H2); its rows read the graph without a
  lease (H3); a disputed set view could not be traced in the logs (H4: sets are now logged by their names, matches and
  sizes in every query, cache hits included).
- Moderate, decided autonomously: 12 fixed (specs' texts kept off the wire, reservations and byte budgets for sets and
  detector tables, one rule for a detector row's members, single resolution per set, client-side limits and unloaded
  rows, the CLI's `--type`, one pass over the type table, the inspection replay core, the logging of replays, activities
  for set actions); 3 fixed in part with the rest deferred (the cache evicted by reservations, the "other snapshot"
  binding for 3.3, three tests).
- Low: 16 fixed in passing (scoped inspection rows now offer what they retain through their own set, 2.4 M14), 10 to the
  Backlog. Every High was confirmed by the reviewer that raised it.
- Deferred to the Backlog: below.

---

## Sprint 3.2: Retention views

**Goal**: see why many objects are kept alive at once, not only one path at a time.

**Scope**: Similar Retention (a set's objects grouped by retention pattern, each group with its most different paths to
roots); key retention paths drawn as a diagram (the distinct paths merged into a graph); group by interface in the types
grid and Compare.

**Acceptance criteria**:
- On the RetentionPatterns fixture (objects held by three known patterns: a list only, the list and a dictionary, an
  array of holders only) Similar Retention of the item type gives exactly three groups with the known counts, each group
  showing the paths of its pattern; a group opens as a set with exactly its objects.
- A set has key paths (the 2.2 grouping over its members); for a set of one type they equal the type's key paths.
- The key paths diagram merges the paths into one node per root and per type, with edges carrying the objects that pass
  them (view model tests), and is drawn on the type's Paths subject and the set's Paths view.
- Group by interface in the types grid and Compare: on the fixture each leak class counts under `ILeak` and the
  disposable fixtures under `System.IDisposable`, matching ClrMD; an interface row opens as a set; gcdumps refuse the
  grouping with a reason.

**Dependencies**: 3.1 (groups open as sets), 2.2.

**Decisions** (all decided autonomously, 2026-10-28, Phase 3 autonomy):
1. A member's retention pattern is the set of distinct key path signatures (2.2: the shortest path from a root, the
   types on the way with repeats folded) of its direct referrers that a root reaches, plus its own root when it is one.
   Members with the same pattern form a group (dotMemory's Similar Retention). At most 16 referrers per member are
   looked at; the result counts the members that had more.
2. A group shows how many distinct paths its pattern has and at most two of them: the first, and the one sharing the
   fewest steps with it ("the most different paths"). Groups rank by objects, then bytes, then their first member; 50
   by default (1 to 500), the rest summed. Groups are numbered from 1 in the app, the CLI and the logs (review).
3. A group opens as a set: spec kind `RetentionGroup` (the inner set and the group's pattern fingerprint, its rank as a
   hint; review). The worker groups the inner set again to resolve it, inside a memory reservation.
4. A set's key paths: `TypePathsRequest` takes a `Set` (the type name then empty), like `InstancesRequest` in 3.1.
5. The diagram is built in the view model from the key paths: a node per root (by its description), per type and for the
   set; an edge per consecutive pair, carrying the summed objects of the paths through it. Layers by first depth; an
   edge back to an earlier layer is drawn as such. The list stays as the keyboard and screen reader form.
6. Interfaces are read from the dump (ClrMD's interface map of one object per type, inherited interfaces included), once
   per type and snapshot, and kept by the worker. A type counts under each interface it implements (the rows overlap;
   the view says so) and types without one under "(no interface)". gcdumps record no interfaces: the grouping is
   refused there. The retained column is not offered in this grouping (a type in several groups breaks the lower bound's
   numbering). Interface rows open as sets (kind `Interface`). Compare groups both snapshots by their own interfaces.
7. Logging: Similar Retention logs the set's shape, its groups, unreachable and limited members, and the top five groups
   with their counts, bytes and shown paths (types and roots are metadata); the interface table logs how many types
   were read and how many failed.
8. Protocol 17.

**Task breakdown**:
1. [x] Contracts: `TypeGrouping.Interface`, set kinds `Interface` and `RetentionGroup` (shape, cache key, factories),
   `TypePathsRequest.Set`, the Similar Retention query and result, protocol 17. Tests: round trips, keys, version.
2. [x] Analysis: `SimilarRetention` (patterns, groups, the most different pair, a group's members), key paths over a set,
   interface grouping in `TypeGroups` and `TypeDiff`. Tests: synthetic graphs with known groups and rows.
3. [x] Reader and fixture: `IObjectReader.ReadInterfaces`; the RetentionPatterns fixture (`--retention-patterns N`) with
   its known answer. Tests: known answers with ClrMD; interfaces of fixture types.
4. [x] Worker: the interface table, interface grouping in summaries and diffs (refused for gcdumps), the two new set
   kinds, set key paths, Similar Retention with logging. Tests: worker tests and integration on the fixture.
5. [x] Application and CLI: session methods; `--group interface` on `summary` and `diff`; `set` views `paths` and
   `similar`, and `--group N` for a Similar Retention group. Tests: CLI output on the fixture.
6. [x] View models: the set's Paths and Similar Retention views, group rows opening as sets, the diagram model, the
   interface grouping in the types grid and Compare, a Similar Retention command on a type's Paths subject. Tests.
7. [x] UI: the views, the diagram control, list and diagram switch. Tests: view tests, no binding errors.
8. [x] Docs: ARCHITECTURE (code map, protocol, ADR-031 Similar Retention, privacy of logs), DEVELOPER, README.

**Review summary** (`docs/reviews/sprint-3.2.md`; four reviewers: solution-architect, senior-dev, gc-specialist and a
support engineer): 7 High, 15 Moderate, 24 Low (3 Moderate and 5 Low from the re-review).
- High, all fixed: a back pointer from a member's child counted as retention (H1); interfaces missed inherited and
  generic ones (H2: base types, base interfaces, and generic instantiations decoded from module metadata, from the module
  file when the dump does not hold the image); Similar Retention's memory was mostly unreserved (H3); the diagram double
  counted a group's overlapping paths (H4); a growth gate over overlapping interface rows (H5, refused); the interface
  table read the graph without a lease (H6, now a derived slot); unread interfaces shown as "(no interface)" (H7, own row).
- Moderate, decided autonomously: 10 fixed (numbering from 1 everywhere with `--similar-group`, group sets named by
  their pattern's fingerprint, every group logged, the retained note and sort fallback, refusals with next steps,
  interface grouping offered only on full dumps, no per-member allocation, edge case and address tests, diagram gaps and
  set labels, unread types logged), 2 in part (a per-grouping capability table and the result's index info deferred);
  from the re-review, nested generic types' arguments and the module file's identity fixed, the exact back pointer test
  deferred. Every High was confirmed by the reviewer that raised it.
- Low: 12 fixed in passing, 12 listed.
- Deferred to the Backlog: below ("From the 3.2 review").

---

## Sprint 3.3: Comparison depth

**Goal**: comparing snapshots answers what grew and what keeps it, across more than two snapshots.

**Scope**: Compare's counts open their objects as sets (from 3.1, with the snapshot asked where it matters: dead objects
only exist in the baseline); retained bytes in diffs (a type's retained lower bound in each snapshot and the delta, from
2.3 decision 7); trends across several snapshots of a session (from the backlog's trend analysis); comparing snapshots of
different sessions; diff growth by generation (from 1.4). From the 3.1 review (M6): a set's baseline generalized into "the
other snapshot" (dead objects only exist in the baseline), bound to the current ids at each call like an inspection row's.

**Acceptance criteria**:
- On two LeakyConsole dumps of one process, a Compare row's New, Dead and Survived objects equal the address matches
  computed with ClrMD: New and Survived open as sets in the current snapshot, Dead as a set in the baseline, and the
  grid's New and Dead columns agree with those sets' counts. Dumps of different processes refuse it with the reason.
- Compare can show each row's retained lower bound in both snapshots and the delta; on the fixture each row equals the
  type summary's retained column of its snapshot.
- A trend of three or more snapshots lists each row's objects and bytes in every snapshot, the change from first to last
  and the steps it grew in; on three fixture dumps with growing leaks the leak types grew in every step.
- Compare shows per-generation byte deltas when both snapshots have generations, and the CLI's growth gate can check one
  heap kind (`--growth-of gen2`) and each row on its own (`--fail-on-growth-per-row`).
- Snapshots from different sessions compare like snapshots of one session.

**Dependencies**: 3.1.

**Decisions** (all decided autonomously, 2026-10-28, Phase 3 autonomy):
1. Object identity across dumps by address (ADR-032, amends ADR-018): only for two complete full dumps of one run of a
   process (same process id and name, the current captured later, the baseline's frozen objects found; review). A current
   object is Survived when the baseline has an object of the same method table, type key, size and heap area, in no
   older generation, at its address (review), otherwise New; a baseline object with no object of its type key at its address in
   the current snapshot is Dead. A compacting collection moves objects, so a moved survivor counts as Dead and New; the
   columns and sets say "by address". Computed by one merge of the two snapshots' sorted addresses.
2. Compare rows grouped by type get New and Dead columns when asked (`TypeDiffRequest.MatchObjects`); a row opens its New
   or Survived objects as a set in the current snapshot and its Dead objects as a set in the baseline. Spec kind
   `Compared`: the inner set (the row's types), the change, and the other snapshot. The 3.1 review's M6: an inspection
   row's baseline and a compared set's other snapshot are one "other snapshot" field, bound to the current ids at each
   call.
3. Retained bytes in Compare (from 2.3 decision 7): `TypeDiffRequest.IncludeRetained` computes or opens both snapshots'
   dominators and adds each row's retained lower bound in both and the delta. Not for interface rows.
4. Trends (`QueryTypeTrendAsync`): 2 to 20 snapshots of one kind (review) in the order given (the app orders them by capture time), rows per
   type, namespace, assembly or interface, with objects and bytes per snapshot, the change first to last and the number
   of steps the bytes grew in. In the app a Trend view beside the two-snapshot comparison uses the open snapshots the
   user ticks; the CLI's `trend` takes the dumps in order.
5. Growth by generation (from 1.4): `TypeDiffResult` and the rows carry byte deltas per heap kind when both snapshots
   have generations (Compare's Generations switch shows them). The CLI's `--growth-of total|gen0|gen1|gen2|loh|poh`
   picks what the gate checks, and `--fail-on-growth-per-row` fails when any one matching row grows more than the
   limit (its percent against that row's baseline).
6. Different sessions: Compare already pairs any two open snapshots; a test states it for two session folders.
7. Logging: a diff logs whether it matched objects (and why not), the New and Dead totals, whether retained bytes were
   added and from where; a trend logs its snapshots in order, grouping and row count; the gate logs what it checked.
8. Protocol 18.

**Task breakdown**:
1. [x] Contracts: `TypeDiffRequest` `MatchObjects` and `IncludeRetained`, the new diff columns (New, Dead, retained in
   both and delta, per-kind deltas), the set kind `Compared` with `ObjectChange` and the other snapshot, the trend query,
   protocol 18. Tests: round trips, cache keys, version.
2. [x] Analysis: `ObjectIdentity` (the address merge: per type New, Dead and Survived counts, and the members of a change
   for a set), per-kind deltas and retained columns in `TypeDiff`, `TypeTrend`. Tests: synthetic graphs with known
   answers, moved objects, different type keys at one address.
3. [x] Worker: diff with matching, retained and kinds; the `Compared` set kind resolved against the other snapshot under
   both leases; the trend query; logging. Tests: integration on two fixture dumps of one process (ClrMD address matches),
   different processes refused.
4. [x] Application and CLI: session methods (`ObjectSetQuery.Baseline` as the other snapshot), `diff --match-objects --retained --growth-of
   --fail-on-growth-per-row`, `trend`, `set --compared-with <dump> --change new|dead|survived`. Tests: CLI on the fixture.
5. [x] View models and UI: Compare's New and Dead columns, retained and generations switches, a row's New, Dead and
   Survived as sets, the Trend view with its snapshot choice. Tests: view model and view tests, no binding errors.
6. [x] Docs: ARCHITECTURE (ADR-032, protocol, logging), DEVELOPER, README, this section.

**Review summary** (`docs/reviews/sprint-3.3.md`; four reviewers: solution-architect, senior-dev, gc-specialist and a
support engineer): 5 High, 14 Moderate, 25 Low (5 from the re-review).
- High, all fixed: a set kind missing from a test (H1); matching allowed for gcdumps, mixed and incomplete dumps (H2);
  a reused address counted as a survivor (H3: method table, size, heap area and generation now checked); the per-row
  gate's log line was false (H4); two runs reusing a process id were matched (H5: frozen objects must be found).
- Moderate, decided autonomously: 11 fixed (the meaning of the counts stated everywhere, the capture advice reconciled,
  a percent with a heap kind refused, early sort checks, JSON and stderr provenance, the diff log's first rows, mixed
  kind trends refused, the Trend view's limits and order, the retained delta an estimate, tests, refusals with next
  steps), 1 in part (the CLI refuses an inspection row with `--compared-with`), 2 deferred (identity kept per pair and
  measured; the CLI trend closing each dump in turn).
- Low: 13 fixed in passing, 12 listed.
- Deferred to the Backlog: below ("From the 3.3 review").

---

## Sprint 3.4: Live workflow and housekeeping

**Goal**: the Live page is where a leak hunt starts and ends, and the session library stays tidy.

**Scope**: a Get snapshot button and Force GC on the Live page (where the runtime allows it); recording allocations
while monitoring (from 2.5); timelines recorded in `session.json` and reopening a past run (from 1.7 M3); deleting
snapshots and sessions with their index caches, refused while open, and a retention rule for old sessions (from 1.1);
one capture per target at a time across pages (1.7 M4).

**Acceptance criteria**:
- On the Live page, Get snapshot captures the monitored process (the run's kind) into the run, with a card and a timeline
  marker; Force GC triggers a full blocking collection in the target (its Gen2 collection count goes up) and marks the
  timeline. Both are refused with the reason while a capture of that process runs.
- One capture per process at a time across the app: a second capture of the same process, from any page or a rule, is
  refused (a rule skips its firing and logs why) while the first runs; captures of different processes still run
  together.
- With "Record allocations" ticked, a monitoring run records an allocation trace for its whole length into the session,
  and the trace opens on the Allocations page.
- Each monitoring run is recorded in `session.json` (process, interval, rules, kind, start and end, timeline, trace and
  the snapshots it took); the Live page lists past runs and reopens one's timeline, markers and cards read only.
- A closed snapshot can be deleted with its index caches (next to it or in the fallback folder, sidecars included) and
  removed from its session; an open one is refused with the reason. A session can be deleted the same way. "Clean up
  old sessions" lists the sessions not opened for N days with their sizes and deletes them after confirmation, skipping
  the current session and any with an open snapshot.

**Dependencies**: 1.7, 2.5.

**Decisions** (all decided autonomously, 2026-10-29, Phase 3 autonomy):
1. Force GC: an EventPipe session with the runtime provider's GC keyword and GC heap collect keyword triggers an induced
   full blocking collection in the target, as a gcdump capture does, without writing a dump. Targets of .NET 5 or later
   with a diagnostics port; elsewhere refused with the reason. Logged with the target and the time taken. It moves
   objects, so the run's markers say "GC forced" (3.3's New and Dead objects across it are mostly dead and new).
2. Get snapshot on the Live page captures through the same capture service as the Processes page and the rules, recorded
   as a manual capture of the run.
3. One capture per process at a time: the capture service holds a gate per process id. A second capture or a Force GC of
   that process fails at once with "a capture of this process is running" (a new `CaptureFailure.Busy`); a rule's firing
   is skipped and logged. Different processes are not serialized.
4. Allocations while monitoring: a "Record allocations" switch starts an allocation trace (2.5's recorder) with the run
   and stops it with the run; the trace is written to the session folder and recorded in the run; the Live page opens it
   on the Allocations page. Refused for targets the recorder refuses, with the reason; the run goes on without it.
5. Runs in `session.json`: a `Runs` member (format version stays 1: older builds keep unknown members) with an id, the
   process, the interval, the rules as text, the capture kind, start and end, the timeline and trace files (relative to
   the session folder) and the ids of the snapshots it took. Written when the run starts and again when it ends. The
   Live page lists the current session's runs and opens one read only from its timeline CSV (the counters, its events
   as markers, its snapshots as cards).
6. Deleting: `SessionLibrary` deletes a snapshot (its file, index caches and sidecars, its partial files, its entry) and
   a session (its folder and its snapshots' caches in the fallback folder). The front end refuses a snapshot that is
   open, and a session with an open snapshot, a running capture or monitoring run; every deletion is confirmed by the
   user, lists what goes and its size, and is logged (paths and sizes, never contents). No automatic deletion:
   "Clean up old sessions" on the Home page proposes the sessions not opened for 30 days (or the days the user picks) and
   deletes only after confirmation.
7. Logging: Force GC, Get snapshot, a refused capture (Busy), the allocation trace's start and end, run records written,
   a past run opened, and every deletion (what, size, why refused) are logged.

**Task breakdown**:
1. [x] Capture and Application: `GcTrigger` (Force GC), the per-process capture gate and `CaptureFailure.Busy`, rules
   skipping a busy target. Tests: two captures of one pid, one refused; different pids both run; a rule skips.
2. [x] Application: run records in `session.json` (`SessionRun`), written at start and end with the snapshots and the
   trace; the timeline CSV reader; deleting snapshots and sessions with their caches; old sessions listed. Tests: round
   trips with unknown members kept; deletions of caches next to a dump and in the fallback folder; refusals.
3. [x] Monitoring: allocation trace with the run; Force GC and Get snapshot during a run as markers and cards. Tests:
   fakes for the counter source and the recorder.
4. [x] View models and UI: Live page buttons (Get snapshot, Force GC), Record allocations, past runs list and read-only
   reopening, the trace opened on the Allocations page; Snapshots page "Close and delete"; Home page delete session and
   "Clean up old sessions". Tests: view model tests with fakes, view tests, no binding errors.
5. [x] Integration: Force GC on LeakyConsole raises its Gen2 count; Get snapshot; one capture per target; deletion of a
   real dump's caches; a past run reopened. Tests: integration on the fixture.
6. [x] Docs: ARCHITECTURE (ADR-033 capture gate and run records, session format, logging), DEVELOPER, README, this
   section.

**Review summary** (docs/reviews/sprint-3.4.md): 7 High, 18 Moderate, 27 Low after dedupe across four reviewers
(solution-architect, senior-dev, gc-specialist, support engineer). All Highs fixed and confirmed by the reviewers that
raised them: a session written into could be deleted (now registered in use and refused, deleted under its lock);
deletion could reach outside the library (now only inside the session folder, through no link, root check separator
safe, outside dumps kept); Start's file work ran on the UI thread; deletions were not backed up by the log and a partial
failure lost the record (now planned, listed in the confirmation, logged path by path with the answer, `session.json`
last); a busy rule skip was silent and a once rule could be lost (now logged and re-armed); Stop did not await every
capture and a late one opened a second timeline (now tracked, refused once stopping, timeline opened at start); the
run's allocation trace had no limit (now 1 GB or 1 GB free disk). Moderates: 15 fixed (a partly failed snapshot deletion explained, the Live smoke run no longer writes into the user's sessions and awaits Start, trace refusal shown at once, Force
GC's timeout over the whole call with three outcomes and a confirmation, the .NET 5 check, exact cache names, refusal
logs, runs recorded without a timeline, Force GC's own messages, log-line tests, run and snapshot ids), 1 in part
(trace stopped after captures), 2 deferred (runs parsed one by one, a seam to test the real capture gate). Measured: the
Live workflow integration tests pass on LeakyApi (a forced collection matched by its induced reason, finished in under a
second).

---

## Sprint 3.5: Leak detection depth

**Goal**: the detectors find the leaks dotMemory's inspections find, and the ones .NET apps commonly have next.

**Scope**: finalizable objects (the finalizer queue, and objects finalized between two snapshots); async state machine
and `Task` leaks; the objects a raw dump holds that the GC has not collected (unreachable from roots) as a set; a type
growth detector across two snapshots (from 2.6).

**Acceptance criteria** (set at /start-sprint, 2026-10-29):
- A "pending-async-methods" detector names async methods with at least 1,000 reachable state machines that have not
  completed (boxes whose task is neither run to completion, faulted nor canceled), by method, with the objects as a set;
  the fixture's never-signaled awaits are found with their known count.
- A "finalizer-backlog" detector names the types of at least 1,000 objects waiting on the finalizer queue (ready for
  finalization, so the finalizer thread is blocked or slow), by type; the fixture's backlog behind a blocked finalizer is
  found with its known count.
- A "type-growth" detector, with a baseline, names the application's types whose reachable objects grew by at least
  1,000 and by at least half since the baseline (types matched by name and module, ADR-018), with the current objects as
  a set; each fixture leak's item type is found with the count it grew by.
- The objects no root reaches in a full dump open as a set (`ObjectSetKind.Unreachable`): from the Types view of a full
  dump and the CLI's `set --unreachable`, with their count and bytes matching the reachability walk.
- No new detector fires on the clean LeakyApi run, with or without a baseline.

**Decisions** (all decided autonomously, 2026-10-29, Phase 3 autonomy):
1. Async leaks: a reachable `AsyncStateMachineBox<T>` is a pending async method unless its task's state flags say it
   ended (read with field values, so full dumps only); rows are keyed by the method (`Namespace.Type.Method` from the
   state machine's `<Method>d__N` name), threshold 1,000. Plain `Task` objects never completed (TaskCompletionSource) are
   the cause the note names; a separate Task detector goes to the Backlog.
2. Finalizer backlog: the objects the finalizer queue roots (ready for finalization), by type, when at least 1,000 wait.
   The 1.6 finalizable inspection keeps the full census; this detector is the leak signal. "Objects finalized between two
   snapshots" is not built in this sprint: it needs the baseline's finalizable objects whose finalizer has not run (the
   header bit) matched by address against the later dump (ADR-032), which only full dumps of one run allow (Backlog).
3. Type growth: reachable objects per type key against the baseline's, framework types (System., Microsoft.) left out
   so a steady service stays quiet; at least 1,000 more objects and at least 50% more. Members are the current reachable
   objects of the type. Rule version 1.
4. Unreachable set: `ObjectSetKind.Unreachable = 9`, the objects the retention views' roots do not reach (ADR-024, the
   finalizer queue included, so members have no path; changed in the review, see H1); refused for gcdumps (their
   collection removed what no root reaches) and incomplete dumps. Protocol 19. ADR-034.
5. Fixtures: two detected leaks in `LeakCatalog.Detected` (PendingAsync: awaits on TaskCompletionSources kept in a static
   list; FinalizerBacklog: a finalizer that blocks the finalizer thread once, then finalizable items queued behind it),
   with known answers; type-growth detections added to the seven earlier leaks' known answers and PendingAsync's.
6. Logging: each detector's basis line (objects scanned, unreadable, thresholds, baseline used); the unreachable set's
   resolution logs its count and bytes.

**Dependencies**: 3.1 (findings open as sets), 2.6.

**Task breakdown**:
1. [x] Analysis: the three detectors in `LeakDetectors`, registered in the catalog. Tests: synthetic graphs (with a fake
   reader for task flags), thresholds, the clean case.
2. [x] Sets: `ObjectSetKind.Unreachable` through Contracts (shape, cache key, factory), the worker (refused for gcdumps),
   the CLI (`set --unreachable`) and the Types view (Open unreachable objects). Protocol 19. Tests: worker, CLI, view model.
3. [x] Fixtures and known answers: PendingAsync and FinalizerBacklog leaks, known answer files, type-growth detections.
   Tests: the fixtures hold their items (raw ClrMD), every detection found, clean run quiet.
4. [x] Docs: ARCHITECTURE (ADR-034, set kinds, detectors, protocol), DEVELOPER, README, KnownAnswers README, this section.

**Review summary** (docs/reviews/sprint-3.5.md): 3 High, 15 Moderate, 21 Low after dedupe across four reviewers
(solution-architect, senior-dev, gc-specialist, support engineer). All Highs fixed and confirmed by the reviewers that
raised them: the unreachable set contradicted the retention views (now the complement of the retention views' roots,
the finalizer queue included, so members have no path; incomplete dumps refused; its log says what only the finalizer
queue holds); type growth compared gcdumps with full dumps (now two full dumps only, after a re-review caught that a
complete gcdump still passed); pending async methods counted the framework's own waiting work (now left out and
logged). Moderates: 12 fixed (incomplete dumps, exact tests, LeakyConsole's hang, exact counts, cancellation and the
census, the set's log, every basis row, a later baseline, the wording of type growth and the finalizer backlog, box
types), 2 in part (memory of the detectors, after a re-review fix of the first attempt), 1 deferred (old-generation
growth). Measured: the detector integration tests find PendingAsync and FinalizerBacklog with exactly 6,000 each on a
LeakyApi run, type growth names every leak's item type with its growth, the clean run fires nothing with or without a
baseline, and the unreachable set matches ClrMD's walk exactly in count and bytes.

---

## Peer review sprints (closed)

The peer review sprints after 3.5.0 ([docs/reviews/peer-review-3.5.md](../reviews/peer-review-3.5.md)); the plan is in
[SPRINTS.md](../SPRINTS.md).

---

## Sprint 3.6: Urgent fixes

**Goal**: no lost session records, no untrusted native code, licenses travel with every build, and a CI that can be
trusted and required.

**Scope** (peer review P1 to P4, P11's flaky tests, S1, S2):
- Session documents (P1): one type owns `session.json`'s file protocol: reads that never block a save, a bounded retry
  of the replace, no silent new session when a document cannot be read, current-folder state off the update lock, and a
  two-process stress test.
- DAC trust (P2): a DAC beside a dump is never loaded without an explicit trust step (the app asks, naming the file and
  its SHA-256; the CLI takes a flag); installed runtimes, `Symbols:DacFolder` and HeapScope's own runtime stay trusted.
  ARCHITECTURE 12 updated.
- Licenses in builds (P3): the publish copies LICENSE, ADDITIONAL-TERMS.md, COMMERCIAL.md, THIRD-PARTY-NOTICES.md and
  every package's bundled notices; the unused TraceEvent natives stay out; CI checks a package holds them;
  THIRD-PARTY-NOTICES names the native components SkiaSharp and HarfBuzzSharp bundle.
- CI (P4, S1, S2): the integration tier runs in CI on Windows; test results are kept as artifacts; tests chosen by trait,
  not a project list; shared build scripts for CI and the gate. Making checks required is a repository setting the
  product owner applies.
- Flaky tests (P11): the worker cancellation tests in a non-parallel collection, the CLI monitor test made
  deterministic, no delay-based "nothing happened" assertions.

**Acceptance criteria** (set at /start-sprint, 2026-10-29):
- Readers of `session.json` never make a save fail: 200 saves while three threads read and list in a loop all land, and
  a save waits for a reader that does not share delete. An unreadable current session (damaged, another format version)
  fails with a next step and creates no new session; a missing one is replaced. `CurrentFolder` answers while an update
  waits for `session.lock`.
- A DAC found only next to a dump is refused with its path and SHA-256 until trusted; once trusted it is loaded from a
  private copy checked against the hash (an integration test on a real dump with a planted DAC). The app asks and
  remembers a yes; the CLI's `--trust-dac` trusts for one run and rejects anything but a SHA-256 (exit 64); a DAC
  HeapScope's capture copied loads without asking.
- Every published build holds `LICENSE`, `ADDITIONAL-TERMS.md`, `COMMERCIAL.md`, `THIRD-PARTY-NOTICES.md`, the .NET
  runtime's and SkiaSharp's notices under `licenses/`, and no `msdia140.dll` or `KernelTraceControl.dll`; the package job
  checks it.
- CI runs the integration tier on Windows and keeps every job's TRX results; the test projects are found, not listed;
  CI and the local gate share the test and fixture scripts.
- The flaky tests named by the review pass ten runs in a row of their classes.

**Decisions** (all decided autonomously, 2026-10-29, peer review sprint autonomy):
1. Session reads share read, write and delete and retry for 1 s; the replace retries for 5 s. The update stays
   synchronous (its callers already run it off the UI thread); only the gates are split. A document of another format
   version counts as unreadable: this build never rewrites it.
2. DAC trust is by SHA-256, not by path. Trusted folders (installed runtimes, `Symbols:DacFolder`, HeapScope's own
   runtime) come first; the dump's folders last. A trusted DAC is loaded from a private copy under its hash. Trust lives
   in Application (`trusted-dacs.json`; beside the session library at first, moved to the user's own data folder by the review, M2); every open request carries it
   (protocol 20). HeapScope's capture trusts the DAC it copies from the target's own runtime, so its dumps open as before.
   ADR-035 (amends ADR-017). The live session design the review asked for in 3.8 becomes ADR-036.
3. The TraceEvent natives stay in development builds (product owner, 2026-10-24) and leave published builds; their
   review moves with 3.7's supply chain work. SkiaSharp's bundled components are listed in THIRD-PARTY-NOTICES with the
   Adobe DNG SDK attribution; the product owner is told about the DNG SDK License, the one license on the list that is
   not an open source license.
4. Package license files come from each package's own files (52 of 84 packages carry some); the others declare their
   license in their metadata only and are covered by THIRD-PARTY-NOTICES.md. A build tool in C# (`dotnet run` on a file)
   reads the restore's assets, so no other language is needed.
5. CI runs the integration tier on every PR on Windows (measured: 294 tests in 163 s locally), not nightly. `gate.ps1`
   is replaced by `gate.sh`, so one gate runs on every system and shares CI's scripts. Required checks are a repository
   setting: the product owner applies it (the command is in the sprint's report).
6. Flaky tests: `WorkerIpcTests` joins the non-parallel heavy collection; LeakyApi gets `POST /gc` and the monitor tests
   force a collection after the leak grows (growth rules and the heap counter follow collections, and a server GC may not
   run on its own within the wait). The delay in `MonitorServiceTests` asserts that a run is still waiting on a held
   gate, which no timing can make pass wrongly; it stays.
7. From the review (decided autonomously): a full dump opened from its index cache is checked for a DAC that needs trust
   before the cache is read (H1); `OpenSnapshotRequest.CheckDac` lets the app open the cache anyway after a "no" and the
   CLI skip the check (re-review); the trust file lives in the user's own data folder, and an elevated capture trusts
   nothing automatically.

**Dependencies**: none.

**Task breakdown**:
1. [x] Session documents (P1): `SessionDocumentFile` (read, create, save with retries), `SessionReadStatus`,
   `SessionUnreadableException`; `SessionLibrary` uses it, `Current` refuses an unreadable session, separate gates;
   capture's failure text. Tests: `SessionLibraryTests` (readers never make a save fail, a save waits for a reader
   without delete sharing, an unreadable current session fails, the current folder answers during an update), five
   repeated runs.
2. [x] DAC trust (P2): `BesideDumpDirectories`, `UntrustedDacException`, `IsTrustedDac` and the private copy in the
   loader; `DacNotTrusted` with `DacTrustDetails` (protocol 20); the worker's trusted set; `TrustedDacs` in
   Application, capture trusting its copy, the open requests; the app's question; the CLI's `--trust-dac`. Tests:
   `DacLocatorTests`, `LoaderKnownAnswerTests` (a planted DAC refused, then loaded from the copy), `TrustedDacsTests`,
   `SnapshotsViewModelTests` (asked, refused, trusted and remembered), `CaptureTests` (a captured dump's DAC trusted).
3. [x] Licenses in builds (P3): `collect-licenses.cs`, `check-package.sh`, `HeapScopePublish` leaving out the TraceEvent
   natives, THIRD-PARTY-NOTICES. Tests: a win-x64 publish passes the check; the published CLI runs, pings its worker and
   refuses a bad `--trust-dac` with 64.
4. [x] CI (P4, S1, S2): `test.sh`, `fixture-dump.sh`, `gate.sh` (replacing `gate.ps1`), the workflow with the integration
   tier and TRX artifacts. Tests: the fast tier through the script; CI on the PR.
5. [x] Flaky tests (P11): heavy collection, `POST /gc`, the monitor tests. Tests: the classes ten times in a row.
6. [x] Docs: ARCHITECTURE (session protocol, ADR-035, ADR-012 amendment, section 12, code map, protocol 20), DEVELOPER,
   THIRD-PARTY-NOTICES, this section.

**Review summary** (docs/reviews/sprint-3.6.md): 4 High, 21 Moderate, 35 Low after dedupe across four reviewers
(solution-architect, senior-dev, gc-specialist, support engineer). All Highs fixed and confirmed by the reviewers that
raised them: a snapshot opened from its index cache was never asked about its DAC (now checked at open, before the cache
is read); the log could not say who trusted a loaded DAC (every open now logs each trusted hash with its file, reason and
date, and the app logs its question and answer); the refusal dropped the search (it lists the trusted folders searched);
the gate skipped CI's fixture check (it runs it, after a re-review caught that Git Bash's process number broke the
fixture's parent watch on Windows). Moderates: 19 fixed (among them the trust file's protocol and location, the capture's
trust rule, the `Open` and `DeleteIfEmpty` races, per-reason next steps, session logging, a lasting `session.lock`, CI
logs and timeouts, a graph-only open after a "no", and the check before the cache read), 2 in part or deferred (a
worker-level planted-DAC test, the Linux and macOS run, both 3.7 or Backlog). CI found five more issues on its first full
runs (backslash paths and capture tests off Windows, an elevated runner, a test race, a truncated counter stream), all
fixed. Measured: the integration tier 294 tests in 163 s; the formerly flaky classes ten runs in a row; the gate 1,335
tests with 0 warnings; a win-x64 publish with the license files of 52 of 84 packages and no TraceEvent natives.

---

## Sprint 3.7: Hardening

**Goal**: crafted files cannot hang, exhaust or fake the analysis, files stay private, and the supply chain and releases
are verifiable.

**Scope** (peer review P13 to P17, S3, S4):
- Crafted input (P13): the address index loop bounded and addresses validated; the minidump header check deduplicated
  and cancellable; gcdump counts checked against the bytes left and an edge budget per load (vendored code change 5); a
  fuzz harness for the gcdump, index cache and minidump header readers, run nightly.
- Index caches (P14): an HMAC over the header with a per-user key, also on the sidecars; rebuilt on mismatch; structural
  checks on open.
- Private files (P15): a per-user etlx folder; existing folders and files repaired; partial and export files created new
  under random names; owner-only ACLs on dumps written with `--out` on Windows.
- Supply chain (P16): `nuget.config` with source mapping, lock files in locked mode, Dependabot for NuGet and Actions,
  actions pinned by SHA, SECURITY.md, the patch package updates. Repository settings are the product owner's call.
- Releases (P17): a tag-driven `release.yml` with checksums, an SBOM and a build attestation.
- Session paths (S3) and the partial-file sweep (S4).
- From the 3.6 review (M19): the integration tier on Linux and macOS (a capture by `dotnet-dump`, or tests that bring
  their own dumps), so the trust of a DAC beside a dump and the private copy run where no signature check backs them.

**Acceptance criteria** (set at /start-sprint, 2026-10-30):
- Two objects at 0x1000 and 0x8000000000001000, and a span of the whole address range, build an address index in
  bounded time; a minidump directory repeating one memory list of 100,000 ranges 100,000 times is checked in seconds.
- Mutated gcdumps and index caches (fixed seeds in every run, more nightly) load or are refused with a reason in bounded
  time, never with another exception; counts a gcdump's stream cannot hold are refused before allocating.
- A cache signed with one key opens with that key only; an unsigned cache is untrusted where a key is in effect; the
  worker signs and checks with the user's key.
- On Windows a file HeapScope creates has an access list granting the user alone, inheriting nothing; off Windows an
  existing data folder with a wider mode is made owner-only; trace conversion leaves the shared temporary folder off
  Windows; partial traces and exports are created new.
- A received session cannot make Open timeline hand a non-CSV or outside file to the system, and no network path is
  looked at; the sweep never touches a folder the user picked.
- Packages restore from nuget.org only, in locked mode in CI; Actions are pinned to commits; Dependabot and SECURITY.md
  exist; the patch updates are in.
- Publishing a release attaches per-RID tarballs with attestations, SBOMs and SHA256SUMS (the workflow; it runs at the
  next release).
- The DAC trust test runs on real Linux and macOS core dumps in CI.

**Decisions** (all decided autonomously, 2026-10-30, peer review sprint autonomy):
1. Fuzzing is mutation testing in the test projects (fixed seeds in every run; `fuzz.yml` nightly with 2,000 iterations
   on the run number), not SharpFuzz or libFuzzer: no new dependency, runs on every system, replays from a seed.
2. Index caches are signed with an HMAC in the header region's spare last 32 bytes (no format change); a missing or wrong
   signature is a new reason, `Untrusted`, and the cache is rebuilt. The key is per user in HeapScope's data folder and
   loaded by the worker only; tests use a scoped key. ADR-036 (the live session design of 3.8 becomes ADR-037).
3. Windows files HeapScope creates get an owner-only, protected access list; folders are not changed on Windows (the
   profile covers HeapScope's own, and a folder the user picked stays theirs). Dumps are made owner-only after they are
   complete, best effort (an elevated capture of another user's process keeps that user's permissions).
4. Lock files everywhere, locked only in CI and only without a runtime identifier: a publish for a runtime restores
   runtime packs the lock files do not list.
5. Actions stay on their current major versions, pinned by commit; Dependabot proposes the newer majors (checkout 7,
   setup-dotnet 6, upload-artifact 7) as PRs to review.
6. Releases: the workflow runs when the owner publishes a release (the manual step of DEVELOPER.md 7 stays), builds from
   the tag, and attaches tarballs, attestations, SBOMs and checksums. Signing and installers stay with 2.8.
7. The integration tier stays Windows only (capture and its fixtures are Windows only, ADR-025); the DAC trust is checked
   off Windows by a loader test on the core dump CI's Linux and macOS jobs already take (3.6 review M19).

**Dependencies**: 3.6 (the integration tier in CI runs the new crafted-input tests).

**Task breakdown**:
1. [x] Crafted input (P13): `AddressIndex` loop bounded; `MinidumpHeader.IsTruncated` reads each memory list once and
   is cancellable; the vendored reader's counts checked against the stream (`CHANGES.md` change 5); the gcdump loader's
   reference budget, child index check and format refusal; `fuzz.yml`. Tests: `AddressIndexTests` (extreme spans),
   `MinidumpTruncationTests` (a repeated list), `CraftedGcDumpTests` (it found a crash: a child index past the node table,
   then a blob past its buffer; both refused now; 480 mutations on six seeds pass), `SignedCacheTests` (mutations).
2. [x] Signed caches (P14): `IndexCacheKey`, the signature in the writer and the check in the reader, `Untrusted`, the
   worker's key. Tests: `SignedCacheTests`.
3. [x] Private files (P15): `PrivateFiles.Protect` and the owner-only access list on Windows, `CreatePrivateDirectory`,
   the etlx folder, `CreateNew` for partial traces and exports, dumps protected when complete. Tests: `PrivateFilesTests`.
4. [x] Supply chain (P16): `nuget.config`, lock files and locked mode, pinned Actions without persisted credentials,
   `dependabot.yml`, `SECURITY.md`, the patch updates (Microsoft.Extensions 10.0.14, System.IO.Hashing 10.0.14,
   System.CommandLine 2.0.14). Tests: a locked restore; CI.
5. [x] Releases (P17): `release.yml`, `ContinuousIntegrationBuild`. Tests: the workflow at the next release.
6. [x] Session paths and the sweep (S3, S4): `Session.FileInside`, `Session.IsLocal`, the Live page's checks, the sweep
   without links and without the user's folder. Tests: `SessionLibraryTests`.
7. [x] DAC trust off Windows (3.6 M19): `PlantedDacTests` on `HEAPSCOPE_FIXTURE_DUMP`, run by the Linux and macOS jobs.
8. [x] Docs: ARCHITECTURE (section 7, section 11, section 12, ADR-036, the repository ADR, code map), DEVELOPER,
   THIRD-PARTY-NOTICES, SECURITY.md, this section.

**Review summary** (docs/reviews/sprint-3.7.md): 8 High, 16 Moderate, 32 Low after dedupe across four reviewers
(solution-architect, senior-dev, gc-specialist, support engineer). All Highs fixed and confirmed by the reviewers that
raised them: a received session could still reach a network path (`\/server`, `\??\`, mapped drives; the Home page opened
its latest snapshot unchecked); a folder link let Open and Delete timeline reach an outside file (one rule with deleting
a session now); an untrusted base cache was rebuilt silently; the key's lifecycle was unlogged and a key failure stopped
every worker (a key of its own for that run now, logged); the gcdump loader turned HeapScope's own errors into "the file
is damaged" (decided by the throw site now, after two re-reviews; the mutation test then found a duplicate address, now a
format refusal); a dump that could not be made private said nothing; the RCW and root walks read children outside the
budget (one budget per load, a crafted shared child list refused at once); the address table count was unchecked.
Moderates: 9 fixed (the two Untrusted wordings, the trace recorder's partial file, the nightly's branch and replay, the
release workflow's privileges, attestations, dry run and SBOM flag, Windows files created owner-only in one step, the
planted DAC test, the capture's wider catch, section 11), 2 in part (count minimums, tests), 5 deferred (3.8: the
injected cache signer, run file refusal reasons; Backlog: the cache folder fallback, structural cache checks, locked
publishes, folder ownership). Measured: 3,200 gcdump mutations on 16 seeds after the fixes, none failing.
