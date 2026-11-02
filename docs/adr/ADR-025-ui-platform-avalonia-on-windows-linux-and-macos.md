# ADR-025 UI platform: Avalonia on Windows, Linux and macOS (added in sprint 2.3.5, amends ADR-001; amended by ADR-039)

- Context: the product owner wants to reach Linux developers (possibly macOS) and teams sharing results (2026-10-25).
  Everything but the WPF UI already targets plain `net10.0`; the engine stays .NET because ClrMD has no equivalent
  elsewhere. Two front-end routes were compared: Avalonia (the view models carry over; desktop on all three systems) and
  a TypeScript web front end over the worker (a new UI stack, plus a way to serve it). The product owner chose Avalonia.
- Portability survey (sprint 2.3.5, task 1):
  - Engine, worker and CLI run on Linux: CI builds them on ubuntu-latest, runs the engine's tests, takes a core dump of
    LeakyConsole with `dotnet-dump` and runs `summary`, `instances --retained` and `paths` on it.
  - Changes it needed: the worker's executable has no `.exe` off Windows (`WorkerHostOptions.WorkerExecutableName`);
    the DAC is `libmscordaccore.so` on Linux and `libmscordaccore.dylib` on macOS, matched to the dump by the ELF GNU
    build id or the Mach-O UUID (`ImageIdentity`; ClrMD's own ELF reader is internal), still local paths only
    (ADR-017). A Linux dump can archive the DAC under `libcoreclr.so`'s build id, or list no DAC at all; the DAC is
    then taken from the folder whose `libcoreclr.so` has the dumped runtime's build id; ClrMD read that runtime's version as 0.0, so its version check is skipped for a DAC
    matched by build id, and the folder name gives the version shown; module paths recorded on another system are split on both separators (`RecordedPath`); Linux and macOS
    core files are recognized as full dumps (`SnapshotFileSignature.IsCoreFile`) and have no minidump header, so their
    capture time is the file's write time.
  - Already portable: the pipe (`PipeOptions.CurrentUserOnly` on every system), the worker's below-normal priority, the
    memory status calls (guarded by `OperatingSystem.IsWindows()`; off Windows they read the GC's view of the
    machine's memory, `GC.GetGCMemoryInfo()`, which honors container limits).
  - Windows only for now: capture (process discovery, dump writing, elevation). Off Windows it refuses with
    `PlatformNotSupportedException` naming `dotnet-dump` (`CapturePlatform.EnsureSupported`), which Application reports
    as `CaptureFailedException` with `CaptureFailure.NotSupportedOnThisSystem`; the elevation probe
    reports `Environment.IsPrivilegedProcess`. Linux and macOS capture (createdump through the diagnostics client,
    `/proc` for discovery) is a backlog item.
  - Some tests depend on Windows file semantics (an open or mapped file cannot be deleted or replaced there; it can on
    Linux and macOS) or Windows paths; they skip off Windows.
- Prototype (`spikes/AvaloniaGrid`, outside the solution, Small dump): Avalonia 12.1.4 with the Fluent theme and the
  free DataGrid 12.1.2, showing the types grid and a type's objects through the real `SnapshotSession` and the existing
  view models (linked, not copied).
  - The DataGrid copies its source into its own list and enumerates it to find the item type, which defeats
    data-virtualized row lists. An `IDataGridCollectionView` that passes through to the row list, typed as
    `IEnumerable<TRow>`, avoids both: zero enumerations, private memory 540 MB down to 197 MB.
  - With it: scrolling stalls at most 36 ms; the first showing of a grid 108 to 162 ms (WPF's smoke run: 140 ms).
  - Sorting and the filter work through the view models unchanged; the WPF-only part was the render yield, now
    `TaskSchedulerExtensions.RenderYield`, set by the UI.
- A dump is read by a worker of the system and architecture that took it (review): ClrMD loads the DAC into the
  worker's process, and HeapScope does not search cross-system DACs. So HeapScope ships a worker per runtime identifier
  (`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`); opening a dump of another system or architecture fails with a
  message that says where to open it. Linux arm64 (for example Docker on Apple silicon) is a backlog item.
- Decision: the UI moves to Avalonia (MIT), on Windows x64, Linux x64 and macOS (arm64 and x64), in a port sprint before
  2.4 (product owner: "port before 2.4"). The WPF UI is retired when the port reaches parity; the view models move to a
  platform-neutral UI library. Only MIT (or otherwise allowed) Avalonia components are used: TreeDataGrid moved to the
  paid "Accelerate" line, so trees (2.4's dominator tree) are built on the free controls.
- Update rule (product owner, 2026-10-25; CLAUDE.md): before any Avalonia update, check that no component HeapScope
  uses moved to a paid license; if one did, stop and ask before updating.
- Consequences:
  - ADR-001's "no macOS or Linux UI" no longer holds; .NET 10 stays, and the worker, CLI and engine run everywhere.
  - ADR-019 (the UI process is never power-throttled) and the UI.Wpf-specific parts of DEVELOPER.md change with the
    port; the smoke and UI tests move to Avalonia's headless test platform.
  - macOS was tested on an arm64 runner built `AnyCPU`: the CLI read a macOS core dump of LeakyConsole there (summary,
    retained sizes, retention paths with the expected static field root). The job stays informational until macOS ships;
    packaging and signing for Linux and macOS are backlog items.
  - Every 2.x UI feature after the port is built once, in Avalonia.
- Done in sprint 2.3.6: the WPF app and the prototype are retired; `HeapScope.UI` (Avalonia) and
  `HeapScope.UI.ViewModels` replace them, with every page and command (the parity list is in
  `docs/reviews/sprint-2.3.6.md`). Bindings are compiled and checked at build time; the UI tests build every view on the
  headless platform and fail on any binding error. The checked smoke numbers equal or beat WPF's (section 8); Linux CI
  opens a core dump of the fixture in the app without a display. Builds per system: `build/publish.sh <rid>` (the app and
  the CLI, self-contained, each with a self-contained worker), produced by CI for `win-x64`, `linux-x64`, `osx-arm64` and
  `osx-x64`. Assemblies are AnyCPU (until 2.3.5 x64, ADR-001): each process runs as its machine's 64-bit architecture.
  Headless smoke runs in CI check that every step works and log the UI lags without gating them (a two-core runner
  drawing in software measures the machine); the Windows smoke gate is unchanged. Avalonia's build telemetry is off for
  the repository (`Directory.Build.targets`, CI's `AVALONIA_TELEMETRY_OPTOUT`), since HeapScope sends nothing anywhere
  (12).
- Amended by ADR-039 (sprint 3.9): releases ship win-x64 and linux-x64 only (macOS builds are built but not shipped),
  and the CLI's dotnet tool carries a framework-dependent worker started through the dotnet host, beside the
  self-contained worker per runtime identifier.
