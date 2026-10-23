# Review: Sprint 0.4 Fixtures and test harness

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-0.3`). Phase 0
runs in autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided
autonomously".

Totals after dedupe: 1 High, 12 Moderate, 12 Low.

## High / Urgent

### H1 Retained bytes bounds without an anchor; two bounds wrong
- Reviewers: solution-architect (High), gc-specialist (Moderate).
- Location: fixtures/KnownAnswers/Closure.json, UnboundedDictionary.json, README.md.
- What: the README did not say which object a per-item retained size is measured from. Measured from the item
  type, CapturedState retains 56 + payload (file said 80) and SessionRecord 56 + payload (file said 96); the
  UnboundedDictionary note had the dominance backwards.
- Why it matters: a correct dominator implementation in 2.3 would fail the known answers, inviting the analysis
  to be bent to match.
- Fix: format version 1 defines the bound as the retained size (dominator definition) of one `types[0]`
  instance; Closure and UnboundedDictionary corrected to 56 + payload; the note now says the Node dominates the
  record and its key. Verified now: `Item_retains_at_least_the_known_minimum` computes retained bytes with ClrMD
  by brute force (reachable from the item minus reachable from the roots without it) for every rooted fixture.
- Status: **Fixed.** Confirmation: see below.

## Moderate

### M1 Path shapes had no grammar and were partly wrong
- Reviewers: solution-architect, gc-specialist. Decision: **fix now** (decided autonomously). README defines the
  grammar (ordered subsequence, generic arguments removed, optional steps with `?`, `(dependent handle)`).
  UnboundedDictionary now goes through `+VolatileNode[]` (an array of structs, .NET 7 and later); EventHandler
  includes the optional multicast invocation list; `roots` is an array (EmbeddedStruct has both roots).
  `Retention_path_matches_the_path_shape` verifies every shape with a ClrMD breadth-first walk.

### M2 Event handler leak never multicast in tests
- Reviewers: senior-dev, gc-specialist, solution-architect. Decision: **fix now** (decided autonomously).
  `EventHandlerLeak.PublisherCount` is configurable (`--publishers`, `Leaks:Publishers`); the test dump uses 64
  publishers, so invocation lists are multicast and the path shape is verified in that regime.

### M3 Root kind not verified, root not shown to be the only retainer
- Reviewers: solution-architect, gc-specialist. Decision: **fix now** (decided autonomously). Exclusivity test:
  with the named roots' values removed, no item is reachable from any retaining root. Runtime root test: the
  static value is reached from a GC root, recorded as `runtimeRoot`. Finding: on .NET 10 static storage is held
  by a **strong handle** to an `object[]`, not a pinned handle; ARCHITECTURE.md 6.1 corrected, and the
  contradiction between 6.1 and the 1.2 scope resolved (storage handles stay retaining, paths prefer
  `StaticField` roots).

### M4 Harness not reusable for LeakyApi or several captures
- Reviewer: solution-architect. Decision: **fix now** (decided autonomously). `FixtureProcess` starts LeakyConsole
  or LeakyApi (READY line with the bound URL, port 0), drives `/work`, captures any number of dumps;
  `DumpAnalysis` holds the ClrMD checks; `TempFolder` cleans up. New `LeakyApiGrowthTests` captures two snapshots
  of one live API and checks growth per census type.

### M5 No per-item census; "leaking type" undefined for 1.4
- Reviewer: solution-architect. Decision: **fix now** (decided autonomously). `census` lists every type an item
  creates (exact or lower bound, shallow bytes); growth verified between two LeakyApi snapshots. Findings:
  `System.Action` exists in ASP.NET itself and `ConcurrentDictionary` nodes are recreated on resize, so both are
  lower bounds. The 1.4 acceptance criterion now says what "the leaking type" means.

### M6 Dead conditional weak table entries and weak-only objects never present in a dump
- Reviewers: solution-architect, gc-specialist. Decision: **fix now** (decided autonomously). New `LateGarbage`
  fixture: entries and weakly referenced objects promoted to gen2, then dropped after the last full collection;
  known answer and test: present (300 each) and unreachable from every retaining root.

### M7 No fixtures for root and heap kinds that 1.2 must label
- Reviewer: gc-specialist. Decision: **fix now** (decided autonomously). New `RuntimeKinds` fixture and known
  answer: finalizable objects (half suppressed), POH and LOH arrays, pinned handles, a frozen literal, a parked
  thread with a local root and one with an interior pointer. Findings: `GC.SuppressFinalize` does not remove an
  object from the finalization queue (the header bit 0x40000000 distinguishes them; `HasFinalizer` definition
  and ARCHITECTURE.md 6.1 updated); in debuggable code ClrMD reports no stack roots for the parked frames (three
  attempts; present in Release), so fixtures are always optimized and 6.1 records the limitation.

### M8 Fixtures for 1.6 and 2.6 missing; format not extensible
- Reviewers: solution-architect, gc-specialist (Low). Decision: format **fixed now** (`formatVersion`, optional
  fields by kind); the fixtures themselves **deferred** to 1.6 and 2.6 (decided autonomously), recorded in their
  scopes: repeated strings, empty and oversized collections, LOH arrays, timer leak, disposed-but-referenced
  objects, a non-leaking LeakyApi workload for the clean run.

### M9 Baseline readings optimistic and on the wrong metric
- Reviewers: gc-specialist, solution-architect. Decision: **fix now** (decided autonomously). `dump-stats` reports
  peak private bytes, runs the reference pass by address (`--by-address 1`) and can cap ClrMD's cache
  (`--cache-mb`). Medium: 3.5 GB private uncapped, 2.1 GB with a 512 MB cap at the same speed (81 s). Section 10
  rewritten (below the budgets table) with the density caveat and the requirement that 1.2 also measures a denser
  real-application heap.

### M10 Disabled leaks touched by LeakyApi
- Reviewers: senior-dev (Moderate), solution-architect (Low). Decision: **fix now** (decided autonomously).
  `/leaks` reads counts of enabled leaks only; growing a disabled leak returns 409; publishers are created
  lazily; `DisabledLeakTests` also checks `LongLivedPublisher`.

### M11 Sample dump script not robust
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). Port check up front, early exit detection
  with the API's log, build output shown on failure, partial dump deleted on failure, size range warning,
  quiet stop, parent watch.

### M12 Fixture processes outlive aborted test runs
- Reviewer: senior-dev. Decision: **fix now** (decided autonomously). LeakyConsole (`--parent-pid`) and LeakyApi
  (`ParentPid`) exit when their parent exits; the harness and the script pass it.

## Low / Trivial (recorded only)
- L1 (senior-dev) Readiness wait did not notice an exited process. (Fixed as part of M4.)
- L2 (senior-dev, gc-specialist) `DataTarget` not disposed when runtime creation fails. (Fixed as part of M4:
  `DumpAnalysis.Open`.)
- L3 (senior-dev) LeakyApi grow endpoint returned 500 for bad input. (Fixed as part of M10.)
- L4 (senior-dev) LoadGen validates `--dump` and `--pid` only after the load; rate and duration model not recorded
  (decision 8 records the model).
- L5 (senior-dev, solution-architect, gc-specialist) Baseline block inside the budgets table. (Fixed as part of
  M9.)
- L6 (senior-dev) `dump-stats` not listed with the benchmarks; RAM figures inconsistent. (Fixed in the docs
  update.)
- L7 (senior-dev) Several public types per file in fixtures and tests.
- L8 (senior-dev) Same-user and elevation check missing in the harness. (Fixed as part of M4: a clear message.)
- L9 (gc-specialist) Watchdog-style CPU time diagnostics for stack issues; not applicable after M7.
- L10 (solution-architect) No sidecar file recording sample dump parameters.
- L11 (solution-architect) Clean-run negative control. (Covered by M8.)
- L12 (gc-specialist) Duplicate strings, timers, disposed objects. (Covered by M8.)

## Confirmations
- H1 confirmed by solution-architect: format version 1 anchors the bound on one `types[0]` instance (dominator
  definition), Closure and UnboundedDictionary corrected to 56 + payload with the dominance note fixed, and the
  bound is verified by brute force for every rooted fixture. Its Moderate findings M1 to M5, M6, M8 (format part)
  and M9 confirmed as matching; its Low on a sample dump manifest remains open (L10).
