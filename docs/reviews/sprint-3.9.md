# Review: Sprint 3.9 Packaging and distribution

Changes since `sprint-3.8`: the sprint itself (ADR-039: release archives, the CLI as a dotnet tool, the worker started
through the dotnet host, clean-machine checks, INSTALL.md) and, as a second priority, the work merged between sprints
that no sprint review had covered (ADR-038's SkiaSharp library without the DNG SDK, no macOS runners, the CI split into
light and heavy Windows jobs, CLI JSON read from stdout in the integration tests, the Markdown review). Four
perspectives, run in parallel with the briefs in `.claude/agents/`: solution-architect, senior-dev, gc-specialist and
support-engineer. None had a shell; each read the files on `sprint/3.9` directly. Moderates are the product owner's to
decide (no autonomy for this sprint).

Totals after dedupe: 2 High, 15 Moderate, 30 Low. A finding raised by more than one reviewer is merged and keeps the
highest severity.

Seen in the full local run (1,494 tests): `LiveWorkflowTests.A_run_records_an_allocation_trace_for_its_whole_length`
failed once under full local load and passes alone; untouched by this sprint (like 3.8's CounterMonitorTests note).

## High / Urgent

- **H1 The documented tool install could install someone else's package** (solution-architect, senior-dev,
  support-engineer). INSTALL.md said `dotnet tool install --global HeapScope.Cli --add-source <folder>` with no version.
  `--add-source` keeps nuget.org, NuGet takes the best version across sources, and HeapScope does not hold the ID
  `HeapScope.Cli` there (decision 4), so anyone could publish `HeapScope.Cli` 99.0.0 and users following INSTALL.md
  would run it instead of the attested file (dependency confusion). `check-tool.sh` had the same exposure. Fix:
  INSTALL.md gives a `nuget.config` with `<clear />` and only the download folder, and installs and updates with
  `--configfile` and `--version`; `check-tool.sh` installs the same way; ADR-039 records the risk. Status: Fixed;
  confirmed by solution-architect, senior-dev and support-engineer ("CI now proves the same path users follow").
  Their notes applied: clear a stray `~/.nuget/packages/heapscope.cli`, the PowerShell path for updates, the .NET 10
  SDK named.
- **H2 INSTALL.md said the tool installs with the .NET runtime** (support-engineer). `dotnet tool install` is an SDK
  command; with only the runtime it fails. Fix: INSTALL.md and ADR-039 say installing needs a .NET SDK and running needs
  the .NET 10 runtime, what a missing runtime looks like, and to use the archive without an SDK. Status: Fixed;
  confirmed by support-engineer.

## Moderate

Decided by the product owner on 2026-11-01 ("all recos"):
- Fixed: M1 (one `WorkerStartException` per case, with tests), M2 (`ToolWorkerTests`: the worker's assembly through the
  dotnet host, Server GC, the host in the start line, left out of `list`; the check scripts assert Server GC through
  `worker info`), M3 (a 32-bit host refused; the architecture-mismatch message names the tool or a source build), M4
  (`release.yml` installs the tool file on Windows and Linux before attaching), M5 (the tool's worker is published, not
  copied; the check refuses a worker executable or runtime at any depth and requires third-party licenses), M6 (each
  runtimeconfig lists `includedFrameworks` and `hostfxr` sits beside it), M7 (failures print a reason and the end of the
  logs; the package job uploads them), M8 (`archive.sh unpack` resolves the path), M9 (`RELEASE-NOTES.md` points to
  INSTALL.md), M10 (INSTALL.md: Windows paths, standalone dotnet-dump, the DAC and its trust, a Settings section, "If
  HeapScope does not start", data folder notes), M12 (every log header names the build form and folder;
  `RuntimeEnvironmentTests`), M14 (the README and INSTALL.md document `Logging__File__MinimumLevel`), M15 (logging and
  privacy sections, with process-model 4.1 and 4.2 and worker-memory 9).
- Deferred to the Backlog: M11, M13, and M14's `--log-level` option.


- **M1 The worker-not-found message is wrong for the tool** (senior-dev, support-engineer; solution-architect and
  gc-specialist as Low). It always names `worker/HeapScope.Worker(.exe)`, which a tool never ships, says "not found"
  when `HeapScope.Worker.dll` is there but no dotnet host was found, and its next step ("Reinstall", "set
  Worker:WorkerPath") does not fit a tool user. No test covers it. Suggested: the resolution returns a reason; one
  message per case naming the worker folder and what to do; a unit test per case.
- **M2 The dotnet-host start path has no test of its own** (senior-dev, gc-specialist; solution-architect as Low). Only
  the path choice and a string helper are unit tested; the real start runs only in CI's package job, which neither the
  local gate nor the integration tiers run, and nothing checks the tool's worker runs Server GC. Suggested: an
  integration test starting the CLI with `Worker__WorkerPath=<built worker>/HeapScope.Worker.dll` (ping, Server GC, and
  on Windows `list` leaving its pid out); `check-tool.sh` asserts `Server GC: True` through `worker info`.
- **M3 A 32-bit or other-architecture host** (gc-specialist; support-engineer; solution-architect as Low). The tool runs
  the worker on whatever host runs the CLI: an x86 `dotnet` gives a 32-bit worker, against ARCHITECTURE.md 2 and the
  budgets, and x86 dumps would fail midway. The architecture-mismatch refusal says "open it with the <arch> build of
  HeapScope", a build that does not exist. Suggested: refuse a non-64-bit dll launch with a clear message; reword the
  mismatch message (releases are x64; the tool on a matching machine, or a source build).
- **M4 The shipped tool file is never installed on Windows** (solution-architect). The release packs on Linux only; CI
  installs a package packed on each runner, and `release.yml` runs no check on what it attaches. Suggested: a release
  job on Windows and Linux installs the packed file and runs `--version`, `worker ping` and `worker info` before the
  publish job.
- **M5 The tool's worker files are globbed from the build folder** (senior-dev; gc-specialist as Low). After a local
  `publish.sh`, `bin/Release/net10.0/<rid>/` (a self-contained worker with its executable and runtime) and any stale
  files are packed; the check only refuses an executable directly under `worker/`. CI and releases pack clean.
  Suggested: publish the worker for the tool (no runtime identifier, no app host) into the tool's `worker/`; the check
  refuses a worker executable, `hostfxr` or `coreclr` at any depth.
- **M6 The Windows "without .NET" check does not prove self-containment** (senior-dev; solution-architect as Low). A
  framework-dependent apphost still finds .NET through the registry and `C:\Program Files\dotnet`. Suggested: assert
  each runtimeconfig has `includedFrameworks` and no `frameworks`, and `hostfxr` beside each executable.
- **M7 Check failures are silent in CI** (senior-dev, support-engineer). Under `set -e`, a failing `worker ping` capture
  exits before printing its output; the runtimeconfig grep has no message; the package job uploads no HeapScope logs.
  Suggested: print the output and a reason on failure; upload the logs with `if: always()`.
- **M8 `archive.sh unpack` fails on a relative `.tar.gz` path** (senior-dev): it changes folder before `tar` reads the
  path. Suggested: resolve the path first and use `tar -C`.
- **M9 The release notes do not link INSTALL.md** (senior-dev, support-engineer): acceptance criterion 4. Suggested: a
  line in `RELEASE-NOTES.md`'s introduction, and the 3.9.0 section at `/close-sprint` points to it.
- **M10 INSTALL.md gaps on the first-snapshot path** (senior-dev, support-engineer): `~` does not expand in Windows
  shells; `dotnet tool install dotnet-dump` needs the SDK (dotnet-dump has a standalone download); nothing on matching
  the dumped runtime's DAC or trusting a DAC that came with a dump; it says settings live in the data folder (they are
  `appsettings.json` beside the executable or environment variables, lost on an update); no "if HeapScope does not
  start" section (Smart App Control, missing ICU or fontconfig, where the log is). Suggested: fix each in INSTALL.md.
- **M11 A worker start failure shows a stack trace in the CLI** (support-engineer). `LogWarning(ex, ...)` reaches the
  console logger, so the user sees the stack and then the same message as "Error:", against ARCHITECTURE.md 11.
  Predates the sprint; the tool makes it the likeliest first-run failure. Suggested: keep the stack in the file log only.
- **M12 Logs do not say which build form ran** (support-engineer). The header has "self-contained True/False" (the tool
  and a source build both read False) and no install folder; the worker's own start line names `dotnet` as its path.
  Suggested: the header names the build form and `AppContext.BaseDirectory`; the worker's line names its folder.
- **M13 The own-process exclusion is silent** (support-engineer). `list` drops HeapScope's processes without a log
  line, and capturing one says "No running .NET process with id N was found", which is false; 3.9 adds a third rule.
  Predates the sprint. Suggested: say "pid N is HeapScope's own" and log the rule that matched.
- **M14 The CLI cannot take `--Logging:File:MinimumLevel`** (support-engineer). The README tells users it adds detail
  for one run; the CLI rejects it, and the environment variable `Logging__File__MinimumLevel` is not documented.
  Predates the sprint. Suggested: document the variable for the CLI (Backlog: a `--log-level` option).
- **M15 ARCHITECTURE.md's logging and privacy sections lag** (support-engineer). Section 11 does not say how to read a
  tool user's log (host in the start line, self-contained False, the worker's path is `dotnet`); section 12 still says
  releases attach tarballs for every runtime identifier. Suggested: update both (part of the docs step).

## Low / Trivial

Listed only; not fixed in this sprint.

- L1 (gc-specialist, solution-architect) The worker's runtime is resolved apart from the front end's; `dotnet exec
  --fx-version` would pin it, and `ExplainExitCode` does not know the host's codes (0x80008096 and the 8-bit forms).
- L2 (gc-specialist) No timing recorded for the tool form (IL only, no ReadyToRun) in budgets.md.
- L3 (gc-specialist, solution-architect) worker-memory.md section 9 and process-model.md 4.1 and 4.2 describe only the
  executable start; ADR-039 does not say it amends ADR-025 for the tool's framework-dependent worker.
- L4 (solution-architect) INSTALL.md and ADR-039 do not say the tool is tested on x64 only (partly done with H2).
- L5 (solution-architect, senior-dev) `ProcessDiscovery` repeats Application's worker layout, matches the path anywhere
  in the command line (not only the first argument; forward slashes missed), and reads each command line twice.
- L6 (solution-architect) The package metadata shows the plain AGPL `LICENSE`, no README, no mention of the additional
  terms: matters once nuget.org is used.
- L7 (senior-dev) A manual `release.yml` run with `dry-run=false` skips the tag and version check.
- L8 (senior-dev) `check-no-dng.sh` treats a grep error as no match (fails open on an unreadable file).
- L9 (senior-dev) `test.sh`'s heavy guard compares project-wide counts, not per class, and misses `typeof` forms.
- L10 (senior-dev) `pack-tool.sh` works only from the repository root, uses regex `grep -x` on paths, and does not
  require any third-party license folder.
- L11 (senior-dev) `check-tool.sh` and `check-install.sh` leave their temp folders behind.
- L12 (senior-dev) The Ubuntu container mounts the dump folder read-write as root, so the later tool check may see the
  container's index cache.
- L13 (senior-dev, support-engineer) The `WorkerPath` comment still says "HeapScope.Worker.exe"; `ResolveWorkerPath` is
  now used only by the message.
- L14 (senior-dev) An uncommented `!` on `WorkingDirectory`; the start log's `{Host}` mixes a phrase and a path.
- L15 (senior-dev) Sprint ids cited beside plain descriptions rather than guards (DEVELOPER.md 4).
- L16 (senior-dev) `dotnet <dll>` without `exec` falls back to SDK command resolution if the dll vanished.
- L17 (senior-dev) A stray blank line in `ChildProcess.Append`; `RunCliAsync` is copied into about ten test files.
- L18 (support-engineer) The `~/.local/bin` symlinked launch is untested; INSTALL.md does not say a new login may be
  needed for the path.
- L19 (support-engineer) INSTALL.md's data folder section misses `$XDG_DATA_HOME`, that full dumps hold secrets, and
  `%TEMP%\HeapScope\etlx`.
- L20 (support-engineer) Explorer's Extract All nests the folder; capturing an elevated process needs HeapScope elevated;
  the Windows CLI example stops before `summary`.
- L21 (support-engineer) README.md says HeapScope runs on macOS, which is not shipped.
- L22 (support-engineer) The between-sprint changes were checked in their current text only, not line by line.
- L23 (gc-specialist) Neither form checks the heap count or concurrent GC of a running worker (`WorkerInfo` lacks them).
- L24 (solution-architect) A second running tool CLI is listed as a process (its shim is outside the install folder).
- L25 (senior-dev) `check-install.sh`'s runtimeconfig grep and similar checks print nothing (merged into M7 for CI).
- L26 (support-engineer) The worker-start-exit message reports a raw host code (merged into L1).
- L27 (solution-architect) The worker-not-found message (merged into M1).
- L28 (gc-specialist) The dll-without-host message (merged into M1).
- L29 (gc-specialist) Local packs after a publish pick up RID folders (merged into M5).
- L30 (solution-architect) The check scripts check neither Server GC nor self-containment (merged into M2 and M6).

## Markdown sweep

After the fixes and the docs step, every tracked Markdown file was searched for what this sprint made stale (tarballs,
the four runtimes in a release, `--add-source`, the old worker messages, the worker's executable as its only form, the
3.9.0 release notes item) and the changed files were read whole. Changed by the sweep: `SECURITY.md` (what a release
carries from 3.9.0), `docs/DEVELOPER.md` (the package job's logs; a long line), ADR-025 and the ADR index (amended by
ADR-039). Changed in the docs step: ADR-039, `INSTALL.md`, `README.md`, `RELEASE-NOTES.md`, `docs/BACKLOG.md`,
`docs/SPRINTS.md`, `docs/architecture/` (code map, logging, process model, security and privacy, worker memory).
Records of past sprints (`docs/sprints/phase-*.md`, past reviews) stay as written. No em or en dashes in any changed
file; every relative link in the changed files resolves.
