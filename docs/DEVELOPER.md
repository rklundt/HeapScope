# HeapScope Developer Guide

Status: living document, updated by every sprint.

## 1. Prerequisites
- Windows 11 x64 (Windows 10 x64 also supported).
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
- Full gate (sprint 1.9; CI since 2.3.5 runs a subset, section 7): `./build/gate.ps1` builds Debug
  and Release (zero warnings), runs the full suite and appends a line to `artifacts/gate/history.csv` (git-ignored):
  time, commit, outcome, durations, test counts and failed test names; a test that failed in an earlier run is named
  again. Run it before closing a sprint.
- Fast tests only (exit code 8 means "no tests in this assembly", e.g. Integration.Tests): `dotnet test --solution HeapScope.sln --filter-not-trait "Category=Integration" --filter-not-trait "Category=Slow" --ignore-exit-code 8`.
- A test run that never finishes: run the test executable directly with hung test detection, for example
  `tests/HeapScope.UI.Wpf.Tests/bin/Debug/net10.0-windows/HeapScope.UI.Wpf.Tests.exe -longRunning 30 -noAutoReporters`;
  it names the test still running every 30 seconds (sprint 1.5: a grid test waited forever on a page gate).
- Run the UI: `dotnet run --project src/HeapScope.UI.Wpf`.
- The Avalonia prototype (sprint 2.3.5, ADR-025; outside `HeapScope.sln`, not shipped, removed by the port sprint 2.3.6):
  `dotnet run --project spikes/AvaloniaGrid -- <dump>` shows the types grid and a type's objects through the real worker;
  `--measure` scrolls the grids, prints first-show and stall times and the row lists' enumeration count, and exits.
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
  sprint 1.9).
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
  `inspect <dump> [<name>] [--top N | --all] [--format table|csv|json]` runs the simple inspections (sprint 1.6):
  without a name, every inspection with its time, finding and note (one line each); with a name
  (`duplicate-strings`, `empty-collections`, `oversized-collections`, `sparse-arrays`, `large-arrays`, `loh`,
  `finalizable`, `fragmentation`), its cause, fix and rows. An unknown or unavailable inspection exits 1. A dump opened
  from an index cache written before 1.6 has no free space or reference-array marks: reopen it with `open --rebuild`.
  `monitor --pid <id> [--interval 1] [--duration N] [--every 5m] [--above 500MB] [--growth 100MB|50%]
  [--rearm once|below|step] [--max N] [--format table|csv] [--kind full|gcdump]` watches a process's GC heap (the heap now and after the
  last GC, per generation, LOH, POH), collections, allocations and the last GC's share of time every interval (sprint
  1.7) and captures full dumps into the current session when a rule fires on the heap after the last GC (labeled with
  the rule, recorded as timed; `below` re-arms under 90 percent of the threshold); it prints a line per sample, a line per snapshot and,
  at the end, the timeline file (`counters-<pid>-<time>.csv` in the session folder). Ctrl+C stops; a process that
  cannot be monitored exits 1.
- DAC lookup (ADR-017): local only. A dump from a runtime that is not installed needs its `mscordaccore.dll` next
  to the dump or in a DAC folder: CLI `--dac-folder <folder>`, UI `--Symbols:DacFolder <folder>` or the
  `Symbols:DacFolder` setting. `--allow-symbol-server` (CLI) or `Symbols:AllowMicrosoftSymbolServer true` opts in to
  downloads and logs a warning. Both reach the worker on its command line only.
- Worker GC heaps: half the logical processors by default, so the worker cannot starve the UI; override with
  `--Worker:GcHeapCount <n>` (UI), the `Worker__GcHeapCount` environment variable, or `DOTNET_GCHeapCount` (hexadecimal).
- Run the worker standalone (for debugging): `dotnet run --project src/HeapScope.Worker -- --pipe <name>`
  (without `--parent-pid` it does not watch a parent; add `--enable-test-hooks` for the crash and hang hooks).
  Attach a client with `dotnet run --project src/HeapScope.Cli -- worker ping --pipe <name>`, or set
  `Worker:AttachPipeName` for the UI.
- Live smoke run: `HeapScope.exe --smoke-live 1800` charts 1,800 made-up samples on the Live page as fast as it can
  (no process, no timeline file) and exits with the same codes as the other smoke runs.
- UI smoke run: `HeapScope.exe --smoke-synthetic 5000000` runs a synthetic job, checks dispatcher lag and exits
  (0 ok, 3 lag over the gate, 4 job failed); details in ARCHITECTURE.md 8. `HeapScope.exe --smoke-open <dump>`
  opens a dump through the Snapshots page instead, with the same checks; add `--smoke-compare <dump>` to also open a
  second dump and check the Compare tab (its cold first show is logged, a warm re-query is checked). `--smoke-open`
  also drills down (types, instances, object, the object's incoming references and retention paths, the type's
  referrers and key paths; sprints 2.1 and 2.2),
  shows the Inspections tab ("Smoke inspections": every inspection's
  finding and time) and logs the open phase on its own line ("Smoke open phase"); add
  `--smoke-trace <file.nettrace>` to record a CPU trace of the UI during the run. The log records
  "First window rendered N ms after process start" for every run.
- Traces: `heapscope-cli trace --pid <id> [--duration 30] [--profile cpu|gc] [--out file.nettrace]` records an
  EventPipe trace of any .NET process (no dotnet-trace needed; Ctrl+C stops early and keeps it). Read it with PerfView,
  Visual Studio, or `dotnet run -c Release --project perf/HeapScope.Benchmarks -- trace-report --trace <file>`
  (`--timeline` for busy UI time per window, `--from HH:mm:ss.fff --to ...` for one stretch, `--focus <frame text>` for
  the callers of a frame). Traced runs are slower: use them to find where time goes, not to check budgets.
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
  values, a "High GC pressure" badge (10 percent or more of the last ten intervals in GC) and a card per snapshot.
- Inspections (sprint 1.6): the Snapshots page's Inspections tab runs the selected snapshot's inspections the first
  time it is shown (one at a time, in the worker): a tile per inspection with its finding, cause and fix (or why it is
  unavailable), fragmentation bars per heap, and the selected inspection's rows; double-click or Enter opens a row's
  objects (a type in its scope, equal strings, or one object) on the Snapshot tab.
- Theme: the UI follows the Windows app theme (light or dark) at startup; `--Ui:Theme Light|Dark` or the
  `Ui:Theme` setting overrides it. Colors live in `src/HeapScope.UI.Wpf/Themes/Light.xaml` and `Dark.xaml`, which
  must define the same keys (a test checks it); views use `DynamicResource`, never literal colors.
- Precompiled UI: `dotnet publish src/HeapScope.UI.Wpf -c Release -r win-x64 --self-contained false -o <folder>`
  (ReadyToRun is on for publish; the worker is published into `worker/` next to it).
- Sessions: captures go to `%LOCALAPPDATA%\HeapScope\sessions\<yyyyMMdd-HHmmss>` with a `session.json`; override the
  root with `--Sessions:Root <dir>` (UI) or the `Sessions__Root` environment variable. Integration tests point every
  front end they start at a temporary root.
- Ctrl+C in tests: `LoadGen --launch <exe> [args]` runs a program with Ctrl+C enabled (children of the test host
  inherit "ignore Ctrl+C"), and `LoadGen --ctrl-c <pid>` sends Ctrl+C to that program's console.
- A canceled capture waits for the runtime to finish writing and then deletes the dump: the CLI waits up to an hour
  after Ctrl+C, closing the UI waits up to 5 minutes; anything left (`*.dmp.partial`, `*.gcdump.partial`) is swept at the next start.
- Logs: `%LOCALAPPDATA%\HeapScope\logs\heapscope-yyyyMMdd.log` (UI and CLI, including forwarded worker lines);
  override with `--Logging:File:Directory <dir>` (UI) or the `Logging__File__Directory` environment variable.
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
  --Leaks:Enabled=all --Leaks:ItemsPerRequest=100 --Leaks:PayloadBytes=64 [--Leaks:Publishers=N] [--ParentPid=P]`.
  Prints `READY pid=<pid> url=<url>` when listening (use port 0 for a free port). `POST /work` grows every enabled
  leak by `ItemsPerRequest` items; `GET /leaks` shows counts (0 for disabled leaks, which are never touched);
  `POST /leaks/{name}/grow?count=N` grows one enabled leak (404 unknown, 409 disabled, 400 negative); `GET /health`.
- `fixtures/LeakyConsole`: `dotnet run --project fixtures/LeakyConsole -- --leak all|none|Name1,Name2 --count N
  --payload BYTES [--publishers N] [--semantics N] [--runtime-semantics N] [--late-garbage N] [--inspection] [--waste]
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
  private static fields, `I` prefix for interfaces, `Async` suffix on async methods. One public type per file.
- **Nullable** is enabled everywhere; no `!` suppressions without a comment explaining why.
- **Warnings are errors**; do not disable a warning without a comment and reviewer agreement.
- **Async**: every async method takes a `CancellationToken` last (after `IProgress<T>` when present). Library
  code uses `ConfigureAwait(false)`, enforced by CA2007 for every `src/` project except UI.Wpf and Cli (see
  `.editorconfig`). Never `.Result`, `.Wait()` or `async void` (except WPF event handlers,
  which must catch everything).
- **MVVM**: CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). No code-behind
  logic beyond view-only concerns. View models get services by constructor injection and never reference WPF
  controls. Long work uses async relay commands with cancellation.
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
  anything else (GPLv2 only, proprietary, SSPL, non-commercial, unlicensed) needs the product owner first. Update
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
- `dominators --cache <snapshot.hsidx> [--mode memory|mapped] [--out <folder>] [--max-working-set-mb N]` (sprint 2.3; the
  cap, a hard limit on the process's working set to see the computation under memory pressure, after the 2.3 review
  M14; it also reports page faults and the peak working set): the worker's path on a
  real index cache: the reverse references and the dominators, computed straight into sidecars (in `--out`, or a temporary
  folder; only the two sidecars are deleted afterwards, and the folder only when the run made it) and mapped, or in managed arrays; the retained lower bounds per type; a reopen of the dominators sidecar.
  Times and peak private bytes per phase; exit 1 when the indices exceed 60 s or the run's peak 4 GB.
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
- CI (`.github/workflows/ci.yml`, sprint 2.3.5) runs on every PR into `develop` or `main` and every push to them, on
  GitHub's hosted runners: Windows builds the solution and runs the fast tests; Linux builds the engine, the worker and
  the CLI, runs the engine's tests, dumps the LeakyConsole fixture with `dotnet-dump` and analyzes it with the CLI;
  macOS does the same, informational (an arm64 runner; ADR-025). It does not replace the local gate, which also runs the
  integration tests. Fixture dumps are never uploaded.
- A sprint's PR into `develop` is squash merged; the annotated tag `sprint-X.Y` goes on the resulting commit on
  `develop` and is pushed. In autonomous mode Claude merges sprint PRs once the gate passes; otherwise the owner
  merges.
- Versions follow the sprint number: `/close-sprint` sets `<Version>X.Y.0</Version>` in `Directory.Build.props`
  (`X.Y.Z` for an in-between sprint such as 2.3.5).
- Releases: when the owner says so, a PR from `develop` to `main`, merged with a merge commit (never squash, so the
  branches do not drift), then the annotated tag `vX.Y.0` on `main` and a GitHub Release (`gh release create`, marked
  pre-release for now). Only the owner decides when to release.
- The owner (rklundt) is the sole author: commits use the noreply address in the repository's git config; no
  Co-Authored-By or generated-by lines in commits, PR titles or PR bodies. Files write the contact address as
  "rayklundt (at) outlook (dot) com".
- Never commit dumps, index caches (`*.hsidx`), traces (`*.nettrace`, `*.etl`) or logs; `.gitignore` covers them.

## 8. Sprint workflow
1. `/start-sprint [id]`: checks the previous sprint is Closed and the tree is clean, reads the docs, analyzes
   the sprint, asks clarifying questions in one numbered batch with defaults, writes the task breakdown and
   decisions into SPRINTS.md, sets In Progress, and implements after go-ahead.
2. Implement task by task, tests alongside code, build green at every step.
3. `/wrap-sprint`: full build and tests, three reviewer subagents (solution-architect, senior-dev,
   gc-specialist) in parallel, consolidated review file in `docs/reviews/sprint-X.Y.md`, High findings fixed
   and confirmed, Moderate findings decided by the human, docs updated, status Wrapping.
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
- ARCHITECTURE.md, DEVELOPER.md, SPRINTS.md and CLAUDE.md reflect what was actually built.
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
