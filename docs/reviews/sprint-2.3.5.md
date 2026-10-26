# Review: Sprint 2.3.5 Cross-platform and sharing decision

Reviewers: solution-architect, senior-dev, gc-specialist (in parallel, on the change set since the fix PR, commit
32a616a). Phase 2 runs autonomously, one sprint at a time, so Moderate findings were decided by the lead engineer, each
with its recommendation, and are marked "decided autonomously".

Totals after dedupe: 2 High, 12 Moderate, 17 Low. A finding raised by more than one reviewer is merged and keeps the
highest severity.

The reviewers confirmed:
- the ELF build id and Mach-O UUID readers are bounds safe and fail closed (gc-specialist, senior-dev);
- skipping ClrMD's version check is safe for a DAC matched by its own build id (gc-specialist);
- the loader's root, handle, static and frozen semantics are the same code on every system (gc-specialist);
- layering: no new references, `RecordedPath` in Core, nothing leaks transitively; the spike stays outside the solution
  (solution-architect);
- the pipe name change touches no wire contract: the worker accepts any `--pipe`, `CurrentUserOnly` is kept, 96 random
  bits (solution-architect);
- ADR-025 is consistent with ADR-019 and the ADR-001 amendment (solution-architect); the RenderYield hook is set on the
  UI thread before the host starts and is sound (senior-dev).

## High / Urgent

- **H1 The local path rule did nothing on Linux and macOS** (solution-architect, gc-specialist; senior-dev as Moderate).
  `DacLocator.IsLocalFixedPath` read the drive type of `Path.GetPathRoot`, which is `/` for every absolute path there,
  so NFS, SMB, sshfs and autofs mounts, `/proc` and `/dev` all passed. A dump on a network share could have its DAC
  loaded from there (ADR-017, 12), a recorded path could trigger an automount, and a FIFO could block the worker.
  **Fixed:** `UnixLocalPath` walks the path one component at a time, resolves links, and requires every step to be on a
  local mount (fixed, overlay included, or tmpfs; by `DriveInfo` of the longest matching mount point), checking the mount
  before looking at anything on it; `DacLocator.IsReadableImage` also requires a file of at least a header's size (pipes,
  devices and `/proc` files report 0), used by the locator and `LocalOnlyFileLocator`. Tests: `UnixLocalPathTests` (an
  injected mount table and links, run everywhere; this system's temp folder on Linux and macOS). ADR-017 and section 12
  amended.
- **H2 Linux and macOS core files had no truncation check** (gc-specialist). A core cut short by `ulimit -c`,
  systemd-coredump or a copy keeps the GC heap but can lose thread stacks, so roots go missing silently while the load
  claims CompleteGraph and ExactCounts (the 1.8 rule). **Fixed:** `CoreFileHeader.IsTruncated` reads the ELF program
  headers (with PN_XNUM) or the Mach-O load commands and flags any table, PT_LOAD, PT_NOTE or LC_SEGMENT_64 range past
  the end of the file; the loader uses it for every core as it uses the minidump check; loader version 3 rebuilds older
  caches. Tests: `CoreFileHeaderTests`.

### Confirmations

The fixes were checked against each reviewer's suggested fix and by CI on Linux and macOS (the temp folder test, the
fixture's core read as complete, the paired DAC found with the version strings); see the Review summary in SPRINTS.md
for the run.

## Moderate (decided autonomously)

- **M1 A DAC paired through its runtime library was never checked itself** (all three). With the DAC archived under
  `libcoreclr.so`'s build id, only the runtime library was compared, and a folder filled by hand could pair it with
  another build's DAC while ClrMD's version check was off. Fix now: the pair is accepted from an installed runtime
  folder, elsewhere only when both files embed the same `@(#)Version` string (`ImageIdentity.ReadVersionString`); an
  entry with the DAC's own build id is preferred; `ignoreMismatch` follows how the DAC was matched. Fixed.
- **M2 SnapshotInfo kept the runtime version 0.0** (all three). Fix now: the loader names it as the DAC request does
  (`DacLocator.RuntimeVersion`), which also accepts preview folder names; loader version 3. Fixed.
- **M3 CI checked one type name, not the known answers** (all three). Fix now: the Linux and macOS jobs assert with
  `jq` 2,000 objects of each leak type, 64,000 bytes and 148 retained per cache entry, the path from
  `StaticCacheLeak.s_cache` with CompleteGraph, a named runtime version and no incomplete heap note. A Windows CI step
  that loads a dump: deferred to the Backlog (the local gate covers it).
- **M4 A core's capture time is the file's time but was shown as exact** (gc-specialist). Fix now:
  `SnapshotFacts.CaptureTimeFromFile` and a note on open. Compare's baseline order for such snapshots: deferred to the
  Backlog with a revisit trigger.
- **M5 A dump of another system or architecture failed with a misleading message, and the platform rule was unwritten**
  (gc-specialist, solution-architect). Fix now: the missing DAC message leads with where to open the dump (system or
  architecture), never names version 0.0, and asks for the runtime library with a paired DAC; ADR-025 states that a dump
  is read by a worker of its own system and architecture, with a worker per runtime identifier; ADR-007 amended; Linux
  arm64 and cross-system reading in the Backlog. Fixed.
- **M6 Unix runtime roots were not searched** (gc-specialist; others Low). Fix now: `/usr/share/dotnet`,
  `/usr/lib/dotnet`, `/usr/lib64/dotnet`, `/usr/local/share/dotnet`, `/opt/dotnet` and `~/.dotnet`; an empty or relative
  root is skipped; paths compare by case on Linux and macOS only where the system does. Fixed.
- **M7 The prototype's grid adapter allocates a row per index when anything enumerates it** (gc-specialist). Defer to
  2.3.6, whose scope now requires select all, copy and keyboard paths to work by index and a probe test.
- **M8 ADR-026 had gaps** (solution-architect): where the export runs, how values are marked, the CLI's own output, a
  versioned schema, how an opened export is shown, metadata left out. Fix now: ADR-026 amended. Fixed.
- **M9 The capture refusal escaped as a raw PlatformNotSupportedException** (solution-architect; senior-dev Low on the
  CLI). Fix now: `CaptureFailure.NotSupportedOnThisSystem` from `ListProcessesAsync` and `StartCapture` (with `Find`
  inside the translation), and the CLI prints it without a stack trace; `CaptureRefusalTests`. An `IsSupported` query
  for front ends: in 2.3.6's scope, where the views need it. Fixed.
- **M10 Docs contradicted the decisions** (solution-architect): section 1, ADR-012, CLAUDE.md's intro, the Backlog's
  "Linux dump analysis". Fix now: all updated. Fixed.
- **M11 Tests skipped off Windows without Unix equivalents** (senior-dev). Fix now: the local path rule has Unix tests
  that run everywhere (H1). Deferred to the Backlog: Unix file semantics stated by tests, and Capture.Tests in the Linux
  job with the Linux capture item.
- **M12 The re-plan's order and scope** (solution-architect, Low, merged here with the 2.3.6 scope items): 2.5 and 2.6
  depend on 2.3.6; 2.5 states its platform scope; 2.3.6 adds the layering rows and the render yield in `IUiDispatcher`.
  Fixed in SPRINTS.md.

## Low / Trivial (listed; fixed where noted)

- L1 `ImageIdentity`: notes in 8-aligned PT_NOTE segments are padded to 4; universal (fat) Mach-O files are not read;
  catching `ArgumentOutOfRangeException` hides slicing bugs; the malformed-input guards are untested (gc-specialist,
  senior-dev).
- L2 `TypeGroups`: the assembly group key uses the raw module string, `ModuleKey` the file name without case
  (gc-specialist).
- L3 Case-insensitive path comparison on Linux (gc-specialist, solution-architect, senior-dev). Fixed with M6.
- L4 `ignoreMismatch` keyed on `Major == 0` (gc-specialist, senior-dev). Fixed with M1.
- L5 HeapScope's own files off Windows use the process umask (solution-architect): 0600 files and 0700 folders, with
  section 12, when Linux capture and exports arrive.
- L6 `RenderYield` is a static global (solution-architect, senior-dev). In 2.3.6's scope (`IUiDispatcher`).
- L7 CI: `dotnet-dump` unpinned, actions pinned by tag, `--ignore-exit-code 8` (solution-architect); the dump step ran
  after a failed build, the READY wait failed silently, no trap, no timeout (senior-dev). Fixed: pinned 10.0.750501, the
  build's outcome gates the step, the wait fails with the log, a trap kills the fixture, a 15 minute step timeout (macOS
  has no `timeout` command).
  Actions stay pinned by major version tag.
- L8 `LocatorAndModeTests` hard-codes Windows paths; section 7 states only Windows file semantics (solution-architect).
- L9 `LocalOnlyFileLocator`'s build id overload compared the wrong file for `SymbolProperties.Coreclr` (senior-dev).
  Fixed: such an image is left to `DacLocator`, which checks the pair.
- L10 The CLI's refusal message for `list` and `monitor` speaks of capturing; `monitor` could work on Linux (senior-dev).
  `monitor` goes with the Linux capture item.
- L11 `ClrMdDumpLoader`: `Load`'s doc comment sat above `CurrentVersion` (fixed); `ProcessName` does not use
  `RecordedPath` (senior-dev).
- L12 `IsCoreFile` accepted 32-bit and big-endian ELF (senior-dev). Fixed, with tests.
- L13 The macOS socket bound in `WorkerHostOptionsTests` should be `< 104` with the terminator (senior-dev).
- L14 `CapturePlatform.cs` holds two public types; guard order differs between the capturers; a fully qualified
  `RecordedPath` in `TypeGroups`; default probes still `WindowsElevationProbe` in constructors (senior-dev). The last
  part fixed: the new refusal test crashed on Linux through it, so every constructor defaults to
  `CapturePlatform.ElevationProbe()`.
- L15 The RenderYield path has no UI test (senior-dev). With L6 in 2.3.6.
- L16 `PipeNamePrefix` sits in Contracts but only Application uses it; attach names are not length checked for macOS
  (solution-architect).
- L17 ADR-017 and section 12 were not amended for the build id rules (solution-architect). Fixed with H1 and M1.
