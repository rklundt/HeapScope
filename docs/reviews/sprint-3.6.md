# Review: Sprint 3.6 Urgent fixes

Changes since `sprint-3.5` that belong to the sprint (the session file protocol, DAC trust, licenses in builds, CI and the
flaky tests; peer review P1 to P4, P11, S1 and S2). Four perspectives, run in parallel with the briefs in
`.claude/agents/`: solution-architect, senior-dev, gc-specialist and support-engineer. Moderates follow the lead
engineer's recommendation (product owner, 2.3.7: "Go with your reco for moderate recommendations"), marked "decided
autonomously". None of the reviewers had a shell; each read the files directly.

Totals after dedupe: 4 High, 21 Moderate (2 from the re-review), 35 Low (12 from the re-review). A finding raised by more than one reviewer is merged and keeps the
highest severity.

## High / Urgent

- **H1 A snapshot opened from its index cache was never asked about its DAC** (gc-specialist; solution-architect,
  senior-dev and support engineer as Moderate). An open from a cache loads no DAC, so it never failed with
  `DacNotTrusted`; the field reader failed later on every field read, with a next step ("answer the question when opening
  the dump") that never came, and ADR-035's Rebuild does not exist in the app. It hit caches built before 3.6, caches
  built by a CLI run with `--trust-dac`, a damaged trust file, and dumps received with their `.hsidx`. Fix: the worker
  checks the DAC when it opens a full dump from its cache (`SnapshotKindHandler.CheckBeforeCacheOpen`,
  `ClrMdDumpLoader.CheckDacTrust`: the same lookup and hash as a load, nothing loaded) and refuses with `DacNotTrusted`, so
  the app asks at open as for a load; a missing DAC does not block a cache open. ADR-035 corrected. Test:
  `LoaderKnownAnswerTests.A_DAC_found_only_next_to_the_dump_is_loaded_only_once_trusted` (the check refuses, then passes
  once trusted). Status: Fixed; confirmation below.
- **H2 The log could not say who trusted a loaded DAC** (support engineer). The open logged only a count of trusted
  hashes; the grant was logged once, in whichever process granted it. Fix: every open and reopen logs, under its trace id,
  each trusted hash with the file, the reason (the user when opening which dump, HeapScope's capture, `--trust-dac` for
  this run) and since when (`TrustedDacs.Describe`); the worker's DAC choice line names the hash it loaded; the app logs
  the question and the answer. Test: `TrustedDacsTests.The_open_log_says_who_trusted_each_DAC_and_when`. Status: Fixed;
  confirmation below.
- **H3 The refusal threw away the search behind it** (support engineer). `UntrustedDacException` had the file and its
  hash, not the files the trusted folders held or why each was rejected, so "I put it in Symbols:DacFolder and it still
  asks" had no answer. Fix: the exception carries `Searched`, and its message (logged by the worker's refusal line and
  shown by the CLI) ends with the trusted folders searched. Test: the loader test asserts it. Status: Fixed; confirmation
  below.
- **H4 The local gate did not run CI's fixture check** (senior-dev). The acceptance criterion says CI and the gate share
  the test and fixture scripts, but `gate.sh` never ran `fixture-dump.sh` or `check-known-answers.sh`. Fix: the gate runs
  them after the tiers (the CLI captures on Windows, `dotnet-dump` elsewhere); without `jq` or `dotnet-dump` it says the
  check was skipped and the history line records it; the dump is deleted. Status: Fixed; confirmation below.

### Confirmations

Each reviewer re-read the fixes of the findings it raised (read only):
- gc-specialist: H1 confirmed (the check shared with the load, the refusal at open, the reopen resending trust, ADR-035
  and the refusal text now true); M18, the copy tests, the capture trust rule and the test comments confirmed; M19
  acknowledged as deferred. Two new Moderates (M20, M21) and two Lows (L24, L25), all fixed at once below.
- support engineer: H2 and H3 confirmed, and every Moderate it raised (trust store, the app's status and failure
  handling, capture logging, the field-reader dead end, `SessionLibrary` logging, next steps, the refused run, DAC I/O
  errors, CI logs and timeouts, sections 11 and 12). New Lows recorded (L26 to L30).
- senior-dev: every Moderate and Low confirmed; H4 not confirmed at first: `fixture-dump.sh` passed Git Bash's own
  process number as `--parent-pid`, not Windows's, so the fixture would exit at once on Windows (N1). Fixed (the Windows
  number from `/proc/$$/winpid`, confirmed different on this machine), with its four new Lows (L31 to L34). H4 is
  confirmed by the gate run of 2026-10-29: Debug and Release with 0 warnings, both tiers (1,335 tests) and the fixture
  captured by the CLI on Windows; the known-answer step reported itself skipped (no `jq` on this machine), as designed.
- solution-architect: H1 (its Moderate), the trust store, the formatter test, the Unix lock and new documents,
  `test.sh` and the docs confirmed; the deferred tests accepted. Its new Lows were the graph-only point (as M20), the
  mismatch check (L25), a trust list reset on a slow read (fixed with L24), an unverified claim in ADR-035 and a stale
  comment (fixed), and the literal-text trait guard (L35).

## Moderate (all decided autonomously)

- **M1 The trust store repeated the file problems P1 fixed** (solution-architect, senior-dev, support engineer): reads
  without delete sharing, one replace without retry, an in-process lock only, a failed read or a newer format read as
  empty and then overwritten. Fixed: `trusted-dacs.lock` across processes (a lasting file), reads that share and retry,
  a bounded replace, temp files cleaned up; a file this build cannot read trusts nothing, is logged, and is kept as `.bak`
  before a change writes a new one; the app catches a failed save and says what to do. Tests: two instances writing 40
  entries lose none; a newer file is kept aside.
- **M2 The trust store followed the configurable session root** (solution-architect, senior-dev): a shared or synced
  root put the list of native code to trust where others could write. Fixed: always in the user's own HeapScope data
  folder.
- **M3 Capture trusted its DAC copy too widely and too narrowly** (senior-dev; support engineer, solution-architect,
  gc-specialist as Low): an elevated HeapScope trusted a DAC from another user's self-contained app (that user can write
  it, and the worker runs it elevated), and a copy that already existed (from 3.5, or a failed trust) was never trusted.
  Fixed: not trusted automatically when HeapScope runs elevated (the open asks); otherwise the copy is trusted when its
  content equals the runtime's file, new copy or old; every skipped case is logged.
- **M4 `Open` and `Current` could interleave** (senior-dev): a capture creating a session could overwrite the one just
  opened, and a failed touch left the folder switched. Fixed: `Open` takes the current session's work lock and switches
  only after the touch.
- **M5 `DeleteIfEmpty` ignored other claims** (senior-dev): a failed capture removed a new session another capture or a
  run had taken. Fixed: kept while anything but the caller's own claim writes into it; the document is removed under
  `session.lock` first; the current folder is cleared only for a removed session. Test:
  `An_empty_new_session_is_kept_while_another_capture_or_run_writes_into_it`.
- **M6 The Live page read session documents on the UI thread** (senior-dev): a read now waits up to a second for a
  program that holds the document. Fixed: past runs are read off the UI thread (`RefreshRunsAsync`, `OpenPastRunAsync`).
- **M7 One next step for every unreadable session, and `Open` treated unreadable as missing** (senior-dev, support
  engineer). Fixed: `SessionRead.NextStep` per reason (held: close the program and try again; damaged or newer: open
  another session on Home or restart HeapScope, the dumps untouched); `Open` throws `SessionUnreadableException` with the
  reason, which the app and the CLI show. Test: `Opening_a_session_that_cannot_be_read_says_why`.
- **M8 The trust flow was tested in process only** (solution-architect, senior-dev, gc-specialist). Fixed in part: the
  DAC details through the real JSON-RPC formatter (`DacTrustRpcTests`), the private copy's checks (`TrustedDacCopyTests`:
  reused, a tampered copy replaced, a changed source refused), the CLI's `--trust-dac` (exit 0 and 64), the cache-open
  check. A planted-DAC test through a real worker needs a worker switch for its runtime folders, and the test on Linux and
  macOS needs the integration tier there: both in the Backlog (3.7).
- **M9 The readers test could pass without readers** (senior-dev). Fixed: every reader signals before the first save.
- **M10 The fast tier still hid projects with no tests, and tests were chosen by project** (solution-architect,
  senior-dev; support engineer as Low). Fixed: the Integration project runs in the integration tier only, no other project
  may end with no tests (`--ignore-exit-code 8` is gone), and `test.sh` fails when an Integration or Slow test sits
  outside the Integration project.
- **M11 The gate's history parsing was fragile** (senior-dev; support engineer as Low). Fixed in part: portable
  (`sed '$d'`), failed names quoted. Reading the TRX files instead of console text is a Low left open (L5).
- **M12 `SessionLibrary` logged nothing** (support engineer). Fixed: it takes a logger; a replaced current session, an
  unreadable one, sessions left out of the list and a removed empty session are logged.
- **M13 A run in an unreadable session went on unrecorded** (support engineer). Fixed: a recorded run is refused with the
  session's reason and next step.
- **M14 A DAC that could not be hashed or copied read as "the file could not be read"** (support engineer). Fixed: it is a
  `RuntimeComponentMissing` refusal naming the DAC, the private folder and what to do.
- **M15 CI kept no HeapScope logs, and a hang lost the results** (support engineer; senior-dev as Low). Fixed: the CLI's,
  app's and workers' logs go to `artifacts/heapscope-logs` and are uploaded with the TRX results, always (also after a
  timeout); test steps have timeouts; a missing results folder warns; the Windows job's name says it runs the
  integration tier (before it becomes a required check).
- **M16 Section 11 did not describe the new log lines** (support engineer; solution-architect as Low). Fixed in the docs.
- **M17 `session.lock` and new session documents were unsafe on Linux and macOS** (solution-architect; senior-dev as
  Low; pre-3.6). Deleting the lock file on close let a waiter lock an unlinked file while a third process locked a new
  one; a new `session.json` was written in place, so a reader could see it empty. Fixed: a lasting lock file (deletion
  removes it with the folder; deletion plans leave it out), and a new document written whole and moved into place
  without overwriting.
- **M18 `POST /gc` could hang behind the finalizer backlog leak** (gc-specialist). Fixed: one blocking collection, no
  wait for finalizers.
- **M19 Trust of a DAC beside the dump was untested off Windows** (gc-specialist, solution-architect). Deferred (decided
  autonomously): the integration tier runs on Windows only; running it on Linux and macOS is part of 3.7.
- **M20 A "no" blocked the cached graph, and CLI graph-only commands broke** (gc-specialist; solution-architect as Low;
  from the H1 fix). Types, retention, diffs and trends need no DAC. Fixed: `OpenSnapshotRequest.CheckDac`; after a "no"
  the app opens the cache anyway (field values and strings refused, the status says how to trust it later); the CLI never
  checks; a reopen after a restart never checks; ADR-035 says so.
- **M21 A refused cache open wasted the whole cache read and left its memory uncollected** (gc-specialist; from the H1
  fix). Fixed: the check runs before the cache is read, when a header probe finds a valid cache.

## Low (listed, not raised)

1. The CLI's "trusted for this run" line comes before the command's activity, and `--trust-dac` values are logged as
   their length (support engineer).
2. A `DacNotTrusted` error without usable details is not logged as such (support engineer).
3. After a declined question the app showed the CLI's text (support engineer; fixed: its own status).
4. `if-no-files-found: ignore` hid a broken results path (support engineer, senior-dev; fixed: warn).
5. The gate reads totals and failed names from console text rather than the TRX files (support engineer, senior-dev).
6. `collect-licenses.cs` fails with a bare exception on a changed assets layout, keeps a stale `licenses/` folder, and
   falls back to a relative repository path (support engineer, senior-dev).
7. The Windows job's name left out the integration tier (support engineer, senior-dev; fixed).
8. With the symbol server opt-in, an untrusted DAC beside the dump is refused before the download is tried
   (solution-architect, senior-dev, gc-specialist).
9. Trusted hashes and private copies are never pruned (solution-architect; revoking trust is now documented).
10. The private copy folder is not configurable, and hashing and copying are not cancellable or size-capped
    (solution-architect, senior-dev).
11. ADR-017's 3.6 amendment sat out of order, and ADR-035 said the app asks before opening (solution-architect; fixed).
12. `VerifyDacOnWindows` is ClrMD's default, not set explicitly or tested with an unsigned DAC (gc-specialist).
13. The image match is not repeated on the private copy (gc-specialist).
14. Two workers copying one DAC could race on a loaded copy, and crashed copies left temp files (gc-specialist,
    senior-dev; fixed in part: a matching copy made meanwhile is used, temp files are removed).
15. `TrustedDacs.All()` reads the file on the thread that starts an open (gc-specialist).
16. The planted-DAC test writes into the shared fixture dump's folder and loads the dump once more to find the DAC
    (gc-specialist).
17. Comments in `CounterMonitorTests` misstated the leak's size and the baseline (gc-specialist; fixed).
18. Temp files leaked when a write failed (senior-dev; fixed).
19. Retry deadlines used the wall clock and retried errors that cannot pass (senior-dev; fixed).
20. The CLI monitor test still runs in a 12-second window (senior-dev).
21. The current-folder test passes without proving the wait if the update has not started, and the cross-process tests
    use two instances in one process (senior-dev; the OS locks are the same).
22. `fixture-dump.sh` depended on the working folder and left the fixture running if killed (senior-dev; fixed:
    `--parent-pid` and its own folder).
23. DEVELOPER did not list `POST /gc`, and the repeated-run results were not recorded (senior-dev; fixed below).
24. A trust file held for more than a second was read as unreadable, so a change started a new list over it
    (solution-architect, support engineer; re-review; fixed: the change is refused instead, and the `.bak` name is
    unique).
25. The cache-open check could fail an open for reasons other than trust, and skipped the system and architecture check
    (gc-specialist, solution-architect; re-review; fixed: only a trust refusal escapes, a mismatch returns quietly).
26. `Describe` showed the oldest 20 trusts (support engineer; re-review; fixed: newest first).
27. The refusal's "Trusted folders searched" also lists the file next to the dump, and the test checks the label only
    (support engineer; re-review).
28. A capture with no runtime folder returns silently, and a pre-3.6 cache's identity line shows the old DAC rule with
    no trust note (support engineer; re-review).
29. A bad trust file is warned about on every read, and the unreadable session count on every listing (support
    engineer; re-review).
30. Front ends started by tests write capture trusts into the developer's real `trusted-dacs.json`, which no option
    moves (support engineer, senior-dev; re-review; the entries are the fixture runtime's own DAC).
31. `fixture-dump.sh` passed Git Bash's process number as `--parent-pid` (senior-dev; re-review N1; fixed).
32. `DeleteIfEmpty` could replace a capture's own failure with an I/O error (senior-dev; re-review; fixed: logged).
33. The readers test left its readers running on a failure (senior-dev; re-review; fixed).
34. "Failed before as well" read the wrong field of `gate.ps1`'s rows; the CLI got the app's next step (senior-dev,
    support engineer; re-review; fixed).
35. The trait guard matches the literal `Trait("Category", ...)` form only (solution-architect; re-review).

## Found by CI during the sprint

The draft PR's first runs (CI now runs every test project and the integration tier) found, all fixed:
- `HeapScope.Architecture.Tests` resolved a project import's backslash path, which only Windows reads (Linux, macOS).
- Two capture tests expected behavior from capture, which refuses before writing off Windows (Linux, macOS).
- The runner is elevated, so a discovery test's "not elevated" assumption failed (Windows).
- The cancel-a-load test raced a fast machine with a 3,000-object dump (Windows; now a dump of its own, 300,000 items,
  and, after it raced once more on the close commit, up to five fresh loads until a cancel lands mid-load).
- The second CI run found three more: the capture tests assume Windows throughout (capture is Windows only, ADR-025;
  the project now says `HeapScopeTestsRunOn=Windows` and `test.sh` runs it on Windows only, as the old hand-written list
  did), the new RPC test's pipe name was longer than a macOS socket path allows, and a CLI JSON test parsed stdout and
  stderr merged (`ChildProcess.StandardOutput` now keeps stdout alone).
- The gate's run found one more: `Force_gc_runs_a_full_collection_in_the_target` waited a fixed three counter samples
  for the forced collection to show; it now waits until a sample counts it (bounded), passing three runs in a row.
- Stopping a counter session can cut the EventPipe stream inside a block ("Read past end of stream"); the counter
  monitor now ends cleanly when the stop was asked for (Windows; a real bug).

## Measurements

- Integration tier: 294 tests in 163 s on the development machine.
- The formerly flaky classes (`WorkerIpcTests`, `CounterMonitorTests`, `PageCostTests`, `SessionLibraryTests`,
  `MonitorServiceTests`) passed ten runs in a row on 2026-10-29; `SessionLibraryTests` also five runs in a row after the
  P1 fix.
- A win-x64 publish carries the license files of HeapScope and of 52 of its 84 packages (the others declare their
  license in their metadata only, covered by THIRD-PARTY-NOTICES), and no TraceEvent natives.

## Markdown sweep

After the fixes, every tracked Markdown file was checked against the code (`git ls-files "*.md"`, 50 files; past review
files left as written). Changed by the sweep: `docs/SPRINTS.md` (decision 2 still put the trust file beside the session
library). Already current from the fixes: ARCHITECTURE (session protocol, sections 4.3, 4.4, 11 and 12, ADR-017's
amendment, ADR-035, the code map, protocol 20, the repository ADR's CI amendment), DEVELOPER (gate, tiers, fixture
script, DAC trust and revoking it, `POST /gc`, CI, published builds), THIRD-PARTY-NOTICES (SkiaSharp's components, the
TraceEvent natives out of published builds) and the reviewer briefs and skills (no change needed). `RELEASE-NOTES.md`
gets 3.6.0 at close. No em or en dashes in the changed files.
