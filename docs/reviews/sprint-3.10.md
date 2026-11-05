# Review: Sprint 3.10 Visual design and README

Changes since `sprint-3.9`: the sprint itself (ADR-040: the design tokens, Inter, the Duotone icons and logo, the page work, the
new README and user guide with real pictures, the process name fix) and the work merged between sprints (Dependabot updates,
the SkiaSharp workflow made manual, two test flake fixes). Four perspectives, run in parallel with the briefs in
`.claude/agents/`: solution-architect, senior-dev, gc-specialist and support-engineer. None had a shell; each read the files
on `sprint/3.10`. Moderates are the product owner's to decide (no autonomy for this sprint).

Totals after dedupe: 5 High, 27 Moderate, 28 Low, counting what the confirmation rounds found. A finding raised by more than one reviewer is merged and keeps the highest
severity.

## High / Urgent

- **H1 The screenshot script deleted whatever folder it was given** (senior-dev). `build/screenshots.sh:41`. `rm -rf "$out"` on
  a documented argument, resolved after a `cd`, so `screenshots.sh ~/Desktop` would wipe the Desktop. Data loss for a developer;
  the default path was safe. Fix: only this script's own `NN-*-light.png` and `NN-*-dark.png` are removed, and a given path is
  resolved before the `cd`. Status: Fixed; confirmed by senior-dev, whose small notes (an inherited `out`, an empty argument, a
  failed `cd`, `CDPATH`) were applied.
- **H2 A dump's process name could be wrong and could not be explained** (support-engineer High; gc-specialist two Moderates;
  solution-architect and senior-dev Low). `ClrMdDumpLoader.ProcessNameOf`. ClrMD sorts modules by base address, so a core file's
  first module could be a library, the same defect decision 13 fixed on Windows; `/app/Orders.Api` became `Orders`; an empty
  first name returned "" and bypassed "Unknown process"; the log held only the result. The name feeds `IdentityMatcher`, which
  refuses New and Dead when names differ, so it is more than a label. Fix: the name comes from the executable's path the dump
  records (ClrMD's `IProcessInfoProvider`, found by gc-specialist in the first confirmation round). Without one, as on Mach-O, the
  modules decide: empty names skipped, " (deleted)" trimmed, the .exe rule on Windows targets only, else the first module not named
  like a library, else unknown. The rule is kept in the cache metadata and logged in the identity line, so a reopen still says it
  (`docs/architecture/logging.md`); new unit cases; `build/check-known-answers.sh` asserts `dotnet` on the real dumps CI takes on
  Windows and Linux. Checked on the demo dump, fresh and cached: `process dotnet (48260; name: the executable's path the dump
  records)`. Status: Fixed; two rounds; confirmed by senior-dev, gc-specialist and support-engineer. Their remaining points are
  M22 to M24 and L27.
- **H3 "Built and tested" up to 8 GB was not true** (support-engineer High; gc-specialist Low). `README.md:124`. ADR-004 sets
  the target; the largest measured dump is 4.4 GB. A user with a 7 GB dump was told this size was tested. Fix: "built for" that
  size, "measured so far up to 4.4 GB and 57.5 million objects". Status: Fixed; confirmed by support-engineer against
  `budgets.md`.
- **H4 "Each finding opens exactly the objects it counted" was not true** (support-engineer High; gc-specialist Low).
  `README.md:30`. Fragmentation rows name no objects and are refused (`ObjectSetResolver.cs:446`); other rows open their type's
  objects in scope. The first fix copied the guide's "a leak detector's row opens exactly the objects it counted", which support-engineer
  found false too: a growing collection's row opens the collection, event rows open delegates and their targets, `System.Timers.Timer`
  rows open two objects per timer. Fix: the README says a row opens the objects behind it (for a growing collection, the collection
  itself); the guide states each case, in support-engineer's own wording from the second round. Status: Fixed; three rounds; the final wording is
  support-engineer's own and was not reviewed again. Remaining: M25 to M27.
- **H5 "Any number of snapshots" was not true** (support-engineer). `README.md:57`, `docs/USER-GUIDE.md:25`. A trend takes at
  most 20 (`SummaryDtos.cs:275`), and the app says so. Fix: "up to 20" in both, and in `code-map.md`. Status: Fixed; confirmed by
  support-engineer.

## Moderate

The product owner decided 1 to 13, 15 and 17 to 21 on 2026-11-05, and asked for 22 to 27 to be rechecked and done after them.

- **M1 Live's empty state never hides** (solution-architect, senior-dev, support-engineer). `LivePage.axaml:143`,
  `LiveViewModel.cs:167`. `!Samples.Count` binds to a plain list with no change notification, so "Choose a process and press
  Start" shows through the transparent chart during a run wherever the layers sit low. No test. Suggested: a `HasSamples`
  property raised with `ChartVersion`, and a test. Status: Fix now (owner, 2026-11-05). Fixed: `HasSamples`, raised with `ChartVersion`, and a view model test.
- **M2 Fit to width can cut a diagram off with no way to reach it** (solution-architect, senior-dev). `KeyPathDiagram.cs:97`,
  `KeyPathDiagramView.cs:50`. The scroll bar is Disabled while the scale stops at 0.75, so a wider diagram loses roots or targets
  silently. Labels also go below ADR-040's 11 px floor. Suggested: disable the bar only when the fitted diagram fits, a view
  test, and state the exception in ADR-040. Status: Fix now. Fixed: the bar stays on Auto and the view fits to its scroll viewer's width; a test lays out a diagram too wide even at 0.75. ADR-040 states the exception to 11 px.
- **M3 Generated number columns can clip their numbers** (solution-architect, senior-dev). `ColumnHeaders.cs:34`. `NumberWidth`
  measures the header only; 92 px holds about 100 MB in bytes at most, so `400,000,000` clips and can read as a different number.
  Suggested: a floor by kind, 130 for Bytes and Integer. Status: Fix now. Fixed: 130 px floor for counts and bytes, 92 for a percent.
- **M4 The Allocations tree's truncation warning became a tooltip** (senior-dev, support-engineer). `AllocationsPage.axaml:101`,
  `AllocationTreeViewModel.cs:128`. The tree's only note says it was cut at 50,000 rows; a cut tree now looks complete. Suggested:
  restore the visible `ctl:NoteText`, which takes no space when empty. Status: Fix now. Fixed: the note is a visible line again. No test: a layout choice in XAML.
- **M5 The theme contract is not enforced, and contrast is tested on the wrong brushes** (solution-architect, senior-dev).
  `ADR-040:9-10,29`, `App.axaml:46`, `ThemeTests.cs`, `Contrast.cs:34`. A color literal in a view passes; the Fluent selection
  keys that controls paint with are separate copies the 4.5:1 test does not read; `Ratio` ignores alpha; a renamed Fluent key
  would fail nothing after an Avalonia update. Suggested: alias or test the keys against their tokens, fail on non-opaque colors,
  scan for literals in `Views/` and `Controls/`, pin the key names. Status: Fix now. Fixed: tests hold Fluent's keys to their tokens, the selected row opacities at 1, App.axaml's accent to the theme's, check each key still exists in Fluent, scan views and controls for color literals, and refuse a translucent pair. ADR-040 reworded. The plain `Brushes.*` fallbacks stay, documented.
- **M6 Theme change tests cover one chart of four** (solution-architect, senior-dev). `ThemeChangeTests.cs:49`. Task 8's proof
  and the Backlog's L14 name the icicle, the diagram and the allocation timeline; only `TimelineChart` is exercised. The code
  looks right. Suggested: reuse the helper for the other three and the icon. Status: Fix now. Fixed: the test now looks for the theme's color anywhere in the frame, for the timeline, icicle, diagram, allocation timeline and icon.
- **M7 The star column test misses weighted columns** (senior-dev; solution-architect Low). `GridLayoutTests.cs:81`. It matches
  only `Width="*"`, so four `2*` and `3*` columns have no `MinWidth`. Suggested: widen the regex, add the widths, test columns
  added after load. Status: Fix now. Fixed: the regex covers weighted columns, four `MinWidth`s added, and a test for a number column added after load. Hidden columns shown later are not tested.
- **M8 Real process names can reach a screenshot** (solution-architect, senior-dev). `ScreenshotRunner.cs:88`,
  `ProcessesViewModel.cs:104`. Made-up rows replace real ones after a fixed wait; a slow refresh overwrites them, and command
  lines can hold secrets. By hand and not in the README, which limits it. Suggested: await the refresh and assert the rows.
  Status: Fix now. Fixed: the runner waits for the refresh, and deletes the picture and fails if a real row came back.
- **M9 Docs disagree with the sprint** (solution-architect). `ARCHITECTURE.md:166,237,335`, `LayerRules.cs:67`,
  `snapshots.md:128`. The dependency table says Inter's assets are excluded, release builds omit macOS, the loader version is 3.
  Suggested: fix in the sweep. Status: Fix now. Fixed, with L20 (`security-privacy.md`).
- **M10 The Live time axis reads as run time but is window time** (senior-dev). `TimelineLayout.cs:74`. Only the last 1,800
  samples are kept, so once the window slides the axis always reads 0:00 to 30:00. Suggested: label true elapsed time from the
  run's start. Status: Fix now. Fixed: the chart takes the run's start, and ticks read the run's age at round steps.
- **M11 The README's performance table is old and its memory column understates** (gc-specialist, support-engineer).
  `README.md:99-104`, `budgets.md:25,27,55,93`. 15.8 s and 2.4 GB are sprint 1.2 first-load figures; later notes say 19 s, and
  computing retained sizes peaks at 3.3 to 3.9 GB. Suggested: rename the column, add the retained peak, remeasure on 3.10. Status: Fix now. Fixed: remeasured on 3.10 with new dumps, column renamed, retained peak added. See `budgets.md`.
- **M12 New, Dead and Survived are stated as facts** (gc-specialist). `README.md:56-58`, `USER-GUIDE.md:22-24`. ADR-032 says young
  objects usually count as Dead and New because ephemeral GCs move them. Suggested: say it is an estimate, most reliable for older
  objects. Status: Fix now. Fixed in both files.
- **M13 The CI growth gate has no capture advice** (gc-specialist). `README.md:82-88`, `USER-GUIDE.md:26-28`. Uncollected garbage
  can fail or pass a build; the advice exists only in DEVELOPER.md. Suggested: one sentence in both. Status: Fix now. Fixed in both files.
- **M14 A cache rebuild can block a dump whose DAC is gone** (gc-specialist). `SnapshotStore.cs:419-426`. A stale cache is rejected
  before the dump is known to load, so a `dotnet-dump` dump can stop opening after a runtime update, for a name change. Nothing is
  lost: the old cache stays. Suggested: fall back to the stale cache with a note, or accept while in pre-release. Status: to revisit with the owner after the others (owner, 2026-11-05).
- **M15 No test drives the cache rebuild** (gc-specialist). `SnapshotStore.cs:419-426`. Decision 13's "caches are rebuilt" has no
  proof. Suggested: an integration test in both memory modes. Status: Fix now. Fixed: `SnapshotTests.A_cache_from_an_older_loader_is_rebuilt_with_a_note`, in both memory modes. Sidecars are not asserted there; their identity check has its own tests.
- **M16 Cold first-show blips doubled, cause not established** (gc-specialist; senior-dev Low). `budgets.md:119-126`. From 3 to 7
  per run to 5 to 13, exempt from the gate, worst unchanged, lag 184 of 200 ms. "New styles" is inferred, and JIT is as likely.
  Suggested: trace one cold first show; a ceiling. Status: to revisit with the owner after the others (owner, 2026-11-05).
- **M17 A macOS user has no next step when macOS refuses the package** (support-engineer). `INSTALL.md`, `release.yml:194`. No entry
  under "If HeapScope does not start", no first snapshot bullet, no link from the release note, no request for results. Suggested:
  a short "If macOS refuses it" list marked unconfirmed, a `dotnet-dump` bullet, the link, an issue link. Status: Fix now. Fixed: an unconfirmed "If macOS refuses it" list, a first snapshot bullet, the release note links to it, an issue link. L17 fixed with it, since this sprint caused it.
- **M18 A slow first reopen after upgrading is explained only afterward** (support-engineer). `SnapshotStore.cs:229,419-426`. No
  reason during a 16 to 60 s wait, no release note, and "results could differ" overstates a name change. Suggested: a phase text
  and a release note line. Status: Fix now. Fixed: progress reads "Rebuilding the index cache: ...", the note no longer says results "could differ", and the release notes will say so at close.
- **M19 A failed job's reason is cut off** (support-engineer). `JobRow.axaml`, `JobViewModel.cs:92`. Refusals put the next step last,
  trimmed at 240 px with no tooltip, and the Jobs page is now the only place a finished job shows. Suggested: wrap when failed.
  Status: Fix now. Fixed: `Failure` wraps below the row and can be copied.
- **M20 New next steps point Linux and macOS users at Windows-only features** (support-engineer). `SnapshotTab.axaml:35`,
  `LivePage.axaml:143`, `AllocationsPage.axaml:59`, `HomePage.axaml`. Suggested: choose the text by `Unavailable`, point to
  `dotnet-dump`, disable Home's two buttons with the reason. Status: Fix now. Fixed: `PlatformHints`, and Home's two buttons are disabled off Windows with the reason in a tooltip. Not run on Linux or macOS.
- **M21 The README's license summary says more than the terms in places** (support-engineer). `README.md:152-161`. Reported for the
  owner only; ADDITIONAL-TERMS.md is not to be edited on a reviewer's suggestion. "Code, screens, or processes" goes beyond 6.2's
  "this work"; "help, version and startup output" drops 1.4's "where applicable"; "must be released" drops the condition of
  conveying or offering over a network. Status: Owner, 2026-11-05: "screens, or processes" stays as written; "where applicable" and the condition of distributing or offering over a network restored. ADDITIONAL-TERMS.md untouched.
- **M22 The name rule's log text can be false off Windows** (support-engineer, confirmation round). `ClrMdDumpLoader.cs:666`. On a non-Windows
  dump .exe modules are skipped, but the text says "(no .exe)". Suggested: build the text from the target system and assert it.
  Status: Still relevant after 1 to 18. Fixed: the text follows the target system, and a test asserts it.
- **M23 The recorded path is not logged, nor why it went unused** (support-engineer, confirmation round). `ClrMdDumpLoader.cs:596-623`. A failed
  read looks the same as a dump with no path. Suggested: put the path in the source text (`logging.md` allows paths) and say why a
  fallback was used. Status: Still relevant. Fixed: the path is in the source text, and a fallback says why it was not used. L27's `.exe` point fixed with it.
- **M24 No test covers the name rule surviving the cache** (support-engineer, confirmation round). `ProcessNameSource` appears only under `src/`.
  Suggested: a metadata round trip test, including JSON without the field. Status: Still partly relevant: M15's test already reads the rule back from a real cache. Fixed: the round trip test and old JSON without the field.
- **M25 The set line's count repeats objects** (support-engineer, confirmation round). `ObjectSetResolver.cs:422`. "The {n} objects listed for
  the row" counts repeats, so a log can read "1001 objects ... the 2000 objects listed". Suggested: "{n} entries listed for the row".
  Status: Still relevant. Fixed: "{n} entries listed for the row".
- **M26 Double-click does nothing on some rows that do hold objects** (support-engineer, confirmation round). `InspectionsViewModel.cs:233,239`.
  Timer "(static method)" and finalizer "(other types: n)" rows have no type or address, so only "Open as a set" opens them, with no
  explanation. Suggested: open the set for any detector row whose table has members. Status: Still relevant. Fixed: double-click or Enter opens such a row as a set, with a test.
- **M27 The CLI's help still says "the objects that row counted"** (support-engineer, confirmation round). `SetCommand.cs:54`. Suggested: "the
  objects behind that row". Status: Still relevant. Fixed: "the objects behind that row".

## Low / Trivial

Listed only (the skill: not fixed, not raised).

- L1 `release.yml` note step runs after the upload, so a failure leaves macOS packages with no note until a rerun; the header's
  rights text does not mention it (solution-architect, senior-dev).
- L2 `ScreenshotRunner.cs:57` and `screenshots.sh:25` say charts keep their first colors, which this sprint fixed
  (solution-architect, senior-dev).
- L3 `AllocationTimeline.cs:179` labels are 10 px; ADR-040 says 11 (solution-architect).
- L4 `AboutViewModel.Diagnostics` is a nullable `HomeViewModel`; extract a small view model (solution-architect, senior-dev).
- L5 `App.axaml.cs:148`: a run with only `--screenshots` falls through to a modal dialog on failure (solution-architect, senior-dev).
- L6 `ByteTicks` returns nothing under 1 MB and the maximum label is gone, so a tiny heap's chart has no scale; `DiffRowItem.Trend`
  is used only by tests (solution-architect, senior-dev).
- L7 `ThemeTests`: the icon test uses a hand list, not `NavigationItems`; `MainWindow.axaml:57` says 13:1 is tested where 4.5 is
  (solution-architect, senior-dev).
- L8 `ScreenshotRunner`: no cancellation token (precedent in `SmokeRunner`) (senior-dev).
- L9 `ScreenshotRunner.cs:67` logs the full output path (senior-dev).
- L10 `TimelineLayout.cs:88`: beyond 6 days the tick count grows without limit (senior-dev).
- L11 `make-icon.cs:109` writes premultiplied color as straight alpha: a faint fringe on the logo's corners (senior-dev).
- L12 `skia-native.sh:67`: `tr | awk ... exit` can return 141 under `pipefail` (senior-dev).
- L13 `ButtonIcon` shows nothing for null or control content; `NumberHeaders` shares an alignment between same-named columns
  (senior-dev).
- L14 `Styles.axaml`: the nav item's selected bar shifts its content 3 px (senior-dev).
- L15 `MainWindow.axaml`: "1 failed" reads in the subtle color (senior-dev).
- L16 Small cleanups: `GridExports.cs:118`, `SignConverters.HasItems`, `SnapshotsPage.axaml:42`, `LivePage.axaml` indent, `App.axaml`
  accent literals, `demo-data.sh` curl without `-f` (senior-dev).
- L17 `INSTALL.md`: "(under `$XDG_DATA_HOME`...)" now attaches to the macOS path (support-engineer).
- L18 Inspections: an unavailable tile's reason is trimmed at 150 px (support-engineer).
- L19 Live: "as gcdumps", "Record allocations" and a past-run count are hidden while the Expanders are collapsed (support-engineer).
- L20 `security-privacy.md:50-53` lists win-x64 and linux-x64 only (support-engineer).
- L21 `README.md:88` gives exit code 2 only; 1, 64 and 130 exist (support-engineer).
- L22 `USER-GUIDE.md`: the rules list omits "grown by", "then" and "at most"; "Ctrl+1 to 7" has no Cmd note (support-engineer).
- L23 An invalid `--Ui:Theme` is ignored silently and the theme is not logged (support-engineer).
- L24 "free, open source" beside terms that restrict AI use: the owner's call (support-engineer).
- L25 `SnapshotStore.cs:417-426`: the loader version is checked after the whole cache is read, leaving 1.5 to 1.8 GB of garbage in
  memory mode, unmeasured (gc-specialist).
- L26 `ProcessNameOf`: a process that maps a second .exe takes the first listed; the PE header's flag would settle it
  (solution-architect, senior-dev). Remains after H2 only where no path is recorded; the log states the rule.
- L27 `ClrMdDumpLoader.cs:622`: the recorded path's `.exe` is stripped on any system, so a Linux executable named `Foo.exe` shows as `Foo`;
  an exec through `/proc/self/exe` shows as `exe` (gc-specialist, confirmation round).
- L28 Comments and `code-map.md:53` still say "the objects the row counted": `ObjectSetResolver.cs:389`, `Inspection.cs:205`,
  `InspectionsViewModel.cs:231`, `DetectorTypeRow.cs:34` (support-engineer, confirmation round).

## Clean, per the reviewers

Layering, process boundaries and IPC (no project references changed, Contracts untouched); ADR-022's notice; ADR-025, 038 and 039
consistent; `release.yml` least privilege and the macOS rows; Inter licensing across package, notices and checks; no new
subscriptions leaking in the new controls; no hot path allocations of concern; no known answer relaxed; no log line carrying dump
contents or filter text; links and anchors resolve; nothing lost in moving detail to the guide; the new UI text about GC is accurate.
