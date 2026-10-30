# HeapScope Developer Guide

Status: living document, updated by every sprint.

## 1. Prerequisites
- Windows 11 x64 (Windows 10 x64 also supported) for everything. Since sprint 2.3.6 the engine, the worker, the CLI and
  the Avalonia app also build and run on Linux (x64) and macOS (Apple silicon or Intel); capture and the integration tests
  stay Windows only. The build scripts are bash (Git Bash on Windows).
- .NET 10 SDK (10.0.100 or later). `global.json` pins the feature band and rolls forward to the latest patch.
- An IDE supporting .NET 10: Visual Studio 2026, JetBrains Rider 2025.3+, or VS Code with C# Dev Kit.
- RAM: 16 GB is the baseline (Medium snapshots open in mapped mode when memory is tight); 32 GB lets Medium
  snapshots open fully in memory. Generating the Medium sample dump needs about 6 GB free RAM and 5 GB disk.
- Admin rights only for capturing or attaching to elevated processes (ADR-014).

## 2. Clone, build, test, run
```
git clone <repo> heapScope
cd heapScope
dotnet build HeapScope.sln
dotnet test --solution HeapScope.sln
```
- Full gate (sprint 1.9; bash since 3.6, the same steps as CI, section 7): `bash build/gate.sh` builds Debug and Release
  (zero warnings), runs the fast and the integration tiers, then CI's fixture check (a LeakyConsole dump, captured by
  the CLI on Windows and by `dotnet-dump` elsewhere, checked against the known answers, which needs `jq`; without
  `jq` or `dotnet-dump` the check is reported as skipped) and appends a line to `artifacts/gate/history.csv`
  (git-ignored): time, commit, outcome, durations, test counts and failed test names; a test that failed in an earlier run
  is named again. Its log and TRX results are kept under `artifacts/test-results/<time>`. Run it before closing a sprint.
- One tier (sprint 3.6): `bash build/test.sh fast|integration [Release|Debug] [results folder]`. `fast` runs every test
  project that runs on this system but the Integration project, found rather than listed, without the Integration and
  Slow traits; a project left with no tests fails. `integration` runs the Integration project (Windows). Integration and
  Slow tests belong in that project only; the script fails when one is elsewhere. Results are TRX files.
- Fast tests by hand: `dotnet test --solution HeapScope.sln --filter-not-trait "Category=Integration" --filter-not-trait "Category=Slow" --ignore-exit-code 8`.
- Test fakes (sprint 3.8): `FakeWorkerService` (UI tests) is split into a partial per worker contract under
  `tests/HeapScope.UI.ViewModels.Tests/Fakes/`; a test that needs one area only builds an `IWorkerService` from the
  parts it implements with `AreaWorkerService.Create(...)` (Application tests), and any other call fails naming its
  contract. `HeapScope.Cli.Tests` runs the CLI in process (`CliApp.RunAsync` with its own output and error writers):
  usage errors need no host, and the tables are checked as text against fixed rows.
- Data folders (sprint 3.8): every test process sets `HEAPSCOPE_DATA_FOLDER` to `HeapScopeTests/data` under the temporary
  folder (`tests/Shared/TestDataFolder.cs`) before anything reads `HeapScopeFolders`, and the workers and front ends it
  starts inherit it, so no test writes into the user's sessions, logs, index cache key, trusted DACs or bookmarks. Set it
  by hand to run the app or the CLI against a scratch profile. The worker also takes `--data-folder` (from
  `Worker:DataFolder` on the client), which wins over the variable.
- Fuzzing (sprint 3.7): the mutation tests (`SignedCacheTests`, `CraftedGcDumpTests`) run a short pass in every run;
  `HEAPSCOPE_FUZZ_ITERATIONS` and `HEAPSCOPE_FUZZ_SEED` raise and vary it. The nightly `fuzz.yml` runs 2,000 iterations on
  the run number as the seed, on `develop`; a failure names its seed, iteration and exception type. The cache half replays
  from the seed alone. The gcdump half mutates a gcdump captured live, so it also needs that base: with
  `HEAPSCOPE_FUZZ_KEEP=<folder>` (set by the nightly, kept with its results) the test keeps the base (`base.gcdump`), the
  iteration in progress and any failing input there, and `HEAPSCOPE_FUZZ_BASE=<base.gcdump>` replays from it.
- The DAC trust check on a real dump of any system: `HEAPSCOPE_FIXTURE_DUMP=<dump> dotnet test --project
  tests/HeapScope.Loaders.ClrMd.Tests --filter-class "*PlantedDacTests"` (CI's Linux and macOS jobs run it, with
  `HEAPSCOPE_REQUIRE_FIXTURE=1` so a skip fails). It plants the DAC next to the dump and removes it after; a folder that
  already holds a DAC or runtime library is skipped, never overwritten.
- Packages (sprint 3.7): nuget.org only (`nuget.config`); every project has a `packages.lock.json` and CI restores in
  locked mode, so after changing `Directory.Packages.props` or a project's references run `dotnet restore HeapScope.sln
  --force-evaluate` and commit the changed lock files. Dependabot opens weekly PRs into `develop` for NuGet and Actions.
- A LeakyConsole dump for checks by hand: `bash build/fixture-dump.sh <dump> cli=<heapscope-cli>` (or `dotnet-dump`), then
  `bash build/check-known-answers.sh <heapscope-cli> <dump>`.
- A test run that never finishes: run the test executable directly with hung test detection, for example
  `tests/HeapScope.UI.Tests/bin/Debug/net10.0/HeapScope.UI.Tests.exe -longRunning 30 -noAutoReporters`;
  it names the test still running every 30 seconds (sprint 1.5: a grid test waited forever on a page gate).
- Run the UI: `dotnet run --project src/HeapScope.UI` (Avalonia, sprint 2.3.6; Windows, Linux and macOS). Add
  `--headless` to run it without a display (Avalonia's headless platform; the CI smoke runs use it). Builds are AnyCPU,
  so a build from source runs as the machine's architecture: on an arm64 machine (Apple silicon, Windows on Arm) it
  opens arm64 dumps only and refuses an x64 dump with "open it with the x64 build" (the DAC must match the worker).
- Sample dumps of a runtime that is no longer installed (an SDK update removes the old runtime): pass the DAC folder of
  the cached runtime pack, for example
  `--Symbols:DacFolder %USERPROFILE%\.nuget\packages\microsoft.netcore.app.runtime.win-x64\10.0.13\runtimes\win-x64\native`.
- UI tests: `HeapScope.UI.Tests` builds the views on the headless platform (one app for the assembly, `UiSession`);
  a view test fails on any binding error Avalonia logs. Bindings are compiled (`x:DataType` on every view), so most
  binding mistakes fail the build instead.
- Builds per system (sprint 2.3.6): `bash build/publish.sh <rid> <folder>` publishes the app and the CLI for `win-x64`,
  `linux-x64`, `osx-arm64` or `osx-x64`, self-contained and ReadyToRun, each with the self-contained worker in
  `worker/`. CI uploads the four on pushes to `develop` and `main` and on manual runs (artifacts `HeapScope-<rid>`,
  kept 14 days): the download is a zip holding `HeapScope-<rid>.tar.gz`; unpack that with `tar -xzf`, which keeps the
  executables' modes. macOS builds are not signed or notarized yet (Backlog). Since 3.6 every build carries `LICENSE`,
  `ADDITIONAL-TERMS.md`, `COMMERCIAL.md`, `THIRD-PARTY-NOTICES.md` and each shipped package's own license and notice
  files under `licenses/` (`build/collect-licenses.cs`), and leaves out the TraceEvent natives no code uses
  (`HeapScopePublish`, `Directory.Build.targets`); `bash build/check-package.sh <folder>` checks both (publish.sh runs it).
- Run the CLI: `dotnet run --project src/HeapScope.Cli -- --version` (the built executable is `heapscope-cli.exe`).
  Commands: `list` (running .NET processes with runtime, elevation, working set and command line),
  `capture --pid <id> [--kind full|gcdump] [--out <file or folder>] [--session <name>] [--label <text>] [--open]` (full
  dump into this run's session folder, an existing session, or `--out`; the target is paused while it is written; with
  `--kind gcdump` a gcdump instead, sprint 1.8: the object graph only, the target runs one full GC),
  `worker ping`, `worker info`, `synthetic --objects N [--refs R] [--seed S] [--top T]`,
  `open <dump> [--rebuild] [--mode auto|memory|mapped] [--top N] [--no-overcommit]` (loads a full dump or reuses
  its index cache, prints the snapshot, notes and largest types), `summary <dump> [--group type|namespace|assembly]
  [--filter text] [--sort bytes|count|name|retained] [--retained] [--top N | --all] [--format table|csv|json]` (the
  types summary with bytes per heap kind; the filter follows the UI's rules; its JSON says the snapshot's `Kind` and
  whether its counts include unreachable objects, `IncludesUnreachable`, true for a full dump only, sprint 1.9; with
  `--retained` or `--sort retained`, sprint 2.3, a retained bytes column per row, a lower bound, and `Dominators` and
  `RetainedIsLowerBound` in the JSON). Ctrl+C cancels; exit codes
  0 ok, 1 failure,
  64 usage, 130 canceled.
  `diff <baseline> <current> [--group] [--filter] [--sort bytes-delta|count-delta|bytes|count|baseline-bytes|baseline-count|name]
  [--top N | --all] [--format table|csv|json] [--fail-on-growth <size|percent>]` compares two dumps by type (ADR-018);
  with `--fail-on-growth 50MB` or `10%` it exits 2 when the matching rows grew by more (CI), 0 otherwise (growth
  exactly at the limit passes; a percent of a zero baseline allows no growth; a `--filter` that matches nothing exits 1
  instead of passing). A full dump counts unreachable objects not yet collected, a gcdump only the survivors of its
  collection: for a stable gate, capture full dumps after a full GC in the target (`GC.Collect();
  GC.WaitForPendingFinalizers(); GC.Collect();`) or compare gcdumps. Snapshot notes (incomplete heap segments, dropped
  references) are printed to stderr for each side, and in the JSON, which also carries `BaselineKind`, `CurrentKind`,
  `BaselineIncludesUnreachable` and `CurrentIncludesUnreachable` (`IncludesUnreachable` is true only when both are,
  sprint 1.9). Since 3.3: `--match-objects` adds New and Dead objects per row, matched by address, type, size and heap
  area, only for two complete full dumps of one run of a process (otherwise stderr says why and the columns are left
  out; ADR-032). For New and Dead do not force a GC between the two dumps (it moves most objects): that is the
  opposite of the growth gate's advice above, so use one capture procedure per question; `--retained` adds each row's retained
  lower bound in both dumps and the delta; `--sort new|dead|retained-delta|gen2-delta`; `--growth-of gen0|gen1|gen2|loh|poh`
  gates on one heap kind's growth (with a size; a percent there exits 64), and `--fail-on-growth-per-row` checks each
  matching row on its own (a percent against that row's baseline bytes) and lists the rows over it. Both read every
  matching row; the gate's log line names each row over its limit with its baseline and limit. `--sort new|dead` needs
  `--match-objects` and `--sort retained-delta` needs `--retained` (exit 64). `trend <dump> <dump> [<dump>...] [--group] [--filter] [--sort bytes-delta|count-delta|growth-steps|name]
  [--top N | --all] [--format table|csv|json]` puts 2 to 20 dumps side by side, oldest first: bytes in each, the change
  first to last, and the steps a row grew in (dumps of one kind only; a dump given twice exits 64). `set ... --compared-with <dump> --change new|dead|survived` opens a set's
  objects matched against another dump of the process (dead objects live in the earlier dump: give it as the snapshot).
  `instances <dump> --type <name> [--module <file>] [--filter <0xprefix|/regex/|text>] [--sort bytes|address|generation|value|retained]
  [--retained] [--top N | --all] [--format table|csv|json]` lists the objects of a type (sprint 1.5): address, size,
  generation and a string's text (first 100 characters) or an array's length; with `--retained` (sprint 2.3) each
  object's retained bytes, blank for an object no root reaches. Without `--module` the type's only module is found from the
  summary; a name in several modules asks for `--module`, an unknown name exits 1.
  `object <dump> <address> [--references N] [--retained] [--format text|json]` prints one object: type, size,
  generation, whether a root holds it directly, a string's text, its fields as an indented tree (arrays show their first
  100 elements) and its first N outgoing references (default 50) named by field; with `--retained` (sprint 2.3) its
  retained bytes and its immediate dominator. The first retained query of a snapshot computes its dominators (seconds
  on a Medium heap, phases on stderr) and saves them as `<snapshot>.dom.hsidx` next to the index cache; later runs reuse
  them. The address is hex with or without `0x`; no object at it
  exits 1. A snapshot without field values (a gcdump) prints the object's graph facts and, on stderr, why the fields are
  missing, and exits 0. Both print dump contents (field values, strings) to the console only, never to the log.
  `referrers <snapshot> (--address <hex> | --type <name> [--module <file>] [--sort references|objects|targets|bytes|type])
  [--top N | --all] [--format table|csv|json]` (sprint 2.1) lists who references one object (each referrer once, with
  its references to it and, on a full dump, the field that holds it, for objects with up to 1,000 referrers) or the
  objects of a type (one row per referrer type: referring objects, references, targets referenced, bytes). The first
  such query of a snapshot builds its incoming references index (seconds on a Medium heap, noted on stderr) and saves it
  as `<snapshot>.rev.hsidx` next to the index cache; later runs reuse it. Exactly one of `--address` and `--type`, or exit
  64; a type or module that matches nothing exits 1 without building the index. On an incomplete snapshot (a sampled
  gcdump, a truncated dump) stderr says that referrers may be missing, and the JSON carries `CompleteGraph`.
  `paths <snapshot> (--address <hex> | --type <name> [--module <file>]) [--max N] [--expand] [--format text|json]`
  (sprint 2.2) explains why an object or a type's objects are alive: for an object, its key paths among up to N roots
  (default 10, at most 50) and the merged tree from the object toward its roots, each step with the field holding the
  next (full dumps) or "(dependent handle)"; for a type, its key retention paths (default 20, at most 100) with object
  counts and bytes, and the objects no root reaches. Runs of framework steps are folded unless `--expand`; runs of one
  type are always one line ("and N more of this type") and a very long path shows "(N objects not shown)" in its middle.
  An unknown type or an address with no object exits 1, `--max` out of range exits 64; a snapshot whose index cache
  predates format version 2 is rebuilt once on open.
  `retainers <snapshot> [--address <hex>] [--group none|type|namespace] [--depth 0-5] [--top 1-1000] [--format
  table|csv|json]` (sprint 2.4) prints the biggest retainers, the top of the dominator tree (each row's retained bytes are
  exact: siblings never share), with `--depth` levels expanded below each of the `--top` biggest rows; with `--address`,
  the tree below that object and its dominators from the top. `retained-by <snapshot> <type> [--module <file>] [--top N
  | --all] [--format table|csv|json]` prints what every object of the type keeps alive together, exactly, by type,
  with the summary's lower bound beside it (the reachable members, and how many no root reaches, which retain nothing).
  `set <snapshot> (--type <name> [--type ...] [--module <file>] | --namespace <name> | --assembly <file> | --interface
  <name> | --unreachable | --address 0x... [--address ...] | --inspection <name> --row N [--baseline <dump>]) [--similar-group N] [--retained]
  [--view types|instances|generations|dominators|retained|paths|similar] [--top N | --all] [--format table|csv|json]`
  (sprint 3.1) shows an object set in one of the app's set views; `--retained` takes what the set keeps alive instead
  of its members; a set that selects nothing exits 1, two ways of choosing it 64. Since 3.2: `--interface` (full dumps),
  `--view paths` (the set's key retention paths), `--view similar` (Similar Retention groups, numbered from 1, with
  their most different paths) and `--similar-group N` (the set's Similar Retention group N, as the similar view numbers it; opened by its pattern, so it is the same group in another snapshot or refused). `diff --group interface` refuses `--fail-on-growth` (interface rows overlap) and `summary --group interface` refuses `--retained` (exit 64).
  `summary` and `diff` take `--group interface` (full dumps; a type counts under each interface it implements).
  Since 3.5: `--unreachable` (full dumps with a complete graph) is the objects no root reaches, with the retention views'
  roots (the finalizer queue counts: objects waiting for their finalizer are not in it); a gcdump or an incomplete dump
  is refused (exit 1).
  The logs name an objects set (bookmarks) by its count and `#hash`: the first 8 bytes, in hexadecimal, of the SHA-256 of
  its distinct addresses sorted ascending, each as 8 little-endian bytes (`ObjectSetSpecs.AddressHash`).
  Both compute the snapshot's dominators on first use and save them next to the index cache. An unknown type, or an
  `--address` with no object or one no root reaches, exits 1; a bad option 64. A tree cut at 50,000 rows, or a `--depth`
  that would open more than 10,000 rows, is noted on stderr in every format; JSON echoes the depth reached and `--top`.
  `inspect <dump> [<name>] [--baseline <dump>] [--top N | --all] [--format table|csv|json]` runs the simple inspections
  (sprint 1.6) and the leak detectors (sprint 2.6): without a name, every inspection with its time, finding and note
  (one line each); with a name (`duplicate-strings`, `empty-collections`, `oversized-collections`, `sparse-arrays`,
  `large-arrays`, `loh`, `finalizable`, `fragmentation`, or a detector: `growing-static-collections`,
  `event-handler-leaks`, `timer-leaks`, `disposed-but-referenced`, `httpclient-instances`,
  `undisposed-service-scopes`, and since 3.5 `pending-async-methods`, `finalizer-backlog`, `type-growth`), its cause,
  fix and rows. `--baseline` is an earlier dump of the same process: the growth detectors report what grew since it
  (`type-growth` needs one). An unknown or unavailable inspection exits 1. A dump opened
  from an index cache written before 1.6 has no free space or reference-array marks: reopen it with `open --rebuild`.
  `monitor --pid <id> [--interval 1] [--duration N] [--every 5m] [--above 500MB] [--growth 100MB|50%]
  [--rearm once|below|step] [--max N] [--format table|csv] [--kind full|gcdump]` watches a process's GC heap (the heap now and after the
  last GC, per generation, LOH, POH), collections, allocations and the last GC's share of time every interval (sprint
  1.7) and captures full dumps into the current session when a rule fires on the heap after the last GC (labeled with
  the rule, recorded as timed; `below` re-arms under 90 percent of the threshold); it prints a line per sample, a line per snapshot and,
  at the end, the timeline file (`counters-<pid>-<time>.csv` in the session folder). Ctrl+C stops; a process that
  cannot be monitored exits 1.
- DAC lookup (ADR-017): local only. A dump from a runtime that is not installed needs its `mscordaccore.dll` in a DAC
  folder (CLI `--dac-folder <folder>`, UI `--Symbols:DacFolder <folder>` or the `Symbols:DacFolder` setting) or next to
  the dump, where it is loaded only once trusted (ADR-035, sprint 3.6): the app asks when it opens the dump (loaded or
  from its index cache), naming the file and its SHA-256, and remembers a yes in `trusted-dacs.json` in HeapScope's data
  folder (`%LOCALAPPDATA%\HeapScope`); the CLI refuses, names the hash and the trusted folders it searched, and
  `--trust-dac <sha256>` (repeatable) trusts it for that run. A DAC HeapScope's capture copied is trusted already, unless
  HeapScope ran elevated. To revoke a trust, delete its entry (or the file); the private copies under
  `%LOCALAPPDATA%\HeapScope\dac` can be deleted at any time. `--allow-symbol-server` (CLI) or `Symbols:AllowMicrosoftSymbolServer true` opts in to
  downloads and logs a warning. Both reach the worker on its command line only.
- Worker GC heaps: half the logical processors by default, so the worker cannot starve the UI; override with
  `--Worker:GcHeapCount <n>` (UI), the `Worker__GcHeapCount` environment variable, or `DOTNET_GCHeapCount` (hexadecimal).
- Run the worker standalone (for debugging): `dotnet run --project src/HeapScope.Worker -- --pipe <name>`
  (without `--parent-pid` it does not watch a parent; add `--enable-test-hooks` for the crash and hang hooks).
  Attach a client with `dotnet run --project src/HeapScope.Cli -- worker ping --pipe <name>`, or set
  `Worker:AttachPipeName` for the UI.
- Live smoke run: `HeapScope --smoke-live 1800` charts 1,800 made-up samples on the Live page as fast as it can
  (no process, no timeline file) and exits with the same codes as the other smoke runs.
- UI smoke run: `HeapScope --smoke-synthetic 5000000` runs a synthetic job, checks dispatcher lag and exits
  (0 ok, 3 lag over the gate, 4 job failed, 5 the first window took over 30 s); details in ARCHITECTURE.md 8.
  `HeapScope --smoke-open <dump>`
  opens a dump through the Snapshots page instead, with the same checks; add `--smoke-compare <dump>` to also open a
  second dump and check the Compare tab (its cold first show is logged, a warm re-query is checked). `--smoke-open`
  also drills down (types, instances, object, the object's incoming references and retention paths, the type's
  referrers and key paths; sprints 2.1 and 2.2),
  shows the Inspections tab ("Smoke inspections": every inspection's
  finding and time), the Retainers tab ("Smoke retainers": its cold first show logged, a warm expand and focus checked,
  sprint 2.4) and logs the open phase on its own line ("Smoke open phase"); add
  `--smoke-trace <file.nettrace>` to record a CPU trace of the UI during the run. The log records
  "First window rendered N ms after process start" for every run, and `--smoke-open` logs the largest types it showed
  ("Smoke open types"), which CI checks against the known answers. Every smoke run also works with `--headless`.
  Add `--smoke-allocations <file.nettrace>` to `--smoke-open` to also show an allocations trace on the Allocations page
  ("Smoke allocations": its cold first show logged; expanding the first back trace and the call tree checked; sprint 2.5).
- Traces: `heapscope-cli trace --pid <id> [--duration 30] [--profile cpu|gc|allocations] [--out file.nettrace]` records an
  EventPipe trace of any .NET process (no dotnet-trace needed; Ctrl+C stops early and keeps it). An `allocations` trace
  is read by `heapscope-cli allocations <file> [--from s] [--to s] [--type <name>] [--types N] [--stacks N] [--frames N]
  [--view summary|types|stacks] [--format]` (the top types and call stacks of an interval, sizes estimated from samples)
  and by the Allocations page (sprint 2.5). Read any trace with PerfView,
  Visual Studio, or `dotnet run -c Release --project perf/HeapScope.Benchmarks -- trace-report --trace <file>`
  (`--timeline` for busy UI time per window, `--from HH:mm:ss.fff --to ...` for one stretch, `--focus <frame text>` for
  the callers of a frame). It finds the UI thread by Avalonia's dispatcher (WPF's for old traces) and counts a sample
  idle when the thread waits in its run loop (sprint 3.8). Traced runs are slower: use them to find where time goes,
  not to check budgets.
- Live sources (sprint 3.8, ADR-037): `dotnet run -c Release --project perf/HeapScope.Benchmarks -- live-tail --seconds
  10` records an allocating copy of itself and reads the trace file while it grows, reporting events read and lost,
  the delay to the reader and its memory.
- Hybrid CPUs: when measuring UI responsiveness, remember Windows may put a background window's threads on efficiency
  cores. HeapScope's UI opts out (ADR-019); pinning a process (`ProcessorAffinity`) to performance or efficiency cores
  is how that was proven.
- Inspection: in the Snapshot tab, double-click or Enter opens a type row's objects, an object, or a reference; Back
  and Forward (buttons, the mouse's back and forward buttons, Alt+Left and Alt+Right). An object's references have
  Outgoing and Incoming tabs (sprint 2.1; incoming is loaded when its tab is first shown); a type's objects have a
  Referenced by button listing the types whose objects reference them, and a row there opens that type's objects.
  The Retention tab (sprint 2.2) shows why the object is alive: key paths and a tree from the object toward its roots,
  framework steps folded unless "Show framework steps"; a type's objects have a Why alive button listing their key
  retention paths, and a row opens an example object. The instances filter is an
  address prefix (`0x1f2a`), a regular expression on the value (`/^Get.*Async$/`) or text in the value. The types grid's
  "Generations" checkbox shows bytes per generation.
- Live (sprint 1.7): the Live page monitors a process (pick it, set the sample interval and the snapshot rules, Start):
  a stacked chart of the GC heap per heap kind over the last 30 minutes with dashed lines at snapshots, the latest
  values, a "High GC pressure" badge (10 percent or more of the last ten intervals in GC) and a card per snapshot,
  with Show in folder for its file.
  When a run stops, a row shows its timeline file with Open (the system's app for CSV), Show in folder (Explorer and
  Finder select the file; elsewhere the folder opens) and Delete (asks first; the run's snapshots are kept). The log
  says which was used and how it went, never the path.
  Since 3.4: Get snapshot captures the process now as one of the run's snapshots; Force GC asks first (the target
  pauses, finalizers do not run), then runs a full blocking collection in it (a dotted line on the chart, only when its
  end was seen; .NET 5 or later); "Record allocations" records an allocation trace for the whole run, up to 1 GB or
  until the disk has 1 GB free (Open allocations shows it on the Allocations page); "Past runs of this session" reopens
  a run's timeline, markers and snapshot cards read only. A capture or Force GC of a process already being captured is
  refused with the reason, from any page; a rule due meanwhile fires at the next free sample. The Snapshots page's
  "Close and delete..." deletes a session snapshot with its index caches; the Home page deletes a session ("Delete...")
  or the sessions not opened for N days ("Clean up old sessions..."); each lists the files and asks first, only files
  inside the session folder go, and the current session and sessions with an open snapshot, a capture or a run are
  kept. The Live smoke run records nothing in the session library (`RecordRuns` off).
- Inspections (sprint 1.6): the Snapshots page's Inspections tab runs the selected snapshot's inspections the first
  time it is shown (one at a time, in the worker): a tile per inspection with its finding, cause and fix (or why it is
  unavailable), fragmentation bars per heap, and the selected inspection's rows; double-click or Enter opens a row's
  objects (a type in its scope, equal strings, or one object) on the Snapshot tab.
- Theme: the UI follows the system's light or dark setting (Windows, macOS, the Linux desktop); `--Ui:Theme Light|Dark`
  or the `Ui:Theme` setting overrides it. Colors live in `src/HeapScope.UI/Themes/Light.axaml` and `Dark.axaml` (theme
  dictionaries), which must define the same keys (a test checks it); views use `DynamicResource`, never literal colors.
- Avalonia updates: before any update, check that no Avalonia component HeapScope uses moved to a paid license
  (ADR-025, CLAUDE.md). Avalonia's build telemetry is off for the repository (`Directory.Build.targets`).
- Sessions: captures go to `%LOCALAPPDATA%\HeapScope\sessions\<yyyyMMdd-HHmmss>` with a `session.json`; override the
  root with `--Sessions:Root <dir>` (UI) or the `Sessions__Root` environment variable. Integration tests point every
  front end they start at a temporary root.
- Ctrl+C in tests: `LoadGen --launch <exe> [args]` runs a program with Ctrl+C enabled (children of the test host
  inherit "ignore Ctrl+C"), and `LoadGen --ctrl-c <pid>` sends Ctrl+C to that program's console.
- A canceled capture waits for the runtime to finish writing and then deletes the dump: the CLI waits up to an hour
  after Ctrl+C, closing the UI waits up to 5 minutes; anything left (`*.dmp.partial`, `*.gcdump.partial`) in the session
  library is swept at the next start; a folder the user picked (`--out`) is never swept (sprint 3.7), so a leftover there
  is the user's to delete.
- Logs (sprint 2.3.7, ARCHITECTURE 11): a file per process and day, `heapscope-yyyyMMdd-<ui|cli>-<pid>.log`, in
  `%LOCALAPPDATA%\HeapScope\logs` (Linux `~/.local/share/HeapScope/logs`, macOS `~/Library/Application Support/HeapScope/logs`),
  with forwarded worker lines (`w<pid> g<generation>`); override the folder with `--Logging:File:Directory <dir>` (UI)
  or the `Logging__File__Directory` environment variable, the level with `Logging:File:MinimumLevel`. To follow one
  action across the UI or CLI and the worker, search for its `TraceId`. To find why a result was shown: the worker's
  "Snapshot ... opened" identity line, then the "... answered in" line of the query (and "Inspection ... basis" for an
  inspection). Kept 14 days, 50 MB per file. New log lines carry names and numbers only (ARCHITECTURE 11's policy); a
  request or result goes through `Provenance`, never formatted by hand.
- Tests that measure responsiveness run in an xUnit collection with parallelization disabled.
- Tests that need gigabyte heaps or dumps (DenseHeap, the CLI Ctrl+C capture) run alone in `HeavyTestsCollection`, so
  they cannot slow other tests into timeouts; they also carry `Category=Slow`.
- Benchmarks: `dotnet run -c Release --project perf/HeapScope.Benchmarks -- <benchmark> [options]`. See
  section 6.

Build output: UI and CLI get the worker deployed into a `worker/` subfolder of their output by
`build/Worker.Deploy.targets`.

## 3. Fixtures and sample dumps
Leak names: `StaticCache`, `EventHandler`, `Closure`, `UndisposedList`, `UnboundedDictionary` (shared code in
`fixtures/Leaks`, namespace `Fixtures.Leaks`; each leak is rooted by a static field of its leak class). Fixture
projects are always compiled optimized (they stand in for production apps; see ARCHITECTURE.md 6.1 on stack roots
of debuggable code) and exit when the process given as parent exits.
- `fixtures/LeakyApi`: `dotnet run --project fixtures/LeakyApi -- --urls http://127.0.0.1:5080
  --Leaks:Enabled=all --Leaks:ItemsPerRequest=100 --Leaks:PayloadBytes=64 [--Leaks:Publishers=N]
  [--Leaks:Detected=all|none|Timer,DisposedReferenced,PendingAsync,FinalizerBacklog] [--Clean:Enabled=true] [--ParentPid=P]`.
  `Leaks:Detected` adds the sprint 2.6 detector leaks (a started timer never disposed, disposed objects in a static list)
  and sprint 3.5's (async calls awaiting tasks nothing completes; finalizable items behind a finalizer that blocks the
  finalizer thread for good, so use it only in a process you will kill); `Clean:Enabled` runs the clean workload on each
  `POST /work` (a bounded cache, unsubscribed handlers, disposed timers and objects, finished async calls kept).
  Prints `READY pid=<pid> url=<url>` when listening (use port 0 for a free port). `POST /work` grows every enabled
  leak by `ItemsPerRequest` items; `GET /leaks` shows counts (0 for disabled leaks, which are never touched);
  `POST /leaks/{name}/grow?count=N` grows one enabled leak (404 unknown, 409 disabled, 400 negative); `GET /health`;
  `POST /gc` runs one blocking full collection (sprint 3.6: tests of after-GC sizes ask for it rather than wait).
- `fixtures/LeakyConsole`: `dotnet run --project fixtures/LeakyConsole -- --leak all|none|Name1,Name2
  [--detected all|none|Timer,DisposedReferenced,PendingAsync,FinalizerBacklog] --count N --payload BYTES [--publishers N] [--semantics N] [--runtime-semantics N] [--late-garbage N] [--inspection] [--waste]
  [--parent-pid P]`;
  prints `READY pid=<pid>` and waits to be dumped and killed. `--semantics` adds the conditional weak table and
  embedded struct fixtures, `--runtime-semantics` the finalizable, POH, LOH, pinned handle, frozen literal and
  parked thread fixtures, `--late-garbage` dead table entries and weak-only objects created after the last full GC,
  `--inspection` one object with fields of every kind (1.5), `--waste` duplicated strings, empty and oversized
  collections, large and sparse arrays, finalizable objects and pinned buffers with known amounts (1.6, `Waste.json`).
- `fixtures/LoadGen`: `LoadGen --url <url> --requests N --concurrency C [--dump <path> --pid <pid>
  [--dump-type Full|WithHeap]]` drives `POST /work`, then optionally writes a dump of the target.
- `fixtures/scripts/New-SampleDump.ps1 -Tier Small|Medium [-Port N]` builds the fixtures, checks the port, runs
  LeakyApi with all leaks (its output goes to a `.log` next to the dump), drives it with LoadGen and writes a full
  dump into `artifacts/dumps/` (git-ignored); a failed run deletes its partial dump and a size outside the tier's
  range is warned about. Measured: Small about 560 MB (5.6M objects) in about 10 s; Medium 4.0 GB (57.5M objects)
  in about 12 s, needing about 4 GB of RAM while running.
- `fixtures/KnownAnswers/*.json`: what the analysis must find for each leak and semantic fixture (format version 1
  and path shape grammar in its README). Integration tests verify every field with ClrMD (`KnownAnswerTests`,
  `DisabledLeakTests`, `LeakyApiGrowthTests`). Test harness: `FixtureProcess` (start LeakyConsole or LeakyApi,
  drive, capture any number of dumps, always killed), `DumpAnalysis` (ClrMD checks), `TempFolder`.
- Benchmarks `dump-stats --dump <path> [--by-address 1] [--cache-mb N]` (ClrMD walk: objects, heap, references,
  timings, peak private bytes and working set) and `stack-roots --dump <path>` (diagnostic: stack roots per thread
  and frame). Both are measurement only, without a budget check.

## 4. Coding conventions
- **Naming**: .NET conventions. `PascalCase` types and members, `_camelCase` private fields, `s_camelCase`
  private static fields, `I` prefix for interfaces, `Async` suffix on async methods.
- **Files** (sprint 3.8, peer review S9): one public type per file, except a Contracts file that holds one area's DTOs
  and small records or enums that serve only the file's main type. `PublicTypesPerFileTests` lists the files that held
  more in 3.8: none may be added, a listed file may not grow, and a file split below two leaves the list (update
  `PublicTypesPerFile.txt`). Split a listed file when a change works in it anyway.
- **Comments** (sprint 3.8, peer review S8): state the current reason, not the history. Cite a sprint, review or ADR
  id only beside a guard someone might otherwise simplify away, so its reason can be found; the history lives in git,
  the review files and the ADRs. Apply it to the code a change touches.
- **Derived state** (sprint 3.8, peer review P20): Analysis returns values and keeps nothing, no static caches and no
  mutable static state (`AnalysisStateTests`). What a snapshot derives gets a `DerivedData` slot in the worker:
  computed once for concurrent callers and dropped when the snapshot closes.
- **Nullable** is enabled everywhere; no `!` suppressions without a comment explaining why.
- **Warnings are errors**; do not disable a warning without a comment and reviewer agreement.
- **Async**: every async method takes a `CancellationToken` last (after `IProgress<T>` when present). Library
  code uses `ConfigureAwait(false)`, enforced by CA2007 for every `src/` project except the UI projects and Cli (see
  `.editorconfig`). Never `.Result`, `.Wait()` or `async void` (except UI event handlers,
  which must catch everything).
- **MVVM**: CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). No code-behind
  logic beyond view-only concerns. View models live in `HeapScope.UI.ViewModels`, which references no UI toolkit; they
  get services by constructor injection and never reference controls. Long work uses async relay commands with
  cancellation. Views use compiled bindings (`x:DataType`).
- **Grids, keys and links** (sprint 2.7): every `DataGrid` sets `ctl:GridExports.ExportName` to a fixed name (a test
  fails on one without), which gives it "Export to CSV..." and Command+E; a result column that names what a row opens
  is `IsHidden`, and one that carries the target's data (string text, field values) is `HasValues`, so exports leave it
  out unless the person opts in. Keyboard shortcuts live only in `Shortcuts` (the window and the About page read it).
  A filter box is named `FilterBox` (Command+F). A row that opens an object implements `IObjectLink` (with
  `LinkTypeName` when it knows the type), which also makes it bookmarkable with Command+D.
- **Logging**: `ILogger<T>` via DI. Use structured templates, not string interpolation. Never log snapshot
  field values or string contents.
- **Exceptions**: throw specific exception types; catch only at boundaries (RPC methods, commands, CLI
  handlers). User-facing messages say what happened and what to do next.
- **Hot paths** (loaders, graph, analysis, storage):
  - Struct-of-arrays and `Column<T>`; work with `ReadOnlySpan<T>` and indices, never per-object classes.
  - No LINQ, lambdas capturing state, boxing or `foreach` over interfaces inside per-object loops.
  - Temporary buffers from `ArrayPool<T>.Shared`, returned in `finally`.
  - Allocate final arrays once at exact size; grow with chunked buffers, not `List<T>` doubling.
  - Check the `CancellationToken` at least every 64K iterations.
- **Text**: no em dashes or en dashes in code, comments, docs or UI text. Use a hyphen or a comma.
- **License header**: every `.cs`, `.xaml` and `.ps1` file we own starts with the header in
  `docs/licensing/SOURCE-HEADER.txt`, in the file's comment syntax (copy it from any existing file). Vendored code lives
  in a `ThirdParty` project or folder, keeps its own header and is listed in `THIRD-PARTY-NOTICES.md`. A test enforces it.
- **Vendored code** (`src/HeapScope.ThirdParty.GcDump`, sprint 1.8): built without analyzers or a nullable context.
  Change it only when unavoidable, mark each change with a `HeapScope change` comment and list it in the project's
  `CHANGES.md`. Wrap it in our own types (`GcDumpCapturer`, `GcDumpLoader`); nothing else uses it directly.
- **Attribution**: the "Powered by HeapScope by Ray Klundt" notice (ADR-022) stays on the main window, unchanged; CLI
  help, version and console startup print it. UI changes are checked against it; a test checks its text, link,
  colors and contrast.
- **Dependencies**: check the license before adding one. MIT, BSD, Apache-2.0, LGPL, GPLv3 and AGPLv3 are fine;
  anything else (GPLv2 only, proprietary, SSPL, non-commercial, unlicensed) needs the product owner first. A package
  that only arrives as a dependency counts too: `Avalonia.Fonts.Inter` (the Inter typeface, SIL Open Font License) comes
  with `Avalonia.Headless` and is kept out of the app's output (`ExcludeAssets="all"`), so it reaches only the tests. Update
  `THIRD-PARTY-NOTICES.md` whenever a dependency is added, removed or vendored.

## 5. Testing strategy
- **Unit tests** (`*.Tests`): fast, deterministic. Analysis is tested on synthetic graphs built with
  `SyntheticGraphBuilder`, where the right answer is known by construction.
- **Architecture test**: `HeapScope.Architecture.Tests` enforces the dependency table in ARCHITECTURE.md.
- **Integration tests** (`[Trait("Category", "Integration")]`): start real worker processes and fixture apps,
  capture real dumps, and assert the known answers.
- **Slow tests** (`[Trait("Category", "Slow")]`): large synthetic graphs; part of the full run.
- **Performance checks**: `perf/HeapScope.Benchmarks` measures against the budgets in ARCHITECTURE.md
  section 10; sprints that touch hot paths record results in their review file.
- Runner: xUnit v3 on Microsoft.Testing.Platform (opted in through `global.json`); test projects are executables
  and can also be run directly from their output folder.
- Assertions: Shouldly or plain xUnit asserts. FluentAssertions is not allowed (commercial license).
- Tests that call methods taking a `CancellationToken` pass `TestContext.Current.CancellationToken` (xUnit1051).

Dense sample dump (sprint 1.2): `./fixtures/scripts/New-DenseDump.ps1 -TargetMB 3000` starts the DenseHeap fixture
(real Roslyn objects, about 3 references per object), captures a full dump into `artifacts/dumps` and always stops
the fixture (a `finally` kill, and the fixture exits by itself when the script's process exits). About 2 minutes and
a 4.4 GB dump for 3,000 MB. Load it with `load-dump --dump <path>`.

## 6. Benchmarks
`perf/HeapScope.Benchmarks` is a plain console app (no BenchmarkDotNet, because the interesting cases use
gigabytes of memory and run once). Each benchmark prints elapsed time, managed memory and process working set,
and compares against the budget, returning exit code 1 when a budget is missed. Run in Release.

- `graph-build [--objects 50000000] [--refs 3] [--seed 42] [--reverse 1] [--by-address 1]`: builds a seeded
  random graph (`--by-address 1` resolves references by address like a loader; the realistic, slower path);
  `--reverse 1` adds objects in descending address order to exercise the sort path. It checks the 30 to 38
  bytes per object budget.
- `index-cache [--objects 50000000] [--refs 3] [--dir <folder>] [--phase all|write|reopen]`: writes a synthetic graph
  to an index cache and reopens it in memory and mapped (all checksums verified), against the reopen and mapped
  private memory budgets. `--phase write` then `--phase reopen` in a fresh process (optionally after a standby list
  purge as administrator) measures a cold reopen.
- `dump-stats --dump <path> [--by-address 1] [--cache-mb N]`: a raw ClrMD walk (the 0.4 baseline).
- `load-dump --dump <path> [--cache-mb 512] [--keep-cache 1]`: HeapScope's loader on a real dump: counts, density,
  dropped data and segment completeness, private bytes when each phase starts and at peak, the cache write, the switch
  to mapped mode and both reopens, checked against the ARCHITECTURE.md 10 budgets (exit code 1 on a miss).
  `--cancel-at <phase>` cancels as a phase starts and checks the 2 s cancellation budget.
- `dump-stats --dump <path> --segments 1`: per segment, the object range, the bytes walked and the gap at its end
  (used to set the incomplete-segment rule).
- `phase2-indices`: the 1.9 prototype of the Phase 2 indices (reverse references, semi-NCA dominators, retained sizes)
  on synthetic shapes or an index cache (ARCHITECTURE.md 6.4).
- `dominators --cache <snapshot.hsidx> [--mode memory|mapped] [--out <folder>] [--max-working-set-mb N]` (sprint 2.3; since
  2.4 it also builds the dominator tree's children, reports their time within the dominators phase, and measures set
  retention of the type entry with the most objects; the
  cap, a hard limit on the process's working set to see the computation under memory pressure, after the 2.3 review
  M14; it also reports page faults and the peak working set): the worker's path on a
  real index cache: the reverse references and the dominators, computed straight into sidecars (in `--out`, or a temporary
  folder; only the two sidecars are deleted afterwards, and the folder only when the run made it) and mapped, or in managed arrays; the retained lower bounds per type; a reopen of the dominators sidecar.
  Times and peak private bytes per phase; exit 1 when the indices exceed 60 s or the run's peak 4 GB.
- `allocations --trace <file.nettrace>` (sprint 2.5): reads an allocations trace as the worker does, with the read's time
  and its growth over the private bytes before it against the file's size (the worker reserves 96 MB plus 3 bytes per
  file byte), what reading found (rundown, unresolved frames, conversion problems), what the samples keep, and the
  whole-trace types, top stacks and both trees. Run it with the worker's GC settings, or the growth reads low:
  `DOTNET_gcServer=1 DOTNET_gcConcurrent=0 DOTNET_GCHeapCount=<half the logical processors, hex>`.
- `retention --cache <snapshot.hsidx> [--mode memory|mapped] [--types 3]` (sprint 2.2): the root path tree, the key
  paths of the types with the most objects and one object's paths of each, with times and peak private bytes; exit 1
  when the tree exceeds 60 s or a query 5 s.
- `reverse-index --cache <snapshot.hsidx> [--mode memory|mapped] [--dir <folder>]` (sprint 2.1): opens a real index
  cache, builds its reverse references index, writes it as a sidecar (in `--dir`, deleted afterwards) and reopens it in
  both modes, with times and private bytes; exit 1 when the build exceeds 60 s or the peak 4 GB.

## 7. Git conventions
- Remote: https://github.com/rklundt/HeapScope (ADR-012). `main` holds releases, `develop` collects sprints. Both are
  protected by a ruleset: changes arrive only by PR (no approvals required, since there is one maintainer); direct
  pushes, force pushes and deletion are blocked.
- Each sprint works on `sprint/X.Y`, branched from an up-to-date `develop` by `/start-sprint`.
- Commits happen only through `/close-sprint`, one per sprint, message `Sprint X.Y: <title>` plus a short body. Since
  sprint 2.3.5, work-in-progress commits may be pushed to `sprint/X.Y` to run CI on its draft PR; the squash merge still
  leaves one commit per sprint on `develop`. Work outside a sprint that the owner asks for (a fix, docs, a chore) uses
  its own short branch (`fix/...`, `chore/...`) off `develop` and the same PR flow.
- CI (`.github/workflows/ci.yml`, sprints 2.3.5, 2.3.6 and 3.6) runs on every PR into `develop` or `main`, every push to
  them and on demand, on GitHub's hosted runners. Tests run through `build/test.sh` and the fixture dump through
  `build/fixture-dump.sh`, the scripts the local gate uses; every job keeps its TRX results 14 days
  (`test-results-<system>`), which is where a test that fails now and then shows up:
  - Windows builds the solution, runs the fast and the integration tiers (about three minutes; since 3.6), captures the
    LeakyConsole fixture with the CLI and checks the known answers (`build/check-known-answers.sh`).
  - Linux builds the engine, the worker, the CLI and the Avalonia app, runs their tests (the view model and headless UI
    tests included), dumps the fixture with `dotnet-dump` (pinned), checks the CLI's JSON against the known answers with
    `jq` (counts, sizes, retained sizes, the path root, a complete graph, a named runtime version) and opens the dump in
    the app without a display (`--headless --smoke-open`). macOS does the same, informational (an arm64 runner;
    ADR-025).
  - After both pass, a package job publishes the self-contained builds per runtime identifier (`build/publish.sh`, with
    the license files checked by `build/check-package.sh`), packs each as a tarball (which keeps the executables' modes),
    unpacks it, checks it again and runs it as a user would (the CLI's version, a worker ping, the worker's Server GC
    setting, a headless synthetic smoke; `osx-x64` is built but cannot run on the arm64 runner) and, on pushes and manual
    runs, uploads the tarballs (`HeapScope-<rid>`, kept 14 days).
  The local gate runs the same tiers; run it before closing a sprint. Fixture dumps are never uploaded. Making the CI jobs
  required checks on `develop` and `main` is a repository setting the owner applies.
- A sprint's PR into `develop` is squash merged; the annotated tag `sprint-X.Y` goes on the resulting commit on
  `develop` and is pushed. In autonomous mode Claude merges sprint PRs once the gate passes; otherwise the owner
  merges.
- Versions follow the sprint number: `/close-sprint` sets `<Version>X.Y.0</Version>` in `Directory.Build.props`
  (`X.Y.Z` for an in-between sprint such as 2.3.5).
- Releases: when the owner says so, a PR from `develop` to `main`, merged with a merge commit (never squash, so the
  branches do not drift), then the annotated tag `vX.Y.0` on `main` and a GitHub Release (`gh release create`, marked
  pre-release for now). Only the owner decides when to release.
- Release builds (sprint 3.7): publishing the GitHub release runs `release.yml`, which builds the tag for every runtime
  identifier (read access only) and then, in one job that builds nothing, attaches the tarballs, an SBOM of the app, the
  CLI and the worker, and `SHA256SUMS`, attesting each in GitHub's attestation store (`gh attestation verify <file> --repo
  rklundt/HeapScope` checks one; attestations need a public repository or GitHub Enterprise). To try it before a
  release: `gh workflow run release.yml -f tag=<tag>` builds without attaching (a dry run; `-f dry-run=false` attaches);
  manual runs need the workflow on the default branch. If a build fails after publishing, rerun the failed jobs: one
  runtime failing does not cancel the others, and attaching replaces files of the same name. Security reports:
  `SECURITY.md`.
- Release notes: `RELEASE-NOTES.md` has a section per version (one per sprint), added by `/close-sprint`. A release
  adds its row to the table at the top, and the GitHub Release's notes are the sections since the previous release.
- The owner (rklundt) is the sole author: commits use the noreply address in the repository's git config; no
  Co-Authored-By or generated-by lines in commits, PR titles or PR bodies. Files write the contact address as
  "rayklundt (at) outlook (dot) com".
- Never commit dumps, index caches (`*.hsidx`), traces (`*.nettrace`, `*.etl`) or logs; `.gitignore` covers them.

## 8. Sprint workflow
1. `/start-sprint [id]`: checks the previous sprint is Closed and the tree is clean, reads the docs, analyzes
   the sprint, asks clarifying questions in one numbered batch with defaults, writes the task breakdown and
   decisions into SPRINTS.md, sets In Progress, and implements after go-ahead.
2. Implement task by task, tests alongside code, build green at every step.
3. `/wrap-sprint`: full build and tests, four reviewer subagents (solution-architect, senior-dev,
   gc-specialist, support-engineer; briefs in `.claude/agents/`) in parallel, consolidated review file in
   `docs/reviews/sprint-X.Y.md`, High findings fixed and confirmed, Moderate findings decided by the human, docs
   updated, then a sweep of every tracked Markdown file against the corrected code (reviewer briefs and skills
   included; past review files, LICENSE, ADDITIONAL-TERMS.md and COMMERCIAL.md left alone), status Wrapping.
4. `/close-sprint`: verifies the Definition of Done, sets Closed and the version, commits on `sprint/X.Y`, opens
   the PR into `develop`, merges it (autonomous mode) and tags `sprint-X.Y` on `develop`, summarizes, offers the next
   sprint.

**Autonomous mode.** When the product owner grants autonomy for a phase, the skills do not stop for
questions: each question is answered with the recommended default and recorded under "Decisions" in the
sprint's section; Moderate findings are decided by the lead engineer and marked "decided autonomously" in the
review file; sprints close and the next one starts without asking. Escalation conditions set by the product
owner still apply.

## 9. Definition of Done (every sprint)
- Solution builds with zero warnings.
- All tests pass, including integration tests relevant to the sprint.
- Acceptance criteria in SPRINTS.md are all met, or explicitly deferred by the human.
- Review file exists in `docs/reviews/`, with all High findings resolved and every Moderate finding decided.
- ARCHITECTURE.md, DEVELOPER.md, SPRINTS.md and CLAUDE.md reflect what was actually built, and the Markdown sweep
  of `/wrap-sprint` found every other tracked Markdown file current.
- No layering violations (architecture test passes).

## 10. Severity rubric
- **High / Urgent**: incorrect analysis results, crashes, data loss, security or privacy issues, layering
  violations, UI freezes, memory or time budgets missed by more than 2x, anything that violates the
  Definition of Done, design choices that will force a rewrite later.
- **Moderate**: maintainability or design concerns with real future cost, missing tests for meaningful edge
  cases, budgets missed by less than 2x, confusing UX, weak error messages.
- **Low / Trivial**: style, naming, minor refactors, nits, optional improvements.

Every finding includes: severity, reviewer, file and line (or area), what is wrong, why it matters, and a
suggested fix.
