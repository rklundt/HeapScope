# Review: Sprint 3.8 Modularity

Changes since `sprint-3.7` that belong to the sprint (peer review P5 to P12, P18 to P21, S5 to S9, and the 3.7 review
deferrals: the injected cache signer, one data folder helper, the run file policy). Four perspectives, run in parallel
with the briefs in `.claude/agents/`: solution-architect, senior-dev, gc-specialist and support-engineer. Moderates
follow the lead engineer's recommendation (peer review sprint autonomy), marked "decided autonomously". None of the
reviewers had a shell; each read the files directly, so none diffed against the tag.

Totals after dedupe: 1 High, 17 Moderate, 41 Low. A finding raised by more than one reviewer is merged and keeps the
highest severity.

## High / Urgent

- **H1 The monitoring run's trace size limit never fired** (solution-architect). The watch looked for
  `<trace>.partial`; since 3.7 the recorder writes `<trace>.<random>.partial` and renames it when it ends, so the watch
  read size 0 and only the free-disk check worked, while the tests' fake recorder used the old name and passed. Fix: one
  naming helper in Capture (`TraceRecorder.PartialName` and `PartialPattern`) used by the recorder, the watch and the
  tests; the fakes now write what the recorder writes, so the limit test fails against the old watch. Status: Fixed;
  confirmed by solution-architect ("the old watch would never see that file, so the limit test now pins the fix").

## Moderate (all decided autonomously)

- **M1 "Worker starting" reached the status bar only after the start finished** (senior-dev). Queued state changes were
  raised after the gate was released, so Starting arrived with Running. Fixed: each change is raised at once on the
  thread pool, in order, never under the gate; the call drains again before it returns.
- **M2 No throwing-listener tests for the snapshot and trace sessions** (senior-dev). Fixed:
  `SnapshotReopenTests.A_throwing_listener_fails_no_open_reopen_or_close` and the trace session's twin.
- **M3 The disposal test proved little** (senior-dev): it relied on the unobserved exception event after a forced
  collection. Fixed: `AllocationRecordingTests` disposes a recording whose watch waits on a running capture and asserts
  the order directly.
- **M4 The runner test did not tie the log line to the worker's trace id** (senior-dev). Fixed: the test logger records
  the current trace id and the test asserts it equals the one the worker saw.
- **M5 The data folder could split between the front end and the worker** (senior-dev, solution-architect, support
  engineer): `Worker:DataFolder` moved only the worker's folders, a relative value resolved against each process's own
  folder, and no log said which folder a process used. Fixed: the client always passes the worker an absolute
  `--data-folder`; every log header names the data folder and its source and the worker's start line names its folder;
  `Worker:DataFolder` is documented as moving the worker's folders only, for tests and tools.
- **M6 The UI's cell reader is silent where the CLI's throws** (senior-dev). Partly fixed: `ColumnMap` throws a
  `FormatException` naming the column and a next step instead of "Value cannot be null". Deferred to Backlog: the view
  models reading cells by name and refusing a missing required column.
- **M7 A blank `HEAPSCOPE_DATA_FOLDER` stopped every process at start** (support engineer). Fixed: a value of only spaces
  counts as unset.
- **M8 A later trace problem was dropped** (support engineer): an early stop recorded "what it recorded is kept" and a
  stop that then failed left no log line. Fixed: every problem is logged; a later one with an error joins the recorded
  text.
- **M9 A failing listener could fill the log** (support engineer): a warning with its stack on every raise, and every
  monitoring event named "monitoring". Fixed: the first failure per listener and event is logged with its stack, repeats
  at Debug with their count; the run's events carry their own names.
- **M10 CLI refusals written to stderr leave the log without a reason** (support engineer; from before 3.8). Deferred to
  Backlog: a `CliContext` helper that writes the refusal and logs which check refused it.
- **M11 The sweep missed recordings' partial files** (solution-architect). Fixed with H1's helper; the sweep test
  includes one and the integration test asserts no partial file remains.
- **M12 ADR-037's decision contradicted the recorder's file protocol and the trace reopen rule** (solution-architect,
  gc-specialist). Fixed: ADR-037 now states the live file contract (written under its final name, shared for reading and
  deleting, owner-only), the "recording ended" contract method (a protocol bump) and a live trace's own reopen identity.
- **M13 The spike measured a different session** (gc-specialist, solution-architect). Fixed: rerun with the allocations
  profile HeapScope records (GC, Loader and JIT at Verbose with rundown) at two rates and two write buffers; at a
  realistic rate the recorder's 64 KB buffer made a live view 2.7 s late (157 ms without it), so the decision now says a
  live recording writes each chunk at once. What stays unmeasured (a separate reader process, live stack folding) is
  stated as a gate for Phase 4.
- **M14 Stacks cannot be folded by method name while live** (gc-specialist): methods compiled before the session are
  named only by the rundown. Fixed in ADR-037: fold by instruction pointer, name lazily; a rundown-only session at the
  start is to be measured.
- **M15 The reachability walk was unreserved** (gc-specialist): its stack marks on push and can hold every object.
  Fixed: the walk reserves `Reachability.WalkBytesPerObject` (4.125 bytes per object).
- **M16 Three acceptance criteria said more than was delivered** (solution-architect). Fixed: restated to point at
  decisions 11 and 12, and S5 as "the matchers agree" with the merge in the Backlog.
- **M17 The `Internal.` prefix could hide an application's own types** (solution-architect). Fixed: retention paths fold
  an `Internal.` step only from a framework module (`FrameworkTypes.IsFrameworkStep`), and a module given as a path is
  matched by its file name; the detectors keep their rule from before 3.8.

## Found by CI during the sprint

- `AllocationTraceTests.A_recording_whose_process_exits_has_no_rundown_and_says_so` failed twice on Windows CI: every
  allocation it recorded came in one burst right before it killed the process, so whether a flush reached the file first
  was timing. The test now waits until the trace has grown before it kills the process.

## Low (listed, not raised)

- solution-architect: the front ends still derive "holds unreachable objects" from the kind (a descriptor fact would
  serve a new kind); `Snapshots` and `Rpc` depend on each other (`FeatureRules`, `ThrottledProgress`); the contract test
  finds contracts by a name suffix and compares names without trimming `Async`; the dispatch test rebuilds the worker's
  registration by hand; the worker's "command line only" note and the data folder variable (now always passed as an
  option); the `recorded` and `attach` callbacks are not guarded; the monitoring files carry unused usings; ARCHITECTURE's
  layout, ADR-023's index title, BACKLOG's gcdump line, snapshots.md and DEVELOPER's missing recipe for a worker area
  (fixed in the sweep).
- senior-dev: `SnapshotHandle` updates its descriptor and generation separately (a torn read can reopen twice); the
  state changes are raised under an ordering lock (documented); the re-entrant start test takes the fast path; the grid
  base copies the runner's catches; decision 12 named the Object view wrongly (fixed); new type summaries carry sprint
  ids; files on the public types list that 3.8 touched were not split; the static state test accepts read-only
  references to mutable objects; no test of the timeline writer's failure path; the shared test data folder is fixed and
  never cleaned; an unexplained `!` in `CaptureCommands`; unused usings in `HeapScopeFolders` (fixed) and using order;
  the run's event names (fixed with M9); linked cancellation sources per derived slot are never disposed; contract test
  gaps; the link test scans untracked files and drops anchors; the spike's timeout path reads its samples unlocked.
- gc-specialist: `OfTypeKeys` keeps its own matcher and its cache key keeps the module as given; `ObjectSetCache.Evicted`
  is raised under its lock; most detectors use the name-only framework rule; the `ConcurrentDictionary` field names are
  outside `RuntimeLayouts`; the frozen scan indexes `HeapKinds` per object and the type growth fill hashes per member;
  the large object heap scope is not gated by the generations feature; `WhyNoInterfaces` always says "gcdump";
  worker-memory.md did not list the new slots (fixed).
- support engineer: the diff gate's message echoed the filter text (fixed); the Unreachable objects button hides
  without a reason; "shown as" lines lack the error code, and page and inspections-page refusals are not logged; page
  reads are guarded without their request and close and release are unguarded; timeline warnings lack the run id and
  path, and a failed timeline reads as "not recorded"; a refused past-run trace is not shown and two refusals lack a next
  step; docs still gave fixed `%LOCALAPPDATA%` paths (fixed in logging.md and DEVELOPER); `JobAdded` is raised outside
  the job's activity.

## Measurements

- CLI answers on the LeakyConsole dump (34 commands, timings and set ids aside) equal the pre-sprint baseline after every
  merge: tasks 9, 12, 13, 14, 16 and 17.
- Full suite before the wrap: 1,475 tests passed, 5 skipped.
- Live sources spike (ADR-037): flat out 394,841 ticks in 15 s, none lost, 181 ms median delay; at about 70 ticks a
  second 2,666 ms median behind a 64 KB write buffer, 157 ms without it.
- trace-report on a headless smoke trace: the UI thread found by Avalonia's dispatcher, 504 of 1,999 samples busy.
