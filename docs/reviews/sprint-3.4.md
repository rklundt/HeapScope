# Review: Sprint 3.4 Live workflow and housekeeping

Changes since `sprint-3.3`. Four perspectives, run in parallel: solution-architect, senior-dev, gc-specialist, and a
support engineer (a general reviewer with a read-only brief, so the logs can back up any result a user contests).
Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go with your reco for moderate
recommendations"), marked "decided autonomously". None of the reviewers had a shell; each read the files directly.

Totals after dedupe: 7 High, 18 Moderate (3 from the re-review), 27 Low (8 from the re-review). A finding raised by more than one reviewer is merged and keeps the
highest severity.

## High / Urgent

- **H1 A session could be deleted while a capture or a monitoring run wrote into it** (solution-architect; senior-dev and
  support engineer as Moderate). Only the current session and open snapshots were refused, in the view model; a run's
  folder is fixed at its start, so opening another session and deleting the run's own one deleted it under the run.
  Fix: `SessionLibrary.MarkInUse` registers the folders written into (a capture until it is recorded, a run until its end
  record), `InUseBy` says what; `DeleteSession` refuses them, marks the folder as being deleted (a capture or run then
  refuses to start there) and deletes under `session.lock`. Test: `SessionLibraryTests.A_session_something_writes_into_
  is_not_deleted_...`, `MonitorServiceTests.Stop_waits_for_every_capture_...` (in use during the run, free after).
  Status: Fixed; confirmation below.
- **H2 Deletion could reach files outside the library** (solution-architect, support engineer; senior-dev as Moderate
  and Low). A snapshot captured with `--out` is recorded by its absolute path and was deleted with the caches next to it;
  an entry with `..` climbed out; the root check had no separator (`sessions-old` passed). Fix: a snapshot is deleted only
  inside its session folder and reached through no link (a junction out); a session only directly under the root
  (separator-safe) and not itself a link; a session's deletion does not follow links and keeps the dumps it recorded
  outside its folder, with their caches, listing them as kept. Test: `SessionLibraryTests.Deletion_never_reaches_outside_
  the_session_folder`. Status: Fixed; confirmation below.
- **H3 Start ran its file work on the UI thread** (solution-architect). The run's start reads the current session and
  records the run under the session's lock, which can wait up to 30 seconds. Fix: `LiveViewModel.StartAsync` starts the
  run off the dispatcher, shows "Starting..." and reports a failure to start. Status: Fixed; confirmation below.
- **H4 Deletions could not be backed up from the log, and a partial failure lost the record** (support engineer; senior-dev
  as Moderate). The log held counts, not paths; the question, its answer, refusals and what cleanup kept were not logged;
  a file that could not be deleted threw after the others were gone. Fix: the library plans each deletion
  (`PlanSnapshotDeletion`, `PlanSessionDeletion`) and the confirmation lists the files by path and what is kept; the log
  has the question's size and file count, the answer, every refusal with its reason, the cleanup cutoff, what it proposed
  and kept and why, and every file deleted, left (with the reason) and kept (`DeletionText`). A failed file is reported in
  `Deletion.Failed`; a snapshot keeps its entry and a session its `session.json`, which is deleted last, so it is still
  listed and can be deleted again. Tests: `SessionLibraryTests` (failure), `HousekeepingTests` (log lines). Status: Fixed;
  confirmation below.
- **H5 A rule skipped while the process was busy logged nothing, and a once rule could be lost in the race** (support
  engineer; senior-dev as Moderate, gc-specialist as a nit). Fix: the rule engine reports the rule that was due but
  deferred (`Deferred`, `LastMetric`); the run logs it once per busy stretch with what holds the process (its own capture
  or one from elsewhere); a rule whose capture lost the race to another one (`CaptureFailure.Busy`) is re-armed
  (`Missed`: a once rule is not used up, a timed rule is due again) and that is logged. Test: `MonitorServiceTests.A_rule_
  that_lost_the_race_...`. Status: Fixed; confirmation below.
- **H6 Stop did not wait for every capture, and a late one opened a second timeline** (senior-dev; solution-architect as
  Moderate). Only the last capture task was awaited; a capture or forced collection that ended after teardown created a
  new, never closed CSV and was missing from the end record. Fix: every capture and forced collection is tracked and
  awaited; once the run is stopping, Get snapshot and Force GC are refused ("The run has stopped."); the timeline is
  created at the run's start and nothing writes or creates it after it is closed. Test: `MonitorServiceTests.Stop_waits_
  for_every_capture_...`. Status: Fixed; confirmation below.
- **H7 The allocation trace of a run had no size limit** (gc-specialist; solution-architect as Moderate). A busy target
  writes gigabytes an hour, past what the Allocations page reads within its budget, and fills the disk. Fix: the trace
  stops at `MonitorOptions.TraceLimitBytes` (1 GB by default) or when the disk has under 1 GB free; what it recorded is
  kept, the run goes on, the Live page says so at once and the run's record holds the reason (`TraceNote`). Test:
  `MonitorServiceTests.The_allocation_trace_stops_at_its_limit_...`. Status: Fixed; confirmation below.

### Confirmations

Each reviewer re-read the fixes of the findings it raised (read only):
- gc-specialist: H7 confirmed (the reservation of a 1 GB trace is about 3.1 GB, inside the 8 GB budget of a 16 GB
  laptop; the overshoot is at most 2 s of writes). One new Low, fixed at once: the limit's stop now waits for a capture of
  the process still running, like the run's end (L20).
- senior-dev: H6 confirmed. One new Moderate, fixed at once (M16): the Live smoke run wrote a session and a run record
  into the user's library, because every run now records itself.
- solution-architect: H1, H2 and H3 confirmed. The same Moderate (M16), a second one fixed at once (M17: the smoke run
  read the run before the now asynchronous Start had set it), and a Low fixed at once (L21: Stop pressed while the run
  starts now stops it once started); ADR-033 says another HeapScope's capture is held off only by the lock.
- support engineer: H2, H4 and H5 confirmed, and M1, M9 to M12. M14 was not confirmed at first (the record line had ids
  without files; captures from other pages logged no snapshot id) and is now fixed. One new Moderate fixed at once (M18:
  a snapshot whose deletion partly failed was already closed and its status said only "in use?"; the entry now stays
  only while its dump does, and the status names the files left and what to do), and Lows fixed at once (L22: the status
  when only a session's folder could not be removed; L23: a past run's timeline path is checked to be inside its session
  folder before Delete timeline can act on it; L24: the cleanup question's log has each session's size and last opened
  time; L25: Force GC's failure status repeated its prefix). Recorded only: the deferred rule's log says "from elsewhere"
  rather than naming the page or rule holding the process (L26); a diagnostics port that does not answer still asks about
  .NET 6 (L27).

## Moderate (all decided autonomously)

- **M1 A refused trace was only reported at Stop** (senior-dev, gc-specialist, support engineer). Fixed: the recorder's
  task is watched; a fault sets `TraceError`, is logged and raises `TraceStopped`, which the Live page shows at once; the
  log says "starting an allocation trace", not "recording".
- **M2 Teardown's catch around the trace was too narrow** (senior-dev). Fixed: any exception from the recorder is caught
  there, so the timeline is closed and the end recorded whatever it threw.
- **M3 Force GC's timeout did not bound the call, and an unconfirmed collection was marked** (gc-specialist, senior-dev).
  The runtime runs the collection while it enables the session, so a frozen target held the process gate forever. Fixed:
  the timeout covers the session's start; three outcomes (finished with its number, none seen, end not seen); only a
  collection whose end was seen is written as "gc forced", recorded and drawn; the induced collection is matched by its
  reason, not only its depth; the wait ends when the target exits; the buffer is 4 MB.
- **M4 Force GC ran without asking and overstated what it does** (gc-specialist). Fixed: it asks first, saying that the
  target pauses, finalizers do not run (finalizable objects survive it), a no-GC region ends, and objects may move;
  ADR-033 says "may move".
- **M5 Runtime provider sessions of one process can induce extra collections when their keywords change** (gc-specialist).
  Fixed in part: a run's trace stops after its captures have ended. Serializing the Allocations page's recording and a
  gcdump of the same process, with an integration test counting induced collections, is in the Backlog.
- **M6 No runtime version check before Force GC** (senior-dev, gc-specialist). Fixed: a target older than .NET 5 is
  refused with the reason, as decision 1 says.
- **M7 A snapshot's cache match was too broad** (solution-architect; senior-dev as Low). `app.dmp.bak`'s cache was deleted
  with `app.dmp`'s. Fixed: Core names the sidecar kinds (`IndexCachePaths.SidecarKinds`, used by the worker); only the
  cache, those sidecars and the writer's own temporary files (32 hex digits) are matched.
- **M8 A run record that does not parse hides the whole session** (solution-architect). Deferred (decided autonomously):
  only this build writes runs so far; reading runs one by one while keeping an unreadable one's JSON is in the Backlog,
  with the rule that new run members are optional.
- **M9 Refusals were not logged** (support engineer, senior-dev). Fixed with H4 for deletions; also the past run whose
  timeline is gone, a timeline that cannot be read, a Force GC refused for its runtime, and the timeline's deletion (now
  with its path).
- **M10 A run without a timeline was not recorded** (support engineer). Fixed: the session folder is fixed for every run;
  the timeline is optional.
- **M11 Force GC's failures talked about dumps** (support engineer). Fixed: its own translation ("The collection could not
  be forced: ...").
- **M12 No tests of the 3.4 log lines** (support engineer). Fixed: deletion questions, answers, paths and refusals; the
  deferred rule and the re-arm; the trace's stop.
- **M13 The real capture gate has no deterministic test** (senior-dev). Deferred (decided autonomously): it needs a seam
  in front of `DumpCapturer`; in the Backlog. The integration test checks the refusal when the first capture is still
  running.
- **M14 Run ids and snapshot ids were missing from log lines** (support engineer). Fixed: the run's lines carry its id;
  a capture logs its snapshot id; the start and end records list the snapshot ids and files.
- **M15 The end-of-run count left out Get snapshot** (support engineer). Fixed: snapshots taken and those taken by rules.
- **M16 The Live smoke run wrote into the user's session library** (senior-dev, solution-architect; re-review). Fixed:
  `MonitorOptions.RecordRun` (the Live page's `RecordRuns`), off for smoke runs, uses no session at all. Test:
  `MonitorServiceTests.A_run_that_records_nothing_leaves_the_session_library_alone`.
- **M17 The Live smoke run read the run before Start set it** (solution-architect; re-review). Fixed: it awaits Start.
- **M18 A partly failed snapshot deletion left a closed snapshot that could not be retried** (support engineer;
  re-review). Fixed: the entry stays only while the dump does; the status lists the files left and what to do.

## Low (listed, not raised)

1. The timeline is read whole and a reopened run shows only its last 30 minutes, dropping earlier markers silently
   (gc-specialist, solution-architect). Backlog: downsample the whole run.
2. ARCHITECTURE's code map lacked `IndexCachePaths` (fixed).
3. ADR-033 should say the gate is per process (fixed).
4. `DeleteSnapshot`'s comment mentioned DAC leftovers it does not delete (fixed with the rewrite).
5. A stray doc comment above `PastRunItem` (fixed).
6. `MonitorService` does not take the registered `TimeProvider` from DI.
7. The rule label "manual" and the source "Manual" differ in case.
8. The reopen log was thin (fixed: path, samples shown, unreadable lines).
9. `Current` re-reads or creates a session during a deletion or a listing (fixed: `CurrentFolder`).
10. `GcTrigger` waited the whole timeout after the target exited and disposed the session twice (fixed with M3).
11. "gc forced" and the snapshot event prefix were magic strings (fixed: constants on `MonitorRun`).
12. `DateTime.Now` in file names instead of the injected time (fixed).
13. The integration test's comment says the cache was "written by opening it once"; it writes a fake one.
14. The fakes' `Busy` sets are read on the feed thread and written by the test.
15. Cleanup's confirmation did not list each session's size (fixed).
16. The forced collection's buffer was 64 MB (fixed with M3).
17. The trace's cost to the target is not measured on a high-allocation target (Backlog with M5).
18. A forced collection before the first sample was not written to the timeline (fixed: the timeline opens at start).
19. A junction inside a session folder was followed when its size was counted (fixed with H2).
20. The trace's limit stopped it while a gcdump of the process could still be running (gc-specialist, re-review; fixed).
21. Stop pressed while the run was still starting did nothing (solution-architect, re-review; fixed).
22. A session whose files were deleted but whose folder stayed was reported as "still listed" (support, re-review; fixed).
23. A past run's timeline path from `session.json` was not checked to be inside its folder (support, re-review; fixed).
24. The cleanup question's log had only the count (support, re-review; fixed).
25. Force GC's failure status repeated its prefix (support, re-review; fixed).
26. The deferred rule's log says "from elsewhere", not which page or rule holds the process (support, re-review).
27. A diagnostics port that does not answer is still explained as "Is it a .NET 6 or later process?" (support, re-review).
