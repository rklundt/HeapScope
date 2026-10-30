# Peer review: HeapScope 3.5.0, the whole codebase

An outside peer review of everything built through sprint 3.5 (develop `e59f90e`, after the v3.5.0 release), asked for
by the product owner on 2026-10-29: "look at what we have done so far as an outside set of agents in a peer review ...
from a tech trio: solution architect, sr. dev, and dev/sec/ops perspective what do we need to change, refactor,
modularize". Three reviewers ran in parallel and read the code, not a diff: solution-architect (SA), senior-dev (SD),
and a DevSecOps engineer (DSO, a general reviewer with a read-only brief that could run read-only commands such as
`dotnet list package --vulnerable`). The lead engineer merged overlapping findings and checked the Critical and the main
High findings in the code; "verified" marks those.

Severities for this review: Critical/Urgent (wrong results, data loss, security exposure, or a blocker for Phase 4),
High (real cost now), Moderate-High (worth doing soon), Lower (only where worth mentioning). The product owner split the
work into three sprints (2026-10-29): 3.6 Urgent fixes, 3.7 Hardening, 3.8 Modularity. Every finding below names its
sprint.

Done well (not findings): layering is enforced by the architecture tests and no front end calls the worker directly;
the worker pipe is current-user only with a random name and one instance; the worker starts with an argument list and no
shell; index cache sections are checksummed before use with no unsafe reads at file offsets; deletion stays inside the
session folder and refuses links; no package has a known vulnerability (`dotnet list package --vulnerable
--include-transitive`, 31 projects); the CI token is read-only; secret scanning and push protection are on.

## Critical / Urgent

- **P1 Session saves can fail, and a failed read silently starts a new session** (SD; verified). `SessionLibrary`
  writers take `session.lock` and replace `session.json` with `File.Move(temp, path, overwrite: true)`; readers
  (`TryRead`, `ListRecent`, `FindSnapshot`, `ListOlderThan`, `SessionToDelete`, `Current`) read with `File.ReadAllBytes`
  and no lock. On Windows a reader's `FileShare.Read` makes the replace fail ("Access to the path is denied", reproduced
  on .NET 10). A capture's dump then stays on disk but leaves the session's list (`CaptureService.cs` catches it), a run's
  end goes unrecorded (`MonitorRun.Record`), and `Current` treats any failed read, or another format version, as "no
  session" and creates a new one. `_gate` is also held across `AcquireLock`'s sleep loop of up to 30 s, so the cheap
  `CurrentFolder` getter can block the UI thread. Likely cause of the flaky
  `Concurrent_records_and_touches_never_lose_a_snapshot`. Fix: one session document file type that owns the protocol
  (reads share read, write and delete and retry briefly; the replace retries, bounded); `Current` creates a session only
  when the folder or document is missing and otherwise fails with a next step; current-folder state under its own lock;
  a two-process stress test. Sprint 3.6.

## High

- **P2 A DAC next to a dump is loaded as native code** (DSO; verified). `DacLocator.cs:116-123` adds the dump's folder
  and `dac/<version>` beside it as candidates; the match uses values the dump supplies (PE time stamp and size, or build
  id and version), so a zip with a dump and a planted DAC runs code in the worker. Linux and macOS have no signature check;
  whether ClrMD's Windows check refuses an unsigned DAC is unconfirmed. ARCHITECTURE 12 calls such a DAC one "the user
  chooses to trust"; unzipping is not that choice. Fix: by default only installed runtime roots, `Symbols:DacFolder` and
  HeapScope's own runtime; a DAC beside the dump needs an explicit trust step (the app names the file and its SHA-256; the
  CLI takes `--trust-dac-beside-dump`); a test that an untrusted DAC beside a dump is not loaded. Sprint 3.6.
- **P3 Published builds carry no license or notice files** (DSO; verified). `build/publish.sh` publishes the app and the
  CLI only; nothing copies LICENSE, ADDITIONAL-TERMS.md, COMMERCIAL.md or THIRD-PARTY-NOTICES.md. CI uploads the tarballs
  on every push to a public repository (68 artifacts). THIRD-PARTY-NOTICES lists SkiaSharp's native assets as MIT, while
  the package's own notices bundle libpng, ICU, libjpeg-turbo, the Adobe DNG SDK and more; the TraceEvent natives
  (`msdia140.dll`, `KernelTraceControl.dll`) the notices say to review before distribution are in the build output. Fix:
  the publish copies the license files and every package's bundled notices; the TraceEvent natives stay out until used;
  a CI check that a package holds them; THIRD-PARTY-NOTICES names the bundled native components. Sprint 3.6.
- **P4 CI is not enforced, and the integration tests never run in CI** (DSO; verified). The ruleset on `main` and
  `develop` has no required status checks; CI skips `Category=Integration` and `Category=Slow` (227 integration tests,
  including the crafted gcdump refusal); the Linux and macOS test list is hard coded. The local gate was last run
  2026-10-26 (11 of its 28 recorded runs failed), and 12 of the last 64 CI runs failed. Fix: the integration tier in
  CI (Windows, on PRs and nightly); test results kept by CI; required checks (a repository setting, the product owner's
  call). Sprint 3.6.
- **P5 One worker interface, one worker class and one Application class carry every feature** (SA, SD).
  `IWorkerService` has 35 methods, implemented by one partial `WorkerService` (14 files, about 2,750 lines, shared hidden
  state such as `_sets`); `SnapshotSession` mirrors it with the same query pattern 12 times and the reopen-after-restart
  protocol three times (`UseAsync`, `UseBothAsync`, `UseManyAsync`), and `AllocationSession` has a fourth. The test fakes
  implement everything (`Fakes.cs`, 1,009 lines; 35 throwing stubs in `ResultSetHandleTests`). Fix: contracts split by
  area (control, snapshots, result sets, heap queries, retention, inspections, allocation traces) served on one
  connection in one protocol bump; a handler per contract with shared services injected; one query helper and one
  `WorkerResource` reopen protocol in Application; per-area fakes. Sprint 3.8.
- **P6 Phase 4's live session has no place in the process model; ADR-023 has drifted** (SA). Counters and the allocation
  trace run in the front end (`MonitorService`); the worker reads only finished trace files and can push nothing but
  progress. Streaming allocations into the UI would break ADR-007 and ADR-028; doing it in the worker breaks the
  dependency table (the worker may not reference Capture). ADR-023 still says gcdump capture moves into the worker "in
  Phase 2". Fix: ADR-035 "Live sources" after a short spike; ADR-023 amended. Sprint 3.8.
- **P7 The UI offers features the worker then refuses** (SA; verified). Front ends decide availability from the snapshot
  kind or a stand-in capability: `CanOpenUnreachable` checks only `Kind == FullDump`
  (`TypeSummaryViewModel.cs:260`) while the worker also requires a complete graph; interface grouping keys on
  `FieldValues`; three worker sites check `Kind == GcDump` although the kind handlers say they never should. Fix: a
  feature availability list on the snapshot descriptor (feature id, null or the reason), filled by the worker from
  capabilities and facts; front ends bind to it. Sprint 3.8, with P5's protocol bump.
- **P8 Eleven view models skip the shared loader, so refusals go unlogged and carry no trace id** (SD).
  `SectionLoader` starts a query activity and logs a refusal; hand-written copies (retention, similar retention,
  referrers, instances, retainers, retained by set, set dominators, compare, trend, types, allocation tree) show
  `ex.Message` without logging, and their worker lines have no front-end trace id. Breaks the provenance rule. Fix: one
  injected query runner used by all of them; a test that a refusal is logged under its activity. Sprint 3.8.
- **P9 Events are raised inside the work they report on** (SD). `JobTracker` invokes `JobChanged` inside the job's
  `try`, so a throwing handler flips a completed job to failed (in the CLI a broken stderr pipe is enough);
  `SnapshotSession` raises `SnapshotsChanged` inside the open job; `WorkerHost` raises `StateChanged` under its gate.
  `MonitorRun.Raise` already does it safely. Fix: one safe raise helper, called after state is final and outside gates;
  a throwing-subscriber test per raiser. Sprint 3.8.
- **P10 The CLI reads columns by position, keeps services in a static field and has no test project** (SD, SA).
  `long.Parse(row[4]!)` and similar in five files (`DiffCommand.cs:347` feeds the growth gate), so a worker column change
  shifts CI numbers silently; `s_services` beside captured services; usage errors (exit 64) are tested only by spawning
  the executable in integration tests; "open with progress" exists three times. Fix: a CLI context instead of the static;
  `HeapScope.Cli.Tests` in process; cells by column name (the helper moves out of UI.ViewModels); the remaining commands
  out of `CliCommands.cs`. Sprint 3.8.
- **P11 The monitoring run does eight jobs, its trace watchdog is never awaited, and timing tests flake** (SA, SD).
  `MonitorRun` holds sampling, rules, captures, Force GC, the timeline CSV, the trace watchdog, the session record and
  events; `WatchTraceAsync` is fire-and-forget and may call `CancelAsync` on a disposed source; `LiveViewModel` is 724
  lines. Flaky tests: `WorkerIpcTests` measures wall clock under 1 s outside a non-parallel collection with a 40M-object
  job; the CLI monitor test waits for a growth rule that needs a GC the fixture never forces; a `Task.Delay(200)` asserts
  that nothing happened. Fix: the flaky tests in 3.6 (so CI can be required); the split (timeline writer, allocation
  recording as an awaited disposable, run record; LiveViewModel into live, past runs and timeline actions) in 3.8.
- **P12 ARCHITECTURE.md and SPRINTS.md are too large to stay correct, and have drifted** (SA, DSO). 2,300 and 3,059
  lines; the code map misses `SetCommand.cs`, `TrendCommand.cs`, `SnapshotStore.Interfaces.cs` and `WorkerVersions.cs`;
  the protocol history is kept twice; ADR-001's heading still says WPF; about ten Backlog items are built (trends,
  growth by generation, deletion, retained bytes in diffs, one capture per target, type growth and more); section 12
  still lists deletion as future work. Fix: an overview ARCHITECTURE.md with module pages and one ADR per file; closed
  sprints and the Backlog out of SPRINTS.md, the Backlog swept. Sprint 3.8.

## Moderate-High

- **P13 Crafted files can hang the worker or exhaust its memory; no fuzzing** (DSO; the hang verified).
  `AddressIndex.cs:67-72` loops forever when few objects span 2^63 or more (a `ulong` shift of 64 wraps to 0); gcdump
  addresses are not range checked. The minidump header check is quadratic on duplicate entries without a cancellation
  token; gcdump nodes sharing one blob multiply into billions of edges; vendored reader allocations are sized by file
  counts (the 10 million node cap applies to the writer only). All are managed failures in the separate worker, none
  memory-unsafe. Fix: bound the loop and validate addresses; dedupe and cancel the header check; validate counts against
  remaining bytes and cap children (vendored `CHANGES.md` change 5); an edge budget per load; a nightly fuzz job. Sprint 3.7.
- **P14 An index cache beside the dump is trusted on unkeyed checksums** (DSO). The cache beside the dump is preferred;
  the fingerprint is size, time and XxHash3 of the first and last MiB, and a dump's time survives unzipping, so a shipped
  `app.dmp.hsidx` decides every count, size and root. Fix: an HMAC over the header with a per-user key (the header covers
  the section checksums), also on the sidecars; rebuilt on mismatch; structural checks on open. Sprint 3.7.
- **P15 Gaps in "files only you can read"** (DSO). The etlx folder on Linux is the shared `/tmp/HeapScope/etlx`, kept
  with whatever owner and mode it has; `PrivateFiles` never repairs an existing folder or file; trace `.partial` and CSV
  export files open with `FileMode.Create` under predictable names in folders the user picks; a Windows `--out` dump
  inherits the folder's ACL. Fix: a per-user etlx folder checked for owner and mode; existing paths repaired; random names
  created with `CreateNew` through `PrivateFiles`; owner-only ACLs on `--out` dumps and their caches. Sprint 3.7.
- **P16 No supply-chain baseline** (DSO). No `nuget.config` (no `<clear/>`, no source mapping), no lock files,
  transitive pinning off, Dependabot alerts and updates off, actions pinned by tag, checkout keeps credentials, no
  SECURITY.md or private vulnerability reporting, no CODEOWNERS, unsigned tags. Patch updates are available for the
  Microsoft.Extensions packages, System.IO.Hashing and System.CommandLine. Fix: all of these (repository settings are the
  product owner's call). Sprint 3.7.
- **P17 Releases are manual, with no checksums, SBOM or provenance** (DSO). Fix: a `release.yml` on a `v*` tag that
  builds from the tag with `ContinuousIntegrationBuild`, writes `SHA256SUMS`, a CycloneDX SBOM (also diffed against
  THIRD-PARTY-NOTICES) and a build attestation, and attaches them; 2.8 adds signing. Sprint 3.7.
- **P18 The CLI and the app duplicate texts and rule parsing** (SA, SD). Retained-source, identity and scope texts are
  written twice with different wording; monitor rules are parsed twice with different grammars (`--every 90s|5m|1h`
  against minutes in the current culture). Fix: shared presentation in Application (column access by name, texts, a
  monitor rules parser). Sprint 3.8.
- **P19 The leak detectors are one 1,672-line file with three definitions of "framework type"** (SD, SA).
  `DetectorGraph.IsFramework`, `TypeGrowthDetector.IsFramework` and `RetentionMapping.IsFrameworkType` disagree on
  `Internal.` types and modules; the tally pattern is copied; runtime field names are hard coded. Fix: a file per
  detector, a shared tally, one framework classifier in Analysis with a test, a runtime layouts table. Sprint 3.8.
- **P20 Per-snapshot derived state has three owners** (SA, SD). The documented home is the worker's `DerivedData`
  slots; the object set cache lives on `WorkerService` with a manual `Forget`; Analysis keeps static
  `ConditionalWeakTable` caches outside the memory reservations, never dropped on close, computed twice under
  contention. Fix: Analysis returns values, the worker keeps them in derived slots; the set cache moves onto the
  snapshot's entry. Sprint 3.8.
- **P21 The Snapshots page holds one analysis at a time** (SA). `SnapshotsViewModel` is a singleton owning one Types,
  Navigator, Compare, Inspections and Retainers re-pointed at the selected snapshot; paths are keyed by snapshot. Fix: an
  `AnalysisWorkspaceViewModel` per snapshot (one per snapshot for now), so Phase 4's analysis tabs are cheap. Sprint 3.8.

## Smaller points

- **S1 CI scripts are duplicated and drifting** (DSO): the fixture start-and-wait script twice in `ci.yml`, a hard-coded
  Linux test list that silently skips new projects, `--ignore-exit-code 8` hiding a filter that runs nothing, a
  Windows-only gate parsing output with regexes. Sprint 3.6, with P4.
- **S2 Flaky-test tracking stopped** (DSO): `artifacts/gate/history.csv` was last written 2026-10-26; test results kept
  by CI replace it. Sprint 3.6, with P4.
- **S3 Paths read from a received `session.json` are trusted too far** (DSO): "Open timeline" shell-launches any file in
  the session folder (a `.bat` named as the timeline runs); snapshot entries may be network paths checked on open; a
  run's trace path is not kept inside the folder. Fix: a regular `.csv` file for the timeline; no network or device
  paths; contained trace paths. Sprint 3.7.
- **S4 The partial-file sweep reaches too far** (DSO): it recurses with default enumeration options and also sweeps the
  user's `--out` folder, deleting other people's `*.dmp.partial` files older than ten minutes. Fix: skip links and sweep
  HeapScope's own folders only. Sprint 3.7.
- **S5 Two type matchers disagree** (SA): `OfTypeKeys` normalizes a module given as a path, `TypeGroups.MatchTypes` does
  not, so a set matches where Instances does not. Fix: one matcher in Analysis. Sprint 3.8.
- **S6 Worker interface leftovers** (SA): the obsolete `GetTypeSummaryAsync` is still used by the CLI, test hooks sit on
  the production interface, and the version bump rule is applied unevenly (19 was bumped for catalog-only additions).
  Sprint 3.8, with P5.
- **S7 trace-report still looks for WPF's dispatcher** (SD): automatic UI thread detection finds nothing in an Avalonia
  process. Sprint 3.8.
- **S8 About 1,000 comments cite sprint and review ids** (SD): many narrate history rather than the current reason.
  Fix: a DEVELOPER.md rule (state the current reason; an id only beside a guard someone might simplify away) applied to
  files 3.8 touches. Sprint 3.8.
- **S9 "One public type per file" is not followed** (SD): `MonitorService.cs` has 11, `SessionLibrary.cs` 9,
  `ResultSetHandle.cs` 11 or more. Enforce it or drop it from DEVELOPER.md 4. Sprint 3.8.

Not raised by the reviewer, recorded only: a race an elevated run's deletes are exposed to (a UAC bypass, which
Microsoft does not treat as a security boundary) and no total size cap on logs.

## Plan

| Sprint | Theme | Findings |
|---|---|---|
| 3.6 Urgent fixes | Data loss, security, a CI that can be trusted | P1, P2, P3, P4, P11 (flaky tests), S1, S2 |
| 3.7 Hardening | Untrusted input and supply chain | P13, P14, P15, P16, P17, S3, S4 |
| 3.8 Modularity | Refactoring ahead of Phase 4 | P5 to P12 (P11's split), P18 to P21, S5 to S9 |
