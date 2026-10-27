# Review: Sprint 2.6 Smart leak detectors

Changes since `sprint-2.5`. Four perspectives, run in parallel: solution-architect, senior-dev, gc-specialist, and a
support and system analyst (a general reviewer with a read-only brief, kept since 2.3.7 so the logs can back up any
result a user contests). Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go with your reco
for moderate recommendations"), marked "decided autonomously".

Totals after dedupe: 8 High, 24 Moderate, 33 Low (the re-review of the fixes added 7 Moderates, 6 fixed and 1
deferred, and 6 Lows). A finding raised by more than one reviewer is merged and keeps the
highest severity.

The reviewers confirmed:
- the worker's lease handling: the baseline lease is released once the run ends, and both leases on any failure
  (senior-dev);
- the fixtures: `Timer.json`'s root is right for .NET 6 to 10, an unreferenced `System.Threading.Timer` is finalized
  (decision 8), and the runtime field names the detectors read are right for .NET 6 to 10 (gc-specialist);
- keeping the detector leaks out of `LeakCatalog.All` protects the five leaks' exact known answers (solution-architect).

## High / Urgent

- **H1 Counts were reported as bytes** (all four). The event detector passed its subscriber count, and the timer
  detector its timer count, as `InspectionResult.Bytes`; the event row carried its publisher count in the Bytes field.
  The tile showed 6,000 subscribers as "5.9 KB", and the CLI, the JSON and the worker's log line carried the wrong
  number. Fix: the event detector's bytes are its subscribers' delegates and their targets (shallow), the timer
  detector's are the timers' own objects (`TimerQueueTimer` and, for `System.Timers.Timer`, the outer timer); both tables
  gain a Bytes column; the event row is its own record with a Publishers field. Tests: the event and timer unit tests
  check the bytes. Status: Fixed.
- **H2 The event's field name was cached per publisher type and delegate type** (all four). Two events of one delegate
  type (`Started` and `Stopped`) were counted under whichever name was read first, so two events below the threshold
  could add up to a false finding; a first read that missed was cached and hid the event. Fix: subscribers are grouped
  by publisher and each publisher's reference paths are read once, so each delegate is named by the field that holds it;
  a miss is counted as "field not found" in the basis, never cached. Test:
  `Two_events_of_one_delegate_type_on_a_publisher_are_counted_apart`. Status: Fixed.
- **H3 Any delegate in any field counted as an event subscriber** (solution-architect, senior-dev, gc-specialist;
  support's unreachable publishers merged here). `Lazy<T>._factory`, `Task.m_action`, cancellation callbacks, callback
  fields, a subscriber keeping its own handler, and classes merely named "...EventHandler" could all fire with a
  confident "unsubscribe" fix; publishers no root reaches were counted too. Fix: subscribers are real delegates (the
  loader's `TypeKind.Delegate`, any delegate type); the publisher and the multicast must be reachable; only field-like
  events count (the holding field is named like an event: PascalCase, no prefix); a delegate held by its own target, a
  compiler-generated holder, or a framework object's single delegate (not through a multicast) is left out; each
  exclusion is counted by reason in the basis. Tests: `Delegates_in_callback_fields_lazies_and_their_own_targets_are_not_events`;
  the clean workload gained these patterns (M8). Status: Fixed.
- **H4 The timer detector counted timers that do not leak, and named no owner for many** (gc-specialist; senior-dev's
  timer findings merged). One-shot timers (`Task.Delay`, `CancelAfter`) release themselves; a paused or fired timer is
  out of the queue; a static or lambda-cache callback was attributed to "(static method)" or a `<>c` type, and that row
  opened a type that does not exist; a `System.Timers.Timer` with two handlers counted twice. Fix: only periodic timers in
  the queue count (`_canceled` false, `_dueTime` and `_period` not infinite), the others are counted by state in the
  basis, unreadable ones apart; a callback with no real target (static, or a compiler-generated type) is attributed to
  the timer's `_state`; each timer counts once per owner type; a "(static method)" row opens nothing. Tests: the timer
  unit test covers canceled, paused and one-shot timers;
  `A_timer_on_a_static_callback_belongs_to_its_state_and_one_shot_timers_are_not_counted`. Status: Fixed.
- **H5 Detector rows opened the whole type** (solution-architect). Rows carried `InstanceScope.All`, so the Instances
  page offered Retained by set on the whole type for a row about a subset, against the 2.4 decision. Fix: a new scope,
  `InstanceScope.Reachable` (the type's objects a root reaches), which every detector row opens; it is not `All`, so the
  whole-type actions stay off. The worker scopes instances with the cached reachability (M1). Status: Fixed.
- **H6 Choosing a baseline during the first run left tiles waiting for good** (senior-dev). The change cancelled the
  first run's token, so the remaining tiles kept "Waiting" and the running one "Running...". Fix: a baseline chosen
  while the first run goes on is not a cancel; the run ends by rerunning the growth tiles whose last run used another
  baseline, until none was chosen meanwhile; later changes rerun with their own token, linked to the snapshot's; a
  cancelled tile never stays "Running...". Test:
  `A_baseline_chosen_while_the_first_run_goes_on_reruns_the_growth_detector_and_no_tile_is_left_waiting`. Status: Fixed.
- **H7 The disposed flag probe was O(types x objects)** (support; solution-architect, senior-dev, gc-specialist as
  Moderate). Finding each candidate type's first reachable object rescanned the object index, roughly 10^10 steps on a
  real application heap, with cancellation only between types. Fix: the counting pass records each type's first
  reachable object, the probe reads it directly; cancellation every 64K objects and 256 types. Status: Fixed.
- **H8 A baseline without field values was silently ignored but reported as used** (support; the other three as
  Moderate). A gcdump baseline gave the "no baseline" note while the DTO, the tile ("growth since X") and the log named
  it. Fix: the worker refuses a baseline the detector cannot read, with the reason and the next step ("Choose a full dump
  of the same process, captured earlier"), so the tile shows why; the detector throws if handed one; a baseline captured
  after the snapshot, or from another process, gets a warning note. Status: Fixed.

## Moderate (all decided autonomously)

- M1 Reachability was computed by each of five detectors, and before checking for candidates (solution-architect,
  senior-dev, gc-specialist, support). Fixed now: `Reachability.For` computes it once per graph and keeps it with the
  graph (a conditional weak table, 0.125 bytes per object, now the only bit set). Deferred: the measurement on a
  Medium-sized dump of the current runtime (the sample dumps need a runtime no longer installed; Backlog, from 2.6).
- M2 The finalizer queue counted as alive (gc-specialist). Its objects are already dead, waiting for finalizers. Fixed
  now: `Reachability` starts from retaining roots other than the finalizer queue, and frozen objects; retention paths
  still show the queue. ADR-020's amendment records it.
- M3 A delegate's `_target` was read raw (gc-specialist): a static or open delegate stores itself there, so
  "(static method)" never appeared and rows were keyed on delegate types; wrapper delegates were not unwrapped. Fixed
  now: `Fields.DelegateTarget` treats the delegate itself, or a set `_methodPtrAux`, as no target, and
  `DelegateTargets` unwraps a wrapper. Tested by the two-events test (static handlers).
- M4 Only bool disposed flags were read, and auto-property backing fields were missed (senior-dev, gc-specialist,
  support). Fixed now: `Fields.Flag` reads a bool or an integer (non-zero is set), and the flag names include
  `<IsDisposed>k__BackingField` and `<Disposed>k__BackingField`. Tested.
- M5 `ConcurrentQueue` (in decision 3) and other collections were not counted; a read ignored the token; a
  `ConcurrentDictionary`'s bytes left out its tables (senior-dev, gc-specialist, support). Fixed in part now: the token
  is passed, the dictionary's bucket array is counted, and decision 3 says what is counted. Deferred to the Backlog:
  `ConcurrentQueue`, `ConcurrentBag`, `ConcurrentStack`, `LinkedList`, sorted collections and `Collection<T>`, with one
  layout table shared with the oversized collections inspection.
- M6 HttpClients made by IHttpClientFactory fired with "use IHttpClientFactory"; one client made three rows
  (senior-dev, gc-specialist). Fixed now: only clients owning their handler (`_disposeHandler` true) count, one row; those
  sharing one and unreadable ones are in the basis. Tested; the clean workload holds 25 sharing clients.
- M7 The service scopes detector counted the root scope, a missing flag as open, and bytes below the threshold
  (gc-specialist, senior-dev, support). Fixed now: the root scope is left out, an unreadable flag is counted apart, the
  bytes are the row's, and a note says scopes of requests in flight and Blazor circuits are open by design. Deferred to
  the Backlog: leaving out scopes a request or circuit holds, and a row for the root scope's tracked disposables.
- M8 The clean workload was too simple to catch false positives (gc-specialist). Fixed now: its first run sets up, above
  each threshold, objects keeping their own handler, unevaluated lazies, pending `Task.Delay` and `CancelAfter` timers and
  HttpClients sharing a handler; `CacheLimit` is 500, below the growth threshold. No detector fires on it.
- M9 Acceptance checked only a minimum, and two detectors have no real-dump fixture (senior-dev, solution-architect).
  Fixed in part now: acceptance also checks a maximum (every item leaked since the start), so double counting fails.
  Deferred to the Backlog: leaky HttpClient and undisposed scope fixtures with known answers.
- M10 Edge cases had no unit tests (senior-dev). Fixed in part now: tests for two events of one delegate type, static
  handlers, callback fields, lazies, self-held handlers, a non-delegate "EventHandler" class, timer states and owners,
  integer and backing-field flags, factory clients and the root scope. Deferred: growth exactly at the thresholds and a
  field missing from the baseline (Backlog).
- M11 A rerun lost errors and leaked the replaced result set (senior-dev). Fixed now: `RerunAsync` handles worker and
  unexpected errors as the first run does, and releases a replaced run in a `finally`.
- M12 The baseline picker is built once and preselects by process name only; later snapshots are listed (senior-dev,
  solution-architect). Fixed in part now: the detector's note warns when the baseline is later or from another process
  (H8). Deferred to sprint 2.7 (UI polish): refreshing the choices as snapshots open and close, matching on process id
  and start time.
- M13 The basis logged only the top rows, and no near misses or exclusions (support). Fixed now: every reported row is
  logged; the growth and event detectors log the largest below the threshold; every exclusion is counted by reason.
- M14 The inspection log line had no elapsed time (support). Fixed now: "rows in N s".
- M15 Hot loops used sets and LINQ per object (support, gc-specialist). Fixed now: type masks are `bool[]`, `Holders`
  fills one reused list, every loop over objects checks cancellation every 64K. Won't fix: `ReachableOf` returns a list,
  bounded by the candidate objects.
- M16 Derived inputs are flags per input (`UsesBaseline`, `UsesReferrers`), which will not scale (solution-architect).
  Deferred to the Backlog: an `InspectionNeeds` description and a cached derived-data accessor, when a third input
  arrives.
- M17 The detectors duplicate `InspectionRows.ByType` and the collection layout table (solution-architect,
  gc-specialist). Deferred to the Backlog with M5's shared table.

Raised by the re-review of the fixes:
- M18 Event exclusions were counts without names (support). Fixed now: `Exclusions` logs each reason with its three
  most common keys (publisher type, or `Publisher.field`), so "why was my event not reported?" is answered by name.
- M19 The growth detector's absences were not logged (support). Fixed now: framework statics left out without a
  baseline, collection types not counted (such as `ConcurrentQueue`) and field names seen twice are named in the basis;
  `Parameters` lists the collection types counted.
- M20 Native-int fields read as nothing on real dumps (gc-specialist): the reader returns `IntPtr` as a pointer, so
  `_methodPtrAux` and `_invocationCount` were never read. Fixed now: `Fields.Integer` accepts pointers; a test reads a
  static handler marked through `_methodPtrAux` as a pointer.
- M21 `new HttpClient(sharedHandler)` owns a handler it shares, so its clients counted as separate pools
  (gc-specialist). Fixed now: the detector counts distinct handlers among the owning clients and fires on 20 of them; the
  row and the basis give clients and handlers. Test `Http_clients_owning_one_shared_handler_are_one_pool_and_not_counted`.
- M22 Naming events read every publisher in full (gc-specialist). Fixed now: only publishers of types whose subscribers
  can reach the threshold are read; the others are logged as "publisher type below the threshold".
- M23 Hand-written events in a private field (`_changed`) were dropped as "field not an event" (senior-dev). Fixed now:
  a multicast delegate in any field of the publisher's own counts as an event; the name rule applies to single
  delegates only. Test `A_hand_written_event_in_a_private_field_counts_through_its_invocation_list`.
- M24 A detector row opens the type's reachable objects, not the objects it counted (solution-architect). Deferred to
  the Backlog with the object set queries: an exact object-set result for detector rows.

## Confirmations

The fixes went back to the reviewers who raised them (2026-10-26), who re-read the code and tests:
- solution-architect: H1, H2, H3 and H5 confirmed (H5 for the regression; the exact object set is M24); H8 confirmed;
  M1, M9, M12, M16 and M17 decisions sound. New Lows L28 and L29.
- senior-dev: H1, H2, H3 and H6 confirmed; its Moderates confirmed (M5, M9, M10 and M12 in part, as recorded). Raised
  M23 (fixed) and Lows L30 and L31.
- gc-specialist: H2, H3 and H4 confirmed (H3 with the Low L32); H1, H7, M1, M2, M4, M7 and M8 confirmed; M3, M5 and M6
  in part, completed by M20 and M21. Raised M20 to M22 (fixed).
- support: H1, H2, H7 and H8 confirmed; M1, M4 and M14 confirmed, M13 in part, completed by M18 and M19 (fixed); no
  values reach the log. Lows L33 (and the baseline's file name and capture time, now in the log line).
- H1 to H8 are Fixed and confirmed.

## Low / Trivial (listed only)

- L1 Thresholds are constants; no test ties a change to a version bump; the note hardcoded 10,000 (now `MinLarge`)
  (solution-architect).
- L2 A generic declaring type's static field may collide across instantiations if the root detail leaves out type
  arguments (solution-architect).
- L3 Timer rows counted handlers, stopped at 64 targets silently, kept the first kind seen (solution-architect, support);
  the count is per timer now (H4).
- L4 A shared invocation-list array can hold slots beyond a multicast's count (solution-architect); mitigated, since an
  older multicast sharing the array is usually unreachable and now left out.
- L5 Fixture comments: "enabled with 'all'" (fixed), `ILeak`'s "rooted by a static field of its own class",
  `All.Concat(Detected)` repeated (solution-architect, senior-dev).
- L6 `InspectionInfo` does not carry the rule version or parameters for CLI output (solution-architect).
- L7 The CLI summary JSON and CSV omit Kind, elapsed time and the baseline (solution-architect, senior-dev, support).
- L8 The scopes detector rebuilt its rows to set the module (solution-architect); fixed with M7.
- L9 `context.Reader!` without the required comment (senior-dev); fixed: `Readers.Of` throws a clear message.
- L10 Loops without cancellation checks; `ToArray` per subscriber (senior-dev); fixed with M15.
- L11 Framework statics are left out only without a baseline (senior-dev, gc-specialist).
- L12 `RunInspectionAsync` takes the token before the baseline (senior-dev).
- L13 The tab's group heading is selectable with its tile (senior-dev); sprint 2.7.
- L14 `CacheLimit` equalled the growth threshold (senior-dev); fixed (500).
- L15 LeakyConsole's usage in DEVELOPER.md lacked `--detected` (senior-dev); fixed.
- L16 A reference array's count is its length, not its non-null slots (gc-specialist).
- L17 Every `ReadField` splits its path and takes a lock (gc-specialist).
- L18 The CLI log hides the inspection name and format as lengths (support).
- L19 The CLI opens `--baseline` even for an inspection that does not use it (support).
- L20 Next steps in the same-snapshot message, the no-baseline note and the gcdump reason are UI-only (support); the
  note now names `--baseline`.
- L21 The Inspections tab does not log the automatic baseline choice's reason or row actions (support).
- L22 Truncations (4,096 locks, 64 delegate targets) are not logged (support).
- L23 The scopes detector returned bytes when nothing fired (support); fixed with M7.
- L24 The timer detector's `Kind` per owner is the first one seen (solution-architect).
- L25 `ConcurrentDictionary` count read with `CancellationToken.None` (gc-specialist, support); fixed with M5.
- L26 No known answer covers a static or lambda handler on a real dump (gc-specialist); unit tests cover them.
- L27 Arrays of values were never collections (already so); the timers' captured execution contexts are not noted
  (gc-specialist).
- L28 "Reachable" means the finalizer queue is left out for detectors and the Instances scope, but not on the paths
  page or in retained sizes (solution-architect); the detectors' basis now defines it.
- L29 The cached reachability is outside the worker's memory reservation; two first callers may both walk
  (solution-architect).
- L30 A replaced rerun's cancellation can reset a tile's status while the new rerun runs (senior-dev); cosmetic.
- L31 A `System.Timers.Timer` with handlers on two owner types adds its bytes to both rows (senior-dev).
- L32 An application type's public PascalCase delegate field still counts as an event (gc-specialist,
  solution-architect); many framework publishers with one subscriber each are left out (revisit for WPF).
- L33 Probed types without a flag, unreachable clients and scopes, and the tab's automatic baseline choice and row
  actions were not logged (support); the first three are in the basis now, the tab's logging is left.
