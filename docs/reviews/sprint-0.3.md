# Review: Sprint 0.3 Worker process and IPC

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-0.2`). Phase 0
runs in autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided
autonomously".

Totals after dedupe: 2 High, 12 Moderate, 13 Low.

## High / Urgent

### H1 Result sets stored as materialized strings with a count-only limit
- Reviewers: gc-specialist (High), solution-architect (Moderate).
- Location: src/HeapScope.Worker/Rpc/ResultSetStore.cs, WorkerService.cs; ARCHITECTURE.md 4.6.
- What: every set was a `string?[][]` of all rows, bounded only by count (32). One instance list over a Medium
  snapshot would be about 7.5 GB; millions of long-lived strings would also lengthen every blocking GC.
- Why it matters: budgets missed by far more than 2x as soon as 1.3 and 1.5 produce large sets.
- Fix: result sets are typed sources (`IResultSource`, for example `TypeSummarySource` with parallel arrays)
  formatted per page only; the store evicts by count (32) and by an estimated byte budget (256 MB), always keeping
  the newest; a set can carry an owner (snapshot lease from 1.2) disposed on release or eviction. Unit tests in the
  new `HeapScope.Worker.Tests`; integration paging tests unchanged and passing.
- Status: **Fixed.** Confirmation: see below.

### H2 Exceptions outside the worker client contract could crash the UI
- Reviewers: senior-dev (High), solution-architect (Moderate, exception boundaries).
- Location: src/HeapScope.Application/Workers/WorkerHost.cs, src/HeapScope.UI.Wpf (view models, App), CLI.
- What: the handshake passed raw `ConnectionLostException`; restart after a protocol mismatch threw a
  `WorkerOperationException` the restart command did not catch; a disposed host threw `ObjectDisposedException`;
  other `RemoteRpcException` subtypes passed through; no last-chance handler existed, so any of these took the UI
  process down without a log entry.
- Fix: `WorkerHost` now guarantees its contract (start, crash, operation or cancellation exceptions only): the
  handshake is bounded and translated, all `RemoteRpcException` subtypes are translated, a stopped or disposed
  host refuses with `WorkerStartException`, a canceled start returns to NotStarted instead of Crashed. The restart
  command catches `WorkerOperationException`. The UI installs dispatcher, app domain and unobserved task
  handlers that log and show a plain message (smoke mode fails the run instead of showing a dialog). The CLI has
  a last-chance boundary. The worker maps domain exceptions centrally (`WorkerErrors.GuardAsync`). New
  integration test: a real handshake protocol mismatch fails the start, sets Crashed and leaves no worker.
- Status: **Fixed.** Confirmation: see below.

## Moderate

### M1 Worker restart is invisible to callers
- Reviewer: solution-architect. Decision: **fix now** (decided autonomously). `WorkerInfo.InstanceId` and
  `IWorkerClient.Generation` added; `SnapshotNotLoaded` error code added; ARCHITECTURE.md 4.3 marks snapshot
  reopen as planned for 1.2 and describes Application-owned handles; carried into the 1.2 scope. Test: restart
  increments the generation and changes the instance id.

### M2 Result sets lack drill-down keys and richer kinds; no re-query on eviction
- Reviewer: solution-architect (parts of its result store finding not covered by H1). Decision: **defer to 1.3**
  (decided autonomously), recorded in the 1.3 scope: address, signed delta and percent kinds, key column flag,
  Application result-set handle that re-runs on `ResultSetNotFound`.

### M3 Decision 10 did not check the acceptance criterion's subject
- Reviewer: solution-architect. Decision: **fix now** (decided autonomously). The watchdog tracks the longest
  single dispatcher operation; the smoke run fails when it exceeds 100 ms, and probe lag stays gated at 2x.
  Decision 7 text corrected. The intermittent probe lag and the UI cold-start cost are deferred to the backlog
  (decided autonomously) and flagged for product owner confirmation.

### M4 Below-normal priority claim does not cover Server GC threads
- Reviewer: gc-specialist. Decision: **fix now** for the wording (ARCHITECTURE.md 4.1, decision 11);
  **defer** the mitigation (job object CPU rate, EcoQoS or heap affinity) to the backlog pending a trace
  (decided autonomously).

### M5 Large transient garbage stays committed in an idle worker
- Reviewer: gc-specialist. Decision: **fix now** (decided autonomously). After the synthetic job (completed,
  failed or canceled) the worker runs one blocking, compacting, aggressive gen2 collection; rule recorded in
  ARCHITECTURE.md 9.

### M6 Synthetic job had no memory projection and could run concurrently
- Reviewer: gc-specialist. Decision: **fix now** (decided autonomously). Peak projection against 50% of the
  GC's available memory fails with `InsufficientMemory`; synthetic jobs are serialized. Integration test added.

### M7 Stop and dispose did not prevent a later start (race)
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). Stopped and disposed flags are set under
  the gate; `GetSessionAsync` and `RestartAsync` refuse while stopped; `StartAsync` re-enables. Integration test.

### M8 Stop was not bounded by the caller's token
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). The token is linked into the shutdown call
  and the exit wait; the worker is killed when it fires. `DisposeAsync` has its own bound.

### M9 Handshake had no timeout while holding the gate
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). Bounded by the connect timeout, with a
  `WorkerStartException` and the process killed.

### M10 CLI boundary let unexpected exceptions print stack traces
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). Last-chance catch logs and prints a plain
  message with the log location.

### M11 Protocol mismatch criterion tested only on the server side
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). Internal `ClientProtocolVersion` option;
  real handshake mismatch test (see H2). The client also checks the version the worker reports.

### M12 Exception boundaries in the worker were per method
- Reviewer: solution-architect. Decision: **fix now** (decided autonomously), merged into H2
  (`WorkerErrors.GuardAsync` with a central mapping; new codes `InsufficientMemory`, `SnapshotFormat`).

## Low / Trivial (recorded only)
- L1 (gc-specialist) Watchdog cannot tell starvation from work outside dispatcher operations; log the UI thread's
  CPU time per lag. (Folded into the backlog item for the intermittent lag.)
- L2 (gc-specialist) Newtonsoft formatter builds a JToken tree per page; measure in 1.3. (Carried to 1.3.)
- L3 (solution-architect) A canceled start showed the crash banner. (Fixed as part of H2.)
- L4 (solution-architect) Restart worker reachable only from the crash banner; no health check for a hung worker.
- L5 (solution-architect) File logger writes synchronously under a process-wide lock; consider a background
  channel.
- L6 (solution-architect) Docs said "error data" and a 5 s CLI stop. (Corrected in the docs update.)
- L7 (senior-dev) `OnMainWindowClosing` finally can throw from dispose; smoke-mode startup failure shows a
  dialog. (The dialog part is covered by the smoke-mode last-chance handler; the dispose part is open.)
- L8 (senior-dev) `OnSessionLost` raises Crashed without the gate; stale banner possible in a rare race.
- L9 (senior-dev) `FileLoggerProvider` registered as an instance is never disposed.
- L10 (senior-dev) `TrackedJob<T>.Result` is null while `JobAdded` handlers run; a throwing `JobChanged` handler
  can re-mark a completed job as failed.
- L11 (senior-dev) The 5M progress test counts the completion event; subscribe before start and count only
  running updates.
- L12 (senior-dev) CLI tests write into the real log folder; the clear-finished view model test polls a thread
  pool continuation.
- L13 (senior-dev) `Stopping` flag and `_session` reads without volatile. (Fixed in the `WorkerHost` rewrite for
  H2.)

## Confirmations
- H1 confirmed by gc-specialist: typed sources formatted per page, count and byte budget eviction with correct
  accounting, owners disposed outside the lock, tests cover each rule. M4 to M6 confirmed as matching. Notes for
  1.2, carried into its scope: a source that reads graph columns takes its own snapshot lease per page in
  `FormatRow`; producers must stay compact because the newest set is always kept; the synthetic budget should
  use the shared 6.5 budget (capped by currently available memory) once 1.2 implements it.
- H2 confirmed by senior-dev: bounded and translated handshake, connect and RPC failures translated, stopped or
  disposed host refuses with a start exception, canceled start not reported as a crash, restart command and
  last-chance handlers in UI and CLI, real handshake mismatch test. M7 to M11 confirmed as matching. Its two
  notes: the contract remark overstated delegate exceptions (wording corrected); a stop arriving during a start
  waits up to the connect timeout before its own bound applies (accepted, commented in code).
