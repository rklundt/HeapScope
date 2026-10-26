# Review: Sprint 2.3.7 Port peer review

A second, wider review of the Avalonia port (`sprint-2.3.5..sprint-2.3.6`, 173 files) asked for by the product owner
(2026-10-26): look hard for refactoring items, and make sure the local logs can back up any result a user contests.
Three perspectives, run in parallel: solution-architect, senior-dev (developer), and a support and system analyst (a
general reviewer with a read-only brief). None repeats the 2.3.6 review. "Telemetry" means local logs only: HeapScope
sends nothing anywhere (ARCHITECTURE 12).

Moderates follow the lead engineer's recommendation (product owner: "Go with your reco for moderate recommendations"),
marked "decided autonomously".

Totals after dedupe: 4 High, 20 Moderate, 26 Low (some Lows merged into Moderates). A finding raised by more than one reviewer is merged and keeps the
highest severity.

The reviewers confirmed:
- no field values or string contents are logged anywhere; smoke lines log type names, counts and addresses only (all
  three);
- every log call uses message templates with named fields and passes exception objects; no logging in per-object loops;
  Analysis has no logger by design (senior-dev);
- the derived-index lines (aggregation, reverse references, root path tree, dominators, group retention) record the
  snapshot id, sidecar or computed, mode, size, reachable counts and times; rejected caches log their reason (all three);
- layering holds: the view models and the CLI use no Core, Capture, Analysis, Storage or Loaders types
  (solution-architect);
- the symbol-server opt-in is logged in both processes; capture logs path, size, duration and failure; memory refusals
  carry needed and available MB; the file log is owner-only off Windows and flushed per line (support).

## High / Urgent

- **H1 Two HeapScope processes overwrote each other's log lines** (solution-architect, support; senior-dev as Moderate).
  `FileLoggerProvider` opened the shared daily file once and sought to its end once; each process then wrote at its own
  position, so the UI, the CLI and smoke runs on one day overwrote each other's lines, and no line said which process
  wrote it. Data loss in the one artifact meant as evidence. Fix: see task 1.
- **H2 A snapshot's identity was never logged** (solution-architect, support; senior-dev as Moderate). Every later line
  keys on a random worker snapshot id, but no line tied that id to a file, its size and fingerprint, kind, capabilities
  and facts, runtime version, capture time and its source, loader and cache writer versions, cache path and identity,
  whether it opened from the cache, or the notes the user sees. Load timings and loader diagnostics (static roots,
  incomplete segments) were computed and dropped; incomplete segments were not kept in the cache metadata; the client
  never logged a reopen after a worker restart or a changed-file refusal. Fix: task 2.
- **H3 No result was ever logged** (support; solution-architect and senior-dev as Moderate). The worker logged how derived
  data was built, never what a query answered: no inspection outcome or thresholds, no diff sides or totals, no retained
  value or dominator, no paths found or truncation, no Compare pair choice on the client. Duplicate strings also dropped
  unreadable strings silently and did not say the scan includes unreachable strings, the likeliest reason a number is
  disputed. Fix: task 3.
- **H4 No trail of who ran what** (support; solution-architect and senior-dev in parts). The UI and the CLI never logged
  their version, system, runtime, architecture or options; the worker's banner lacked the protocol version, system,
  architecture and runtime; `ProductInfo.Version` dropped the commit; `JobTracker` (open, capture, inspections) logged no
  start or end; worker start failures and the errors the user saw (status lines, CLI `Error:`) were not logged; the CLI
  did not log its command or exit code. Fix: task 4.

## Moderate (decided autonomously)

- **M1 No correlation ids; the file sink dropped scopes** (solution-architect, senior-dev; support Low L3 merged).
  `BeginScope` returned null, forwarded worker lines carried no worker pid or generation, timestamps had no offset.
  Fix now: task 5.
- **M2 Coded errors and refusals were logged wrongly or not at all** (solution-architect, support; support L4 merged).
  Errors created as `LocalRpcException` were logged nowhere; expected refusals (capability, invalid argument, changed
  file) were Warnings with a stack, which the CLI printed to stderr; inspection availability reasons were never logged;
  ARCHITECTURE 4.3 claimed the worker logs every failure. Fix now: task 6.
- **M3 The DAC choice was not recorded** (all three). Only failures surfaced, and a rejected candidate looked like an
  absent one; the chosen file, the matching rule, a symbol-server download and the reader's later choice were invisible.
  Fix now: task 7.
- **M4 Live auto-captures and the CLI growth gate could not be explained** (all three). A fired rule logged only its
  label; no run start or end; not the metric, baseline, threshold, step or rearm; the CLI's `--fail-on-growth` decision
  went to stderr only. Fix now: task 8.
- **M5 Rules and thresholds were unversioned** (solution-architect). Inspection thresholds are constants with no
  version; semantics and loader versions were logged only on a rebuild. Fix now: task 3 (inspection version and
  parameters) and task 4 (the worker's versions line).
- **M6 No policy for what logs may hold, and filter text reached the log** (solution-architect; senior-dev Low merged).
  Logs will be handed over as evidence, so they need a written content policy; the instances filter put the user's
  text into exception messages that the worker logged. Fix now: task 9.
- **M7 Exactness and caveats live in each client** (solution-architect). Whether a retained value is a lower bound is a
  client convention and caveat texts are written twice. Defer: 2.4 (its exact set retention changes this; carry
  exactness on the wire there).
- **M8 Navigation is wired by hand, with tab numbers and settable commands** (solution-architect, senior-dev). 2.4 adds
  cross-links and a new subject. Defer: 2.4 task 1 (a navigation target and subject interface, tab enums, a typed trail).
- **M9 2.4's drill-down needs worker pieces that do not exist** (solution-architect): a children index or a measured
  scan, a request carrying expanded nodes, node id and depth columns, an updated memory budget. Defer: decided at 2.4
  start.
- **M10 The instances filter dropped objects silently** (senior-dev). Unreadable strings and regex timeouts were left
  out with no count. Fix now: task 10.
- **M11 SmokeRunner repeats itself, does not say which step failed, and its gate logic is untested** (senior-dev).
  CI and a test also match log text. Fix now: task 11.
- **M12 Repeated error and release patterns** (senior-dev; solution-architect L4 merged). Fifteen copies of the three
  worker exception types, three copies of release-and-log, four copies of the section loading skeleton in
  `ObjectViewModel`. Fix now: task 12.
- **M13 Column groups are hard-coded in GridBehaviors** (senior-dev). Defer: 2.4, with the grid adapter (2.3.6 M13).
- **M14 Repeated view markup** (senior-dev). Defer: 2.4, which would add the third copy (a toolbar control and a note
  text control built then).
- **M15 Test wiring duplicates the app's** (senior-dev). Fix now: task 12 (one `AddHeapScopeViewModels` for the app and
  the tests); typed fake rows deferred to 2.4 with its new columns.
- **M16 The log folder was undocumented and two messages pointed to the wrong one** (support). Fix now: task 13.
- **M17 Logs were never cleaned up or capped, and their level was fixed** (support; solution-architect L6, senior-dev and
  support Lows merged). Fix now: task 1.
- **M18 UI failures before the host exists and Avalonia's own warnings never reached the log** (support; senior-dev Low
  merged). Fix now: task 14.
- **M19 Worker crashes were hard to diagnose** (support). Raw exit codes, no worker last-chance line. Fix now: task 14.
- **M20 Jobs and query re-runs were invisible** (support L5 raised to Moderate with H4). Fix now: tasks 4 and 5.

## Low / Trivial (listed; fixed where noted)

From solution-architect:
- L1 `SnapshotSession` repeats its query boilerplate seven times; the `WorkerOperations` comment says every operation
  goes through it.
- L2 `ResultColumns` lives in UI.ViewModels; the CLI reads table rows by position.
- L3 `VirtualRowList`'s WPF-era leftovers: the no-op collection-changed event and comments (ties to 2.3.6 M13); files
  with several public types.
- L4 merged into M12.
- L5 Timestamps without an offset (fixed with M1).
- L6 merged into M17.

From senior-dev:
- L7 merged into M17 (file log level).
- L8 Avalonia warnings went to trace only (merged into M18).
- L9 Pre-formatted messages (`"{Message}"`, `"{Error}"`, joined lists) lose fields (fixed where tasks touch them).
- L10 No UI or CLI startup line (merged into H4).
- L11 CLI expected failures and the growth gate not logged; messages name the default log folder (merged into H4, M4,
  M16).
- L12 Filter text in exception messages (merged into M6).
- L13 The five-minute wait for running jobs at exit logs nothing when it expires.
- L14 Shutdown and last-chance handlers live in `App`, untested.
- L15 `VirtualRowList` comments still justify WPF behavior (see L3).
- L16 Literal column names and an `int[]` layout in the grid view models.
- L17 Seven `CreateList` factories repeat the same call.
- L18 Files with several public types from the WPF era; double blank lines.
- L19 `TrackedJob<T>.Result` is a task that reads like a blocking call.
- L20 `ShowFrameworkSteps` and `ShowFramework` name the same option.
- L21 The Snapshots-only history keys rule lives in code-behind.

From support:
- L22 Watchdog warnings are not rate limited and say "UI thread CPU 0 ms" off Windows.
- L23 to L25 merged (file level into M17, worker prefix and offsets into M1, coded errors into M2).
- L26 `ResultSetHandle` re-runs were invisible (raised to M20).

