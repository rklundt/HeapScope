# Review: Sprint 1.1 Process discovery and capture

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-1.2`). Phase 1 runs in
autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided autonomously".

Totals after dedupe: 1 High, 15 Moderate, 21 Low.

## High / Urgent

### H1 Canceling a capture left a complete dump behind
- Reviewers: solution-architect, senior-dev, gc-specialist.
- Location: DumpCapturer.cs (`WriteAsync` with the job's token, `TryDelete`).
- What: the token only stopped HeapScope waiting for the IPC reply; the runtime's createdump (inside the target) kept
  writing. The delete ran too early or hit a sharing violation that was swallowed. Measured before the fix: a capture
  canceled after 200 ms left a complete 203 MB dump, unrecorded, while the user was told "Capture canceled".
- Why it matters: a file full of secrets left on disk after HeapScope said it was gone (section 12).
- Fix: createdump is never abandoned (`IDumpWriter.WriteAsync` has no token): a cancel reports "Canceling: the runtime
  finishes writing the dump, then HeapScope deletes it", waits for the runtime's reply, deletes the file (retrying in
  the background for up to 30 s if it is still held) and only then ends the job as canceled. The dump is written to
  `<name>.dmp.partial` and renamed only on success, so a half-written file never has a dump's name; a session created
  for the canceled capture is removed. createdump is never killed (its threads hold the target's suspension).
  Tests: unit test with a writer that keeps writing after the cancel; integration test
  `Canceling_a_real_capture_leaves_no_dump_behind` against LeakyApi (no `.dmp` or `.partial` left, no empty session).
- Re-review: gc-specialist and senior-dev did not confirm the first fix: the CLI returned 130 after System.CommandLine's
  2 second grace period and exited while the runtime was still writing, and closing the UI did the same. Second fix:
  the CLI gives Ctrl+C up to an hour (`ProcessTerminationTimeout`; null was tried first and turned out to disable
  Ctrl+C handling), the UI cancels running jobs and waits before the host stops, and stale `*.dmp.partial` files are
  swept when capture starts. Test `Cli_ctrl_c_during_a_capture_waits_for_the_runtime_and_leaves_no_dump`: a real Ctrl+C
  (LoadGen `--ctrl-c`, CLI started through LoadGen `--launch` with Ctrl+C enabled) during a 2.7 GB capture gives exit
  130 and leaves no file.
- Status: **Fixed.** Confirmation: see below.

## Moderate (decided autonomously)

- **M1 Runtime failures escaped the error contract; broad exception mapping** (all three). **Fix now:** specific Capture
  exceptions (`TargetUnavailableException`, `DumpFileExistsException`, `DumpWriteFailedException`); `ServerErrorException`
  decoded (disk full; the target's account cannot write the folder, with `--out` as the next step); post-write steps
  inside the mapped region; `CaptureFailure` gains `Unknown = 0`, `TargetExited`, `FileExists`. Tests for each mapping.
- **M2 Concurrent writes could lose session records; same-second folders shared across runs** (architect,
  senior-dev). **Fix now:** folders claimed by creating `session.json` exclusively; every change a read-modify-write
  under a `session.lock` file and the in-process gate, unique flushed temporary files; `Touch` changes only the last
  opened time and runs off the dispatcher. Tests: two libraries in the same second, 40 concurrent records and touches
  across two libraries lose nothing.
- **M3 Session model not ready for 1.4, 1.7, 1.8** (architect). **Fix now:** snapshot `Id`, `Label`, `Source`, `Kind`
  as the Contracts enum written by name, runtime folder; unknown JSON members kept on save; `SessionLibrary.Open` makes a
  session current (Home's Open, CLI `--session`), CLI `--label`. Test: a future member survives a save.
- **M4 No deletion or retention of dumps** (architect). **Deferred to the backlog** (Medium); section 12 now says where
  dumps live and that they can be deleted by hand until then.
- **M5 Session created before the checks; one-second file names; no TimeProvider** (architect, senior-dev). **Fix
  now:** the session is created only after preflight (failures remove an empty one); counter suffix on collision; times
  from `TimeProvider`. Tests: refused capture leaves no folder, two quick captures get two files.
- **M6 Front ends tied to Capture types through a concrete service; tests enumerated real processes** (architect).
  **Fix now:** `ICaptureService`; view models and their tests use it (`FakeCaptureService`), no Capture reference in
  UI tests; per-test temp folders.
- **M7 The dump estimate scaled the wrong term** (gc-specialist). **Fix now:** `DumpSizeEstimator` sums committed
  readable pages with `VirtualQueryEx`; fallback private bytes x 1.1 + 384 MB + 16 MB. Tests: the measured estimate of
  the test process, the fallback errs high.
- **M8 Free space could be driven to zero** (gc-specialist). **Fix now:** 1 GB is always left free; the message names it.
- **M9 Service targets cannot write the user's folder** (gc-specialist). **Partly fixed:** the error names the cause and
  `--out`; writing through a folder the target's account can write is **deferred** (backlog).
- **M10 Captured dumps became unreadable after a runtime update** (gc-specialist). **Fix now:** the target's
  `mscordaccore.dll` is copied next to the dump and the runtime folder recorded. Test: a captured dump loads with only
  its own folder searched.
- **M11 The target is paused without warning** (gc-specialist). **Fix now:** progress, CLI stderr and the UI status say
  the process is paused while its memory is written; the CLI always prints a new phase.
- **M12 `DriveInfo` wrong for UNC paths and mount points** (senior-dev). **Fix now:** `GetDiskFreeSpaceEx` on the folder;
  unknown free space does not block. Tests.
- **M13 `capture --open` ignored Ctrl+C and showed no progress** (senior-dev). **Fix now:** progress line and cancel
  registration for the open job.
- **M14 Nothing logged in the capture path** (senior-dev). **Fix now:** `CaptureService` logs start, end (size, time),
  refusals and failures with the exception.
- **M15 Edge cases untested** (senior-dev). **Fix now:** real cancel test, long command line (6,000 characters), generic
  failure in the view model, refresh when the Processes page is shown. CLI exit code 130 stays untested (Low).

## Low / Trivial (recorded only)
- L1 (architect) `--out` and `Sessions:Root` accept shares and broad folders without a warning. (A relative root is now
  resolved under HeapScope's data folder.)
- L2 (architect) Unknown private bytes disabled the disk check silently. (The measured estimate and fallback cover it.)
- L3 (architect) `CaptureFailure` default meant "needs administrator". (Fixed with M1.)
- L4 (architect, senior-dev, gc-specialist) Own processes excluded by name; `Find` not excluding them. (Fixed: by pid
  and installation path, in both.)
- L5 (architect, gc-specialist) Check-then-write race on the dump file. (Fixed by the partial name and rename.)
- L6 (architect) Code map and section 12 wording. (Section 12 fixed; code map still lists 0.3 commands.)
- L7 (architect) TraceEvent referenced before 1.7 needs it.
- L8 (gc-specialist) Single-file, x86 and NativeAOT targets. (Backlog.)
- L9 (gc-specialist) File version shown instead of the product version.
- L10 (gc-specialist) Discovery opens each process several times and builds every module object.
- L11 (gc-specialist, senior-dev) Two definitions of "elevated". (Fixed: token elevation for both.)
- L12 (gc-specialist) The temporary probe test. (Removed; replaced by the asserting test.)
- L13 (senior-dev) Raw handles instead of SafeHandles; duplicated declarations.
- L14 (senior-dev) Unexplained `!`; a root path gave `ArgumentNullException`. (Root path now a clear `ArgumentException`.)
- L15 (senior-dev) Early return left module objects undisposed. (Fixed: all modules disposed.)
- L16 (senior-dev) `DateTime.Now` and collisions. (Fixed with M5.)
- L17 (senior-dev) UI: refresh not cancelable, one busy flag, raw byte columns.
- L18 (senior-dev) Tolerant reading gaps and unflushed temp file. (Fixed: entries validated, flush to disk.)
- L19 (senior-dev) Shared test sessions root never cleaned.
- L20 (senior-dev) Hard to read sort expression. (Fixed.)
- L21 (senior-dev) P/Invoke correctness confirmed; no change needed.

## Confirmations
- H1 confirmed by solution-architect after the first fix, and by gc-specialist and senior-dev after the second (CLI
  Ctrl+C waits up to an hour, UI close waits for running jobs, stale partial dumps swept). Their remaining Lows were
  taken where cheap: the "still held" warning now checks the dump's own `.partial` file, the startup sweep runs off the
  constructing thread, unknown capture errors map to `CaptureFailure.Unknown`, the lock file retries on access denied,
  and the wait bounds are documented. Recorded only: a session folder left empty when its held dump is removed later
  by the sweep, a UI hint when the 5 minute bound gives up, and an unbounded wait for a hung target.
- All Moderate decisions were confirmed by the reviewers that raised them (M1 to M6 by solution-architect; M2, M3, M1,
  M12 to M15 by senior-dev; M7, M8, M10, M11 by gc-specialist; M10 refined to one DAC folder per runtime version).

