# ADR-033 One capture per process at a time; runs recorded in their session (added in sprint 3.4)

- Context: the Processes page, the Live page and monitoring rules could each start a capture of one process at once,
  doubling the pause and the disk use (1.7 M4); a monitoring run's timeline was a loose CSV with no record of what it
  watched (1.7 M3); and sessions grew without a way to remove them from the app.
- Decision: `CaptureService` holds a gate per process id (one per process, in one HeapScope) for captures and forced
  collections; a second one fails at once with `CaptureFailure.Busy` naming what runs, and rules see a busy process as a
  capture running: a due rule is deferred and logged, and a rule that lost the race is re-armed. Forcing a collection is
  an EventPipe session with the runtime's GC heap collect keyword (`GcTrigger`), like a gcdump without the heap walk; the
  runtime runs it while enabling the session, so the timeout covers the session's start, and only an induced collection
  whose end was seen is marked. It asks first: the target pauses, finalizers do not run, and a no-GC region ends.
  Monitoring runs are recorded in `session.json` (`Runs`) when they start and when they end, with their files and
  snapshots, so a past run reopens read only from its timeline. Deletion is explicit only: a snapshot of a session (file,
  caches, sidecars, entry), a session, or the sessions not opened for N days, each confirmed with its files and logged
  with them; never a file outside the session folder, never a session something writes into (in this HeapScope; another
  HeapScope's capture is held off only by `session.lock` and its open files), never automatically. A run's allocation
  trace stops at 1 GB or when the disk runs low. A run started with `RecordRun` off (smoke runs) uses no session at all.
- Consequences: different processes still capture together. A forced collection may move objects (ADR-032's comparisons
  across it see moved ones as dead and new) and leaves finalizable objects for a later collection; the timeline and the
  run record mark it. A run that ends with the app recorded no
  end (its `EndedUtc` stays null). Cache file names are Core's, so Storage and Application agree on them.
