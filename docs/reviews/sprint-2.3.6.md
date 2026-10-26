# Review: Sprint 2.3.6 Avalonia port

Reviewers: solution-architect, senior-dev, gc-specialist (in parallel, on the change set since `sprint-2.3.5`). Phase 2
runs autonomously, one sprint at a time, so Moderate findings were decided by the lead engineer, each with its
recommendation, and are marked "decided autonomously". The reviewers had no shell, so the WPF side of the parity check
was done by the lead engineer from the `sprint-2.3.5` tag (below).

Totals after dedupe: 1 High, 15 Moderate, 30 Low. A finding raised by more than one reviewer is merged and keeps the
highest severity.

The reviewers confirmed:
- layering: `HeapScope.UI.ViewModels` references only Application, Contracts and CommunityToolkit.Mvvm and no UI
  toolkit; `HeapScope.UI` matches ARCHITECTURE 3.1 and `LayerRules` (solution-architect);
- no blocking calls (`.Wait`, `.Result`, `Invoke`) in the UI or the view models (solution-architect);
- protocol version 10 adds one optional trailing field and the bump is right (solution-architect);
- the power throttling opt-out runs on Windows only (ADR-019); the attribution keeps its text, link, place, surface,
  selectability and font size (ADR-022) (solution-architect);
- CI sends no telemetry and never uploads a fixture dump (solution-architect);
- the grid behaviors hold no instance state, `HighlightedText` builds inlines only for matching cells, the chart builds
  one geometry per layer per change, `VirtualRowList.IndexOf` and `Contains` are constant time, and the single
  selection mode keeps select all and copy away from enumeration (gc-specialist, senior-dev);
- retained group ids use one comparer and key; `CaptureTimeFromFile` travels through the cache metadata and the cache
  fingerprint; storage layouts and known-answer sizes are the same on x64 and arm64; Unix modes go through `mkdir` and
  `open` with the umask, never looser than 0700 and 0600 (gc-specialist);
- the headless lag mapping and the first-window gate match decisions 8 and 11 (senior-dev).

## High / Urgent

- **H1 The builds per system did not run as downloaded** (solution-architect; gc-specialist and senior-dev as
  Moderate). `actions/upload-artifact` drops Unix file modes, so the Linux and macOS builds arrived with `HeapScope`,
  `heapscope-cli` and `worker/HeapScope.Worker` not executable, and the first dump open would fail to start the worker.
  The package job never ran anything it published, and it did not wait for the test jobs, so a red commit could upload
  builds. **Fixed:** the package job needs the Windows and Unix jobs; each build is packed as a tarball (which keeps the
  modes), unpacked into a separate folder and run as a user would: `heapscope-cli --version`, `heapscope-cli worker
  ping` (starts the self-contained worker through the pipe), the worker's `"System.GC.Server": true`, and the app
  headless with `--smoke-synthetic 200000`; only the tarball is uploaded (`osx-x64` is built on the arm64 runner and
  not run). `build/publish.sh` checks its arguments and the runtime identifier (senior-dev Low).

### Confirmations

- H1, first pass: solution-architect confirmed the Unix side (the tarball keeps the modes, the ping really starts the
  worker from `worker/`, the Server GC check, the headless smoke, `needs`) and found a new High: on the Windows runner
  Git Bash's GNU tar reads `D:` in `$RUNNER_TEMP` as a remote host, so the win-x64 leg would fail both the pack and the
  unpack. Fixed: both tar calls use relative names from inside the folder (portable to macOS bsdtar, unlike
  `--force-local`). Its Lows, also fixed: the worker's explicit restore passes `PublishReadyToRun`; DEVELOPER 2 says the
  download is a zip holding the tarball and how to unpack it.
- H1, second pass: CONFIRMED by solution-architect (relative names on every runner, failures still stop the step, the
  upload path matches, the unpacked tree is what runs; both Lows done; nothing new introduced).
- The first CI run of the fixes (2acd914) also caught the architecture test refusing the font exclusion reference
  (`LayerRules` allows only listed packages); `Avalonia.Fonts.Inter` is now listed for the app, with a note that it is
  referenced only to exclude its assets.
- CI on f2d0d1c: every job green, the four package legs included; three ran their unpacked build.

## Moderate (decided autonomously)

- **M1 A full disk crashed the worker on Linux and macOS** (gc-specialist, borderline High). `MappedIndexFileWriter`
  sized its temp file with `SetLength` (a sparse file on Unix) and wrote through a mapping, so running out of space
  raised SIGBUS instead of an `IOException`. Fix now: `PrivateFiles.Open` takes a preallocation size
  (`FileStreamOptions.PreallocationSize`, `posix_fallocate` on Unix), used for the whole mapped length. Test:
  `Space_reserved_up_front_fails_at_open_when_the_disk_cannot_hold_it`. Fixed.
- **M2 The longest UI operation was a lower bound, not the upper bound the docs claimed** (gc-specialist,
  solution-architect). Avalonia's dispatcher has no operation hooks (checked in its API), so the highest-priority probe
  only sees what is left of an operation when it is posted; a 120 ms operation could pass the 100 ms gate. Fix now: an
  operation starts after the previous highest-priority probe ran, so the time from that run to the next probe's run is
  an upper bound (`MaxOperationBound`); smoke runs probe every 10 ms from a thread of their own (a busy thread pool had
  widened the gaps) and check the bound. A first version (the lower bound plus the largest tick gap) was sound but
  failed Medium at 136 ms with no long operation, so it was replaced. Re-measured (Release): Small at most 64 ms, Medium
  at most 69 ms (WPF timed exactly 44 to 68 ms); ARCHITECTURE 8 says the two were measured differently. Decision 12.
  Fixed.
- **M3 Whole-list walks were counted but nothing on real pages checked them** (gc-specialist; the task 4 wording also
  raised by solution-architect). `SourceCollection` returned the raw row list, so a consumer walking it was not counted;
  only one unit test with a bare grid checked the counter; the app's UI memory was never re-measured. Fix now:
  `SourceCollection` returns the view itself; every smoke run logs the UI's private and managed memory and the count of
  whole-list walks, and fails with exit code 6 when one happened; task 4 reworded. Measured: no walk on the Small and
  Medium smoke runs, 219 to 229 MB private (ARCHITECTURE 8). Fixed.
- **M4 Three module keys** (gc-specialist). The 2.3.5 L2 cleanup made the summary compare module file names in any case,
  but the diff still compared them exactly, so an assembly recorded as `MyApp.dll` in one dump and `myapp.dll` in the
  other showed every type as gone and new. Fix now: one `TypeKeyComparer` (name exact, module in any case; both in any
  case for assembly rows) used by the diff, incoming references and the graph inspections. Test:
  `A_module_recorded_in_another_case_is_the_same_module`. Fixed.
- **M5 A self-contained worker trusted a folder outside HeapScope as an installed runtime** (gc-specialist). The last
  installed-runtime root was three folders up from `System.Private.CoreLib.dll`; for the self-contained worker that is a
  folder next to the install (for example `~/Downloads/shared`), which skipped the DAC pairing check, while the worker's
  own DAC was never searched. Fix now: `DacLocator.RunningSharedFolder` counts only a real
  `shared/Microsoft.NETCore.App/<version>` folder; a self-contained process searches its own folder last, as an ordinary
  candidate that still needs the version pairing (`ClrMdLoaderOptions.OwnRuntimeDirectory`). Tests: the candidate order
  with the own folder, a network own folder refused, the test host's shared folder. Fixed.
- **M6 A dump of another architecture got the wrong advice** (gc-specialist). With AnyCPU builds, a build from source on
  an arm64 machine runs the worker as arm64; when a matching x64 DAC was found, loading it failed with the generic
  "wrong runtime version" message instead of "open it with the x64 build". Fix now: `DacLocator.Mismatch` runs at the
  start of `ClrMdDump.CreateRuntime`, before the DAC search; DEVELOPER 2 explains the dev-build consequence. Tests: six
  cases of `A_dump_of_another_architecture_or_system_is_refused_with_what_to_do`. Fixed.
- **M7 ARCHITECTURE 9 said DATAS stayed on in the worker** (gc-specialist). Checked: the worker now logs the GC's own
  settings at start, which show `GC heaps 12, GC dynamic adaptation 0` (24 cores): setting the heap count turns DATAS
  off. Fix now: the doc is corrected and the fixed count kept, since it is what keeps the UI responsive during blocking
  collections (sprint 1.3). Fixed.
- **M8 A failing host dispose left the app running without a window** (senior-dev). In `OnMainWindowClosing` the
  dispose ran in `finally` unguarded, so an exception skipped `Close` and `Shutdown` (the close was already canceled and
  the app shuts down only explicitly). Fix now: the dispose is guarded and logged; `Close` and `Shutdown` always run. The
  watchdog's dispose is idempotent (it is disposed by the app and by the host). Fixed.
- **M9 The hand-made attribution link and message window had no tests** (senior-dev). Also, the link hit test used the
  caret position, so the right half of the space before the link counted and the right half of its last letter did not.
  Fix now: the hit test uses the character under the pointer; the link opens through a launcher seam; headless tests
  click the link, plain text and both edge halves, drag a selection across the link, press Enter, and answer the message
  window with Yes, No, OK, close and Escape, with and without an owner (`InteractionTests`). Fixed.
- **M10 A grid could silently lose the render yield** (senior-dev; solution-architect as Low). `UiDispatcher` was a
  settable property defaulting to the immediate dispatcher, copied by hand in four places. Fix now: `IUiDispatcher` is a
  required constructor parameter of every grid view model; tests pass `ImmediateUiDispatcher.Instance`. Fixed.
- **M11 The shipped builds were not all ReadyToRun** (solution-architect, senior-dev; gc-specialist as Low). The CLI
  publish ran second into the same folder without ReadyToRun and replaced the app's precompiled shared assemblies and
  worker with IL-only copies. Fix now: `build/publish.sh` passes `-p:PublishReadyToRun=true` to both publishes (and so
  to both worker publishes). The worker is still published twice into `worker/` (same output); one shared runtime folder
  is a later option. Fixed.
- **M12 The WPF to Avalonia parity list did not exist** (solution-architect). ADR-025 and task 5 point to it. Fix now:
  the table below, built from the `sprint-2.3.5` tag. Fixed.
- **M13 The grid adapter is chosen by concrete type and assumes a fixed row count** (solution-architect). 2.4's
  flattened dominator tree will need a list whose count changes. Defer: added to the 2.4 scope (an adapter keyed by an
  interface, forwarding changes). Deferred.
- **M14 Avalonia's TreeView is not virtualized** (solution-architect). ARCHITECTURE 8 said the field tree was
  virtualized (true of WPF); the 2.4 plan allowed a TreeView. Fix now: section 8 states the caps the current trees rely
  on (100 array elements per level, field text 1,000 characters, 2,000 retention nodes); the 2.4 plan requires a
  flattened, paged grid for unbounded trees. Fixed.
- **M15 The app shipped a font under the SIL Open Font License** (solution-architect). `Avalonia.Headless` brings
  `Avalonia.Fonts.Inter`, which is not on the allowed license list. Fix now: the app references it with
  `ExcludeAssets="all"`, so it reaches only the tests; the app uses the system font, and the headless smoke runs work
  without it (checked). DEVELOPER 4, THIRD-PARTY-NOTICES and decision 13 record it. Fixed.

## Parity: WPF views and commands against the Avalonia app (M12)

Every `{Binding}` of every WPF view at `sprint-2.3.5` was compared by script with the Avalonia views; each one has an
equivalent (four took another form, noted). Gestures and helpers from the code-behind were compared by hand.

| WPF (`src/HeapScope.UI.Wpf`, sprint-2.3.5) | Avalonia (`src/HeapScope.UI`) |
|---|---|
| `MainWindow`: navigation, worker status, jobs panel, crash banner (`RestartWorkerCommand`, `DismissCrashBannerCommand`) | `Views/MainWindow.axaml`, same bindings |
| `MainWindow` attribution `Hyperlink` (`RequestNavigate`) | `Controls/AttributionText` (link run by hit test, Enter, launcher), same text and link (ADR-022) |
| `MainWindow`: mouse back and forward buttons, Alt+Left and Alt+Right | `MainWindow.axaml.cs`: XButton1 and XButton2, Alt+arrows |
| `HomePage`: synthetic job, open dump, recent sessions (`OpenSessionCommand` per row), refresh | `HomePage.axaml`, same; the row command binds through `$parent[DataGrid]` |
| `ProcessesPage`: processes, capture, gcdump option, elevation hint, refresh | `ProcessesPage.axaml`, same, plus the reason capture is unavailable off Windows |
| `SnapshotsPage`: snapshot tabs, open dump, close | `SnapshotsPage.axaml`, same |
| `SnapshotTab`: types (group by, filter, generations, retained), instances, object (fields, references, retention, why alive), type referrers and paths, trail with back and forward | `SnapshotTab.axaml`, same; column visibility and the filter highlight use `GridBehaviors` and `HighlightedText.Filter` instead of the WPF binding proxy (`Data.Filter`, `Data.RetainedColumnShown`, `Data.GenerationColumnsShown`) |
| `CompareTab`: baseline and current, swap, group by, filter, totals, rows, sort, open row | `CompareTab.axaml`, same, plus the file-time baseline hint (added item 5) |
| `InspectionsTab`: tiles, findings, fragments, result rows, open row; unavailable inspections dimmed (`IsAvailable` trigger) | `InspectionsTab.axaml`, same; dimming through the `unavailable` class |
| `LivePage`: process, rules, start, stop, chart, markers, snapshots | `LivePage.axaml` and `Controls/TimelineChart`, same |
| `JobsPage`: jobs, clear finished; `Shared.xaml` job row (progress, cancel) | `JobsPage.axaml` and `JobRow`, same |
| `AboutPage`: version, worker details, refresh | `AboutPage.axaml`, same |
| `GridHelpers`: sort command and state, double-click and Enter open, highlighted text, proportional bar, result columns | `Controls/GridBehaviors`, `HighlightedText`, `ProportionalPanel` |
| `ThemeManager`, `Themes/Light.xaml`, `Dark.xaml` | `Services/ThemeChooser`, `Themes/Light.axaml`, `Dark.axaml` (same keys; `ThemeTests`) |
| `MessageBox` confirmations | `Views/MessageWindow` (asynchronous, `InteractionTests`) |
| `DispatcherWatchdog` (operation hooks), `SmokeRunner`, `NativeMethods`, `UiHost` | `Services/DispatcherWatchdog` (probes, M2), `Smoke/SmokeRunner`, `Services/NativeMethods`, `UiHost` |

## Low / Trivial (listed; fixed where noted)

- L1 `VirtualRowList` keeps rows created while no page is tracked (no rows yet, after `Cancel`, a failed page): after a
  worker crash, scrolling keeps every row it touched (gc-specialist).
- L2 `build/check-known-answers.sh` expects the static cache's retained size to be exactly 148; the known answers give
  a minimum (gc-specialist).
- L3 `WorkingSetCap.Counters` calls `kernel32` unconditionally, so the dominators benchmark fails off Windows now that
  benchmarks are AnyCPU (gc-specialist).
- L4 The watchdog posts two probes every 25 ms even when idle (battery, App Nap); cache the delegates and slow down when
  idle (gc-specialist; the Backlog already has the idle item).
- L5 Files and folders created by 2.3.5 builds on Linux and macOS keep their old modes; `CreateDirectory` also gives 0700
  to every missing parent, possibly `~/.local/share`; Storage has no owner-only test; a file-system helper in Core
  stretches its purpose (gc-specialist, solution-architect).
- L6 An orphaned `<summary>` in `TypeGroups` (fixed); `Version` still 2.3.5 (set by /close-sprint) (gc-specialist).
- L7 ADR-022 and the markup say the attribution contrast is above 13:1 in both themes; dark is 12.96:1. The test
  checks only 4.5:1 and not the strip's dock position and order (solution-architect).
- L8 The architecture test does not govern `Avalonia*` or WindowsDesktop references and skips missing known assemblies
  silently (solution-architect).
- L9 No test of `AvaloniaUiDispatcher.YieldToRenderAsync` or of a grid binding its rows after a render (2.3.5 L15 still
  open) (solution-architect).
- L10 Off Windows the Processes page still shows an empty grid and disabled capture controls under the reason; the open
  hints do not mention Linux and macOS core files (solution-architect).
- L11 The view model library declares `HeapScope.UI.Services`, which the app also uses; `ImmediateUiDispatcher` and
  `AutomaticUserInteraction` share files with their interfaces; the library grants internals to the app
  (solution-architect, senior-dev). `MessageButtons` now has its own file (fixed).
- L12 `Worker.Deploy.targets` copies nested runtime folders left by an earlier publish into `worker/` locally
  (solution-architect).
- L13 Start failures: a bad `--smoke-synthetic` or `--smoke-live` value exits 4 without a log line; a non-smoke failure
  after the host started shuts down without stopping it (senior-dev).
- L14 Fire and forget: the UI error window can stack one per exception; the link launch is now observed and the hand
  cursor cached (fixed) (senior-dev).
- L15 `!` suppressions without the reason DEVELOPER 4 asks for in `ProcessesViewModel`, `CompareViewModel` and
  `PrivateFiles`; the ones in `VirtualCollectionView` now say why (fixed) (senior-dev).
- L16 The trail breadcrumb's `ReflectionBinding` has no comment saying why (senior-dev).
- L17 `TimelineLayout.XOf` uses the raw span while `Layers` floors it at one second; two samples at one instant give
  NaN (senior-dev).
- L18 Theme brushes looked up by key are not refreshed when the system theme changes while the app runs (senior-dev).
- L19 Compare's file-time hint says "the snapshot opened first" also with three or more snapshots and after a manual
  swap; no three-snapshot test (senior-dev).
- L20 `SmokeRunner` had a misplaced summary and stray blank lines (fixed); `CompareViewModel` has double blank lines
  (senior-dev).
- L21 UI test hygiene: `UiSession.Dispose` uses `.Wait`; `GridTests` closes windows only on success; the binding error
  sink and the enumeration counter are shared by test classes that run in parallel (senior-dev).
- L22 The 103-character socket bound also applies on Linux (107 fits), counts UTF-16 characters rather than bytes, and
  has no boundary test (senior-dev).
- L23 The watchdog's first over-budget warning reports the UI thread's whole CPU time (senior-dev).
- L24 `build/publish.sh` failed with "unbound variable" on missing arguments and did not check the runtime identifier
  (fixed with H1) (senior-dev).
- L25 to L30: merged into the findings above (the package job's `needs` into H1; the CLI's ReadyToRun into M11; the
  grid dispatcher into M10; the docs drift into M3, M14 and the docs pass: ARCHITECTURE's "x64" processes, hidden
  panels, DEVELOPER's prerequisites and CI section, SPRINTS' `ICaptureService.Availability`) (solution-architect,
  gc-specialist).
