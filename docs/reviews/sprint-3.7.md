# Review: Sprint 3.7 Hardening

Changes since `sprint-3.6` that belong to the sprint (crafted input, signed index caches, private files, session paths and
the sweep, supply chain, release builds, the DAC trust check off Windows; peer review P13 to P17, S3, S4 and 3.6 review
M19). Four perspectives, run in parallel with the briefs in `.claude/agents/`: solution-architect, senior-dev,
gc-specialist and support-engineer. Moderates follow the lead engineer's recommendation (peer review sprint autonomy,
2026-10-30), marked "decided autonomously". None of the reviewers had a shell; each read the files directly.

Totals after dedupe: 8 High, 16 Moderate (2 from the re-reviews), 32 Low (7 from the re-reviews). A finding raised by more
than one reviewer is merged and keeps the highest severity.

## High / Urgent

- **H1 A received session could still make HeapScope reach a network path** (solution-architect, senior-dev, support
  engineer). `Session.IsLocal` refused only `\\` and `//`, but Windows takes any two leading separators as a share
  (`\/server/share`), and `\??\UNC\` and mapped network drives passed; the Home page opened a session's latest snapshot
  with no check at all, and Show in folder looked at a card already marked "not on this machine". Looking at a share
  offers it the user's credentials. Fix: `IsLocal` refuses any two leading separators of either kind, `\??\`, and a drive
  letter whose drive type is Network, without touching the path; the Home page refuses with a status line and a log line,
  and Show in folder checks first. Test: `SessionLibraryTests.Any_form_of_a_network_or_device_path_is_not_local`. Status:
  Fixed; confirmation below.
- **H2 A folder link inside a received session let Open and Delete timeline reach an outside file**
  (solution-architect; senior-dev as Moderate). `FileInside` checked only the file for a link, not the folders between
  the session folder and the file, and compared case differently from the deletion rule. Fix: `FileInside` uses the
  deletion rule (`SessionLibrary.IsUnder` and `ThroughLink`, one implementation) before any I/O, then refuses a file that
  is itself a link. Test: `SessionLibraryTests.A_file_reached_through_a_linked_folder_is_not_a_file_of_the_session` (runs
  where symbolic links can be made: the Linux and macOS jobs). Status: Fixed; confirmation below.
- **H3 An untrusted base cache was rebuilt silently** (support engineer; senior-dev as Moderate). `TryOpenCache` asked
  `GetInMemoryBytes` first, which returned null for an unsigned cache, so neither the log line nor the snapshot's note
  said why the dump was read again: a shipped cache, and every pre-3.7 cache, left no trace. Fix:
  `IndexCacheReader.GetInMemoryBytes(path, out failure)`; the worker logs "Index cache {Path} not used: {Reason}
  ({Detail})" and adds the note for any reason but Missing. Test:
  `SignedCacheTests.An_unsigned_cache_and_one_signed_with_another_key_are_told_apart`. Status: Fixed; confirmation below.
- **H4 The key's lifecycle was unlogged and a failure to load it stopped every worker** (support engineer;
  solution-architect and senior-dev as Moderate). `UseForThisUser` ran before the log existed, and a folder of another
  user, a full disk or an unreadable key ended the worker with only an exit code. Fix: the key loads after the log and
  the crash handler exist; `UseForThisUser` returns where the key came from (Loaded, Created, ReplacedDamaged, or
  ThisRunOnly with the error: a random key for this run, never no key, so caches are still signed and checked and only
  rebuilt on the next run); the worker logs it, and a failure as a Warning with a next step. Test:
  `SignedCacheTests.The_key_file_is_created_once_reloaded_and_replaced_when_damaged`,
  `Processes_creating_the_key_at_once_end_with_one_key`. Status: Fixed; confirmation below.
- **H5 The gcdump loader turned HeapScope's own errors into "the file is damaged"** (support engineer; senior-dev as
  Moderate). The new catch converted every exception from the graph build but cancellation and out of memory, so a
  builder bug told the user to capture again. Fix: only a reader inconsistency is converted, decided by type and by where
  it was thrown (the first frame outside the runtime must be the vendored reader's); type indices, child indices and
  duplicate addresses are checked explicitly with their own messages; a refusal that wraps another exception logs it at
  Debug with its stack. The mutation test then found a duplicate address the builder refused, now a format refusal.
  Status: Fixed; confirmation below.
- **H6 A dump that could not be made private said nothing** (support engineer; senior-dev as Moderate). The best-effort
  `Protect` after a capture swallowed its failure. Fix: protection moved from `PartialSnapshotFile.Commit` to
  `CaptureService.MakePrivate`, which logs a Warning with the path and error and returns a warning with a next step in
  `CaptureResult.Warning` (shown by the app and the CLI). Status: Fixed; confirmation below.
- **H7 The RCW walk and the root walk read children outside the reference budget** (gc-specialist, senior-dev). Nodes
  sharing one child list made N times K iterations in `AddObjects` (not cancellable within a block) and a roots list and
  stack that grew until out of memory in `AddRoots`, before the budget in `AddReferences` could trip. Fix: one
  `ChildBudget` per load (a child per byte of the file) that every child read draws on, with the index check and
  cancellation every 64K children; the root walk does not push a pseudo node it has walked and checks cancellation per
  node. Test: `GcDumpTests.A_crafted_gcdump_whose_nodes_share_one_child_list_is_refused_quickly` (2,001 RCW records on one
  list of 1,000 children from a file of a few kilobytes, refused well within 10 s). Status: Fixed; confirmation below.
- **H8 The gcdump's address table count was unchecked** (gc-specialist). `MemoryGraph.FromStream` allocated whatever
  count the file stated, and a table shorter than the node table failed later with an index error. Fix: vendored change
  5 checks it against the stream (8 bytes each) and refuses a table that does not cover every node; both reader
  constructors set the limit; the debug assertions on file-derived child counts and indices are removed. Status: Fixed;
  confirmation below.

### Confirmations

- H1: Confirmed by solution-architect ("all three paths I flagged are now closed") and senior-dev.
- H2: Confirmed by solution-architect (one rule for open and delete, text checks before any file system call).
- H3: Confirmed by support engineer and senior-dev.
- H4: Confirmed by support engineer and senior-dev; solution-architect: "fails safe rather than open".
- H5: Confirmed by senior-dev; the support engineer did not confirm it twice. First, the catch still matched index and
  argument errors thrown by `HeapGraphBuilder`: fixed by deciding on the throw site, with type indices checked before
  every type read. Second, a builder error then reached the worker as an `ArgumentException` and was logged as an
  "invalid argument" refusal without a stack: fixed by wrapping any other error from the graph build as a HeapScope error
  (`InvalidOperationException`, logged at Error with its stack). Third re-review: Confirmed. Test:
  `GcDumpTests.Only_the_readers_own_errors_count_as_a_bad_file`; the reader side is proven by the mutation test, which
  accepts only a format refusal.
- H6: Confirmed by support engineer, with a new Moderate (M15) fixed after.
- H7: Confirmed by gc-specialist and senior-dev.
- H8: Confirmed by gc-specialist.

## Moderate (all decided autonomously)

- **M1 The Untrusted note sounded alarming after the upgrade** (support engineer, senior-dev; solution-architect Low):
  every pre-3.7 cache read "not written by HeapScope for this user". Fixed: an all-zero signature reads "unsigned (written
  by a HeapScope before 3.7, or not by HeapScope)", another reads "signed with another key (another user, another
  machine, a deleted key file, or a HeapScope run that could not use its key file)".
- **M2 Refusals of a session's paths gave no reason** (support engineer, twice). The open-session and Show in folder
  refusals are now logged. Deferred to 3.8: `FileInside` returning which check refused, with a status per cause, as part of
  moving the run file policy out of `LiveViewModel` into Application (P11 split, P18; solution-architect Low).
- **M3 `CreatePrivateDirectory` detects another owner only by a failing chmod** (support engineer, senior-dev): a 0700
  folder of root passes, and the message gives no next step. Deferred to Backlog: H4 removes the startup failure, and
  3.8's `HeapScopeFolders` is the place for one ownership check.
- **M4 A cache next to the dump that cannot be replaced never falls back to the user's cache folder**
  (solution-architect, support engineer; gc-specialist Low): a planted read-only `.hsidx` keeps a dump in memory on every
  open. Deferred to Backlog: availability, not integrity (the planted cache is never used); fallback on write failure and
  reading both places.
- **M5 The trace recorder's partial file had a fixed name and deleted a file it did not create** (support engineer;
  senior-dev Low). Fixed: the partial is created new under a random name before the session starts and only that file
  is deleted on failure.
- **M6 The nightly fuzz did not run the code in progress and could not replay a failure** (solution-architect,
  senior-dev, support engineer, gc-specialist). Fixed: a scheduled run checks out `develop`; with `HEAPSCOPE_FUZZ_KEEP`
  the gcdump test keeps the base gcdump, the iteration in progress (written before each load, so a crashed host still
  names it) and any failing input, uploaded with the results (LeakyConsole fixture data only); `HEAPSCOPE_FUZZ_BASE`
  replays from a kept base; a failure names its seed, iteration and exception type.
- **M7 The release workflow was weaker than documented** (solution-architect, senior-dev, support engineer). Fixed: the
  builds hold read access only and pass their files on as artifacts; one job that builds nothing holds the rights to
  attest and attach; the tarballs, the SBOMs and `SHA256SUMS` are all attested; one runtime failing no longer cancels the
  others; a manual run takes a tag and is a dry run by default; the SBOM flag is `--output-format Json` (CycloneDX 6
  documents no `--json`). Deferred to Backlog: SBOMs per runtime identifier from the published output.
- **M8 Counts are checked against the whole file with low minimums** (gc-specialist, senior-dev). Partly fixed: a type
  takes at least 12 bytes, the address table 8. Deferred to Backlog: checking against the bytes left and the real minimum
  of each COM record.
- **M9 Cache opens rest on the checksums alone** (gc-specialist): structural checks behind them (monotonic edge starts,
  index ranges, sorted addresses, sidecars) are not implemented. Deferred to Backlog: with the signature, only the user's
  own HeapScope can write a cache that passes.
- **M10 Tests prove less than the criteria claim** (gc-specialist, senior-dev, solution-architect, support engineer).
  Fixed: the key's lifecycle and race, the two Untrusted wordings and the base-cache failure, a crafted shared child
  list. Deferred to Backlog: a cache fuzz mode that recomputes checksums and signs; an untrusted sidecar test per kind; a
  worker-level signature test (needs 3.8's data folder option); deterministic gcdump count tests with an allocation bound;
  a builder exception passing through the loader; the capture warning reaching `CaptureResult`.
- **M11 The signature check fails open when no key is loaded** (solution-architect): an ambient static, on only where
  the worker calls `UseForThisUser`. Deferred to 3.8 Modularity: an injected cache signer with an explicit "none" for
  tools and tests, which also removes the `AsyncLocal` seam.
- **M12 Locked restores do not reach the shipped bits** (solution-architect): publishes for a runtime restore unlocked,
  only the app states its runtime identifiers, and the SDK feature band can move. Deferred to Backlog; ARCHITECTURE 12
  now states the gap.
- **M13 Windows files were owner-only only after they were created** (solution-architect, senior-dev): a handle opened
  in between kept reading. Fixed: `PrivateFiles.Open` creates the file with its owner-only list in one step
  (`FileSystemAclExtensions.Create`), then opens it as asked. Dumps written by the runtime are still protected when
  complete (decision 3), which ARCHITECTURE 12 now says.
- **M14 `PlantedDacTests` could overwrite and delete a developer's DAC, and its skip passed silently**
  (senior-dev; solution-architect and gc-specialist Low). Fixed: it skips when the dump's folder holds any of the names and
  never overwrites; with `HEAPSCOPE_REQUIRE_FIXTURE=1` (set by CI) a skip fails instead.
- **M15 `MakePrivate` let some Windows errors escape** (support engineer, re-review): `NotSupportedException` and
  `InvalidOperationException` from a volume without access lists failed a capture that had written its dump. Fixed.
- **M16 The docs did not describe the new log lines** (support engineer, re-review; solution-architect Low). Fixed:
  ARCHITECTURE 11 gains a 3.7 entry, ADR-036 the key's fallback, section 12 the narrowed catch.

## Low (listed, not raised)

- solution-architect: `maxSections` fits the signature by arithmetic, not by a reserved length; ADR-036 does not state
  that section integrity rests on XxHash3 (now stated); `LocalApplicationData/HeapScope` built in seven places (3.8);
  `LiveViewModel` holds file policy and touches files on the dispatcher thread (3.8); the core file truncation check is
  not cancellable; Dependabot NuGet PRs must update the lock files (check the first one); docs mismatches in section 7,
  12, the code maps and SECURITY.md (fixed in the sweep); `IsUnder` ignores case off Windows; no view model test of the
  open-session refusal.
- senior-dev: the extreme-span theory does not assert the bucket bound and a hang would keep a pool thread; a redundant
  stopwatch assertion; the sweep's dead parameter and silent catch, and DEVELOPER's "swept at the next start" (fixed in
  the sweep); `OpenOrCreate` on Windows is not protected; the owner-only list removes SYSTEM; `FileMode.Create`
  truncates before protecting; Unix network mounts count as local; the read-phase catch still turns a cancellation in
  the middle of the read into a format refusal.
- gc-specialist: `maxSections` should subtract the signature; a uint offset in `MinidumpHeader`; TraceEvent's string
  allocations; the budget could follow the blob length rather than the file's; the root walk can queue one pseudo node
  many times before walking it (bounded by the budget, but heavier per entry than a reference); no test drives the
  budget through the root walk alone.
- support engineer: `CheckedCount` should name its table; the sweep should log paths and bytes; the SECURITY.md reporting
  setting needs the owner; old `.etlx` files under `/tmp` stay; ReplacedDamaged should log as a Warning; monitoring
  captures drop `CaptureResult.Warning` from the run's card; a worker restart during a ThisRunOnly run refuses automatic
  reopens with a misleading reason (the log backs it up); a reader accessor inlined into the loader hides its frame from
  the throw-site check (fails safe, as an unexpected error with its stack; stated in the code); the throw-site test calls
  the check directly rather than through `Load` and the worker.

## Found by CI during the sprint

- The gcdump loader's architecture test caught `UI.ViewModels` using a Core type for private exports: routed through
  Application's `CsvExport.CreatePrivate`.
- NU1004 on CI's newer SDK (it adds the machine's runtime identifier for the app's ReadyToRun publish): the app states
  its runtime identifiers and its lock file was regenerated.
- After the review, the key creation test failed on macOS: two processes creating the key at once each kept their own,
  because a move that must not overwrite is not atomic there. The key is now created under a lock file
  (`index-cache.key.lock`); the test passes five runs in a row locally and on every system in CI.

## Markdown sweep

Every tracked Markdown file (52) was checked after the fixes for what 3.7 changed (the index cache key and its log
lines, the gcdump refusals, private files, session paths, the sweep, fuzzing, releases, lock files, vendored change 5).
Changed: `docs/ARCHITECTURE.md` (section 7's reasons, section 11's 3.7 entry, section 12's crafted input, private files,
session paths, lock file gap and releases, the trace reader's folder in the code map, the Storage code map, ADR-036's
fallback and limits, the repository ADR's amendments in order, the vendored code's five changes), `docs/DEVELOPER.md`
(fuzz replay, the DAC trust check, the sweep of a folder the user picked, release builds and the dry run), `SECURITY.md`
(what a release carries), `docs/SPRINTS.md` (task 8, the review summary, 3.8's scope, the Backlog),
`src/HeapScope.ThirdParty.GcDump/CHANGES.md` (change 5) and `RELEASE-NOTES.md` (3.7.0). The agent briefs and skills
needed no change. No em or en dashes in any changed file.

## Measurements

- gcdump mutations: 480 on six seeds at implementation; during and after the fixes 3,200 more on 16 seeds (200 each),
  none failing after the last fix, in about a second per 100.
- Crafted shared child list: 2,001 nodes, about 2 million stated children, refused in well under a second.
