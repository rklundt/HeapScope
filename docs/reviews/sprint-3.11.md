# Sprint 3.11 review: Screens that explain themselves

Reviewers: solution-architect, senior-dev, gc-specialist, support-engineer (2026-11-08), on `sprint/3.11` against `sprint-3.10`.
Each reviewer read the working tree (none had a shell for `git diff`). Findings that overlap are merged and keep the highest
severity. Counts: 3 High, 12 Moderate, 26 Low; the confirmations added 1 High (H4), 2 Moderates (M13, M14) and Lows L21 to L37.

Before the review: Debug build with 0 warnings; 1,633 tests, 1,628 passed, 5 skipped, 0 failed (integration tests included).

## High

### H1. Tasks 2 to 4 were ticked against proofs that did not exist
- Reviewers: solution-architect (High), senior-dev (Moderate, plus keyboard reach and Space).
- Where: `docs/SPRINTS.md` tasks 2 to 4; `tests/HeapScope.UI.Tests/InfoIconTests.cs`, `ThemeTests.cs`.
- What: no test put an icon beside a disabled button or showed a disabled control's tooltip; no test walked the pages (only Live
  was built); the wording test left out Table 1 and failed unclearly on a missing heading; Space and the icon's theme color were
  untested; and nothing checked that the keyboard reaches an icon inside a tab header or a list item.
- Why it matters: the Definition of Done rests on those proofs, and acceptance criteria 1 to 3 were not shown to hold.
- Fix: `ViewTests.Every_page_tab_and_view_shows_its_info_icons` walks every page, each Snapshots tab, Compare's trend mode and the
  Snapshot tab's views down to one object's three tabs, and finds 38 icons with their approved texts where a user sees them (the
  set views are not walked; their texts are held by the wording test). `InfoIconTests` gained: an icon beside a disabled button
  opens; a disabled button that shows on disabled opens its tooltip on hover; Space opens the icon; Tab reaches an icon in an
  expander header and in a list item; Table 1 is held word for word. `ThemeTests` ties the icon's color to `Brush.Subtle`, which
  the contrast test covers.
- Found by the fix: Tab could not reach the icons in the Object view's tab headers, so they moved into each tab's content; the
  Inspections list now lets Tab into its items (`KeyboardNavigation.TabNavigation="Continue"`), so its group headings' icons are
  reached.
- Status: Fixed. Confirmed by solution-architect and senior-dev (2026-11-08), with Lows L21 to L23 and L26.

### H2. The Processes page's CSV export named two columns "Avalonia.Controls.TextBlock"
- Reviewer: senior-dev (Moderate; raised to High: a regression in output this sprint introduced).
- Where: `src/HeapScope.UI/Controls/GridExports.cs` (`ItemsGridExport.From`), `ProcessesPage.axaml`.
- What: the Elevation and Working set headers became TextBlocks to carry a tooltip, and the export of in-memory rows took each
  header's `ToString()`.
- Fix: `HeaderText` takes a TextBlock header's text. `ExportTests` now gives one column a TextBlock header and checks the header
  line; with the fix undone, it fails.
- Status: Fixed. Confirmed by senior-dev (2026-11-08), with Low L25.

### H3. A "+N smaller" bar's hover text pointed at a double-click that does nothing
- Reviewers: support-engineer and senior-dev (Moderate; raised to High: acceptance criterion 4, no claim the code does not back),
  gc-specialist (Low, the "Keeps" wording).
- Where: `IcicleGeometry.Describe`, `docs/USER-GUIDE.md`.
- What: "Double-click the bar above them to see them." Under the top bar (level 1, the most common case) the bar above is the
  focus itself, and a double-click shows the same chart. And "Keeps X alive" for a group is the sum of its members' own retained
  bytes: together they keep at least that much.
- Fix: a group says "Together keep at least X alive". At level 1 it first said "The tree below lists them."; senior-dev confirmed
  that, but support-engineer did not: under Group children by Type or Namespace (which "Show in dominator tree" sets by itself)
  the tree lists groups, and past 100 rows a "more" row, so the members are not on screen. Second fix, true for every grouping:
  level 1 says "The tree below has them (expand a group or a "more" row if they are not shown)."; deeper, "Double-click the bar
  above them to focus on it; the tree below has them (expand a group or a "more" row if they are not shown)." Geometry and chart
  tests cover both levels; the guide matches.
- Status: Fixed. Confirmed by senior-dev (first fix) and support-engineer (second fix, 2026-11-08), who traced the members'
  reach under Type grouping through `DominatorTreeRows`; no test walks that tree path (noted, not a finding).

### H4. Opening a dump again left Compare and Trend on the earlier snapshot
- Reviewers: solution-architect and senior-dev, each on their own, while confirming M1 (2026-11-09).
- Where: `SnapshotsViewModel.ShowOpened`, `CompareViewModel`, `TrendViewModel`.
- What: M1's fix keeps the row, so Compare and Trend no longer saw a close and an open and did not run again. They kept the earlier
  graph's results (or its refusal), and a page fetched later found the result set gone and ran the old query against the handle's
  new worker snapshot (`ResultSetHandle.GetRowsAsync`): rows of two graphs under one set of totals, with no new "Compare pair" line.
- Why it matters: a wrong result, which the "was rebuilt, open it again" refusal exists to prevent; introduced by M1's fix.
- Fix: when an open changes the worker snapshot, `ShowOpened` calls `Compare.Reopened`, which runs a pair that holds it again,
  logged as "Compare pair run again, a snapshot of it was opened again" with the new ids, and `Trend.Reopened`, which runs a trend
  that ticks it again (its line names the ids). Not tied to the row's PropertyChanged, which a silent reopen of the same graph also
  raises. The three-snapshot test checks that the last diff and trend queries name the new id; without the call it fails.
- Status: Fixed. Confirmed by senior-dev and solution-architect (2026-11-09).

## Moderate

Decisions (product owner, 2026-11-09, "all recos"): fix now M1 to M6 and M8 to M12 (M12 with M1); M2 to M6 and M9 by
rewording approved text, proposed in `docs/design/info-text.md` for approval first; M6 also adds a Backlog item to mark
group rows' retained bytes as a lower bound. M7 to the Backlog. The owner approved the rewording table as proposed and chose to
fix M13 now (2026-11-09).

Status of the fixes (2026-11-09):
- M1 and M12: fixed (`483305b`). M12 confirmed by support-engineer. M1 confirmed by support-engineer and senior-dev for the symptoms
  listed; solution-architect first did not, as Compare and Trend went stale (H4), then confirmed M1 as a whole with H4 fixed.
- M8: fixed and confirmed by solution-architect.
- M9: the part with no new wording (one style for tooltips on disabled controls, Run synthetic job's tip, the Inspections menu
  text on its items, the guide) fixed and confirmed by solution-architect, senior-dev and support-engineer. The Record
  allocations tooltip's wording approved and built, now a row of Table 3 (44 single controls); confirmed by support-engineer
  (its "Needs" sentence matches `CanRecord`).
- M10: fixed and confirmed by support-engineer.
- M11: fixed and confirmed by gc-specialist and senior-dev.
- M2 to M6: rewording checked by gc-specialist (its corrections to rows 1, 2, 9, 10, 12 and 13 in), approved by the owner and
  built: Tables 1 to 3 of `docs/design/info-text.md`, `InfoTexts`, the Why alive, Group children by and Record allocations
  tooltips, and `ObjectIdentityTexts.Matched` (shared with the CLI). Row 9 left out "not in a younger generation than before",
  as M13 makes that test not hold for pinned objects. M6's code comments corrected (`DominatorDtos`, `IcicleGeometry`).
- M13: fixed. `ObjectIdentity` no longer tests the generation for an object a pinned handle, an async pinned handle or a
  pinned stack root holds in either snapshot, and counts such matches (`PinnedYoungerSurvivors`, in the worker's object
  matching log line). An unpinned object demoted beside a pinned one still counts as dead and new. Comment, ADR-032 and
  DEVELOPER.md corrected. Test: `ComparisonTests.A_pinned_object_the_GC_demoted_survives_and_an_unpinned_one_does_not`
  (fails without the change). No real dump fixture: a demotion cannot be caused on demand. support-engineer confirmed the
  Survived count and the privacy of the log line, but not the fix as a whole: it raised M14. Confirmed by gc-specialist
  against the CLR (pinned handles, pinned and interior stack roots, the gcdump path), which also confirmed the built rewording
  word for word against Tables 1 to 3, closing L32. M14 fixed and confirmed (below).
- M7: in `docs/BACKLOG.md`, confirmed by solution-architect.

- **M1. Opening a dump again replaces its row, with side effects** (support-engineer, solution-architect, senior-dev).
  `SnapshotsViewModel.ShowOpened` swaps in a new `SnapshotItemViewModel`. Compare treats it as a close and open: a pair the user
  chose falls back to the automatic one, and the log says "chosen automatically". Trend re-ticks an unticked snapshot and moves
  it last. The navigator shows the old subject again, so a refusal on an Instances, Object, Referrers, Paths or Set step stays on
  screen. The new log line leaves out the worker id the row showed. Only one snapshot on the Types step is tested.
  Suggested: refresh the row in place (an observable item, raised when the handle's descriptor changes), forget or requery the
  handle's analysis path, log both ids, and test with three snapshots, a picked pair, an unticked trend choice and an Object step.
- **M2. The Roots tile's text** (gc-specialist). The count includes weak handles and collectible types' statics, which keep
  nothing alive, and counts root entries, not objects. Suggested: reword (owner approval), or count only retaining roots.
- **M3. "A root is something the runtime always keeps"** (gc-specialist). Statics can be cleared and handles freed, and a local
  is a root only while the method still uses it. Suggested: "something the runtime holds on to by itself, such as a static field,
  or a local variable a running method still uses" (owner approval).
- **M4. "New and dead objects" wording** (gc-specialist). "Heap area" here means small object heap, LOH, POH or frozen, with
  promotion allowed, not the generations the app calls heap areas elsewhere; and a new object of the same type and size where a
  dead one was counts as survived, which no text says. Suggested wording in the reviewer's report (owner approval).
- **M5. POH and generations wording** (gc-specialist). The POH holds objects allocated as pinned, not every pinned object; and
  generations measure collections survived, not time. Suggested wording for Heap areas and the set's Generations (owner approval).
- **M6. Retainers: "their retained bytes stay exact"** (gc-specialist). A Type or Namespace group row adds up its members' own
  retained bytes; what several of them hold together sits in the row above, so the group's figure is a lower bound of what they
  keep alive together. Suggested: reword "Group children by" (owner approval); later, mark group rows' retained as a lower bound.
- **M7. The wording test checks presence, not placement** (solution-architect, senior-dev). A Table 3 text on several controls
  passes with one copy; a text on the wrong control passes. Table 1 is now held (H1). Suggested: map each row to its view file.
- **M8. Adding a page or setting has no written procedure or structural check** (solution-architect). Nothing fails if a new
  page has no icon. Suggested: a Consequences line in ADR-040, a DEVELOPER.md entry, and a test that each view under
  `Views/Pages` and `Views/Subjects` has an `InfoIcon`, with named exceptions.
- **M9. Tooltips on disabled controls, set control by control** (solution-architect, senior-dev, support-engineer). Missing on
  Run synthetic job, the trail's "more" button, Record allocations (whose approved draft named what it needs but did not reach
  Table 3), and the Inspections row menu's text sits on the menu, not its items. The user guide says every single button has one.
  Suggested: a style that shows tooltips on disabled controls everywhere, the missing tips (Record allocations' wording needs
  approval), and the guide reworded.
- **M10. logging.md has no 3.11 entry** (support-engineer). Suggested: what the reopen line says, that a retried key read is not
  logged, and that "+N smaller" grouping depends on the window's width and is not logged.
- **M11. Icicle grouping edge cases untested** (gc-specialist, senior-dev). Groups at two levels at once, a narrow, wide, narrow,
  narrow run, a group under MinWidth, and a size case at worker scale. Suggested: add the tests.
- **M13. A pinned object the GC demotes is counted as dead and new** (gc-specialist, raised while checking the rewording).
  `ObjectIdentity` refuses a match when the object is in a younger generation now, on the comment's claim that an object never
  changes it. The GC can demote: survivors around pinned plugs stay in Gen0 after a Gen1 collection, and with regions (.NET 7
  and later) a region of few pinned survivors can go back to Gen0. A pinned object never moves, so it survives at the same
  address but can read Gen1 in the baseline and Gen0 later. Pinned buffers, which leak hunts chase, then look like churn.
  Suggested: drop the generation test for objects pinned in either snapshot, or drop it and rely on type, size and heap; at
  least correct the comment and say "usually" in the wording; a fixture with a demoted pinned object.
  Decision: fix now (product owner, 2026-11-09).
- **M14. The matching log gives a wrong reason for an unpinned demoted object** (support-engineer, raised while confirming M13).
  `ObjectIdentity` counts every address match that fails a test as reused, and the log says "addresses reused by other objects".
  An object of the same type, method table, area and size that is only in a younger generation, unpinned (the case M13 keeps
  as dead and new), lands there too, so support cannot tell it from a real type or size mismatch when a user contests a New or
  Dead count. Suggested: count matches that fail only the generation test on their own, log them beside the pinned count,
  reword the rest as "addresses holding another type, size, area or method table", and extend the M13 test and logging.md.
  Decision: fix now (product owner, 2026-11-10).
  Fix: `ObjectIdentity.YoungerUnpinned` counts addresses whose objects match in all but a younger generation, unpinned, apart
  from `ReusedAddresses`; the object matching log line gives both, the rest as "addresses holding another type, size, area or
  method table". logging.md says what the new count can and cannot tell. Both identity tests assert the counts and fail when
  the case goes back under reused. Confirmed by support-engineer (2026-11-10): the match result is unchanged, only the counting
  moved, and the line now separates the two cases.
- **M12. ShowOpened's condition is broader than a refused reopen** (support-engineer). After a silent successful reopen the row's
  id is stale too, so a later open replaces the row and logs "shown afresh" when nothing was refused. Suggested: fold into M1.

## Low

Listed only (DEVELOPER.md 10).

- L1. A key file read retried under the lock is not logged; the outcome says Loaded either way (support-engineer).
- L2. "+N smaller" counts only the bars the worker sent; bars under 0.1 percent of the top bar are not sent (support-engineer,
  solution-architect).
- L3. Which segments were grouped is not logged (support-engineer).
- L4. Restart worker's tooltip leaves out that a changed dump or cache asks to be opened again (support-engineer).
- L5. ADR-040's amendment leaves out option B's consequences, tooltips on disabled controls and the approved source; its title and
  index line do not say amended (solution-architect).
- L6. `TypesOptionsInSet` is hand-copied from `TypesOptions` and not checked (solution-architect).
- L7. Every icon's accessible name is "What this is" (solution-architect, senior-dev).
- L8. The flyout and tooltip panels use Fluent's colors, not tokens, so no contrast test covers them (solution-architect).
- L9. A group is a made-up segment with address 0, which means the whole heap on the wire; a group can be narrower than MinWidth;
  an undrawn lone bar's children can still group under an empty spot (solution-architect, gc-specialist, senior-dev).
- L10. The unlocked key read does not share delete, so another process's replace of a damaged key can fail and fall back to
  this run only (solution-architect, gc-specialist).
- L11. `docs/design/info-text.md` is still titled a draft, and the names table does not record that the names stay
  (solution-architect).
- L12. `IcicleGeometry` sorts each level and the accumulated groups on every draw; "one walk along each level" overstates it.
  Within budget (defaults give about 6,000 segments) (gc-specialist, senior-dev, solution-architect).
- L13. "Growth in Gen2 is usually what stays" leaves out LOH and POH, which are also collected only with Gen2 (gc-specialist).
- L14. Fragmentation's text: its live figure counts garbage too, and Gen0 and Gen1 free space is normal (gc-specialist).
- L15. The Retainers page text's "the closest object on every path" can be read as the nearest referrer (gc-specialist).
- L16. `ObjectIdentity.Younger`'s comment: demotion near pinned plugs can make a survivor younger (gc-specialist).
- L17. Some tests close windows and delete folders outside `finally`; counts 48, 20 and 43 are hard-coded (senior-dev).
- L18. The Snapshot tab's page icon sits at the end of the "Opened from" line, which the next icon's text explains (senior-dev).
- L19. Three changed files list several public types and were not split (DEVELOPER.md 4) (senior-dev).
- L20. The group click test starts with no selection, so it cannot tell "ignores" from "clears" (senior-dev).

Raised while confirming the High fixes:

- L21. The page walk does not show the set views' texts (chosen by view model state: `SetTypes`, `TypesOptionsInSet`,
  `SetInstances`, `SetPaths`), nor Fragmentation and AllocationTrees, which need data; task 3's proof does not say so
  (solution-architect, senior-dev).
- L22. `KeyboardNavigation.TabNavigation="Continue"` on the Inspections list makes every tile a Tab stop, about 17 between the
  baseline picker and the rows (solution-architect, senior-dev).
- L23. The list Tab test sets Continue on its own list, so removing it from the view fails no test (senior-dev).
- L24. A group's members are the smallest siblings, so they sit behind "more" rows; deeper, focusing the bar above can show
  another group when many siblings are about 0.1 percent each (senior-dev, support-engineer).
- L25. A TextBlock header built from inlines, with no Text, would drop its column from the CSV without a warning; none is built
  that way today (senior-dev).
- L26. No test hovers an InfoIcon itself; hover is proven on a disabled Button (senior-dev).

Raised while confirming the Moderate fixes:

- L27. `before` is read ahead of the memory and DAC dialogs, so a silent reopen while a dialog is open makes an open that refused
  nothing start the analysis path again (support-engineer).
- L28. logging.md says the "+N smaller" groups are made in the view; they are made in `IcicleGeometry` from the chart's width
  (support-engineer).
- L29. `before` is the bare worker id: after a restart with no query since, opening an unchanged dump again starts its path
  again, and with H4 Compare and Trend run again and log "opened again" for an unchanged dump; deciding on the cache identity
  would avoid both (solution-architect).
- L30. The evidence table's locations for the text M9 moved are stale (InspectionsTab, AboutPage) (senior-dev).
- L31. `SnapshotItemViewModel.Reload` returns a bool no caller reads; `before` repeats the session's find-by-path rule, which a
  `SnapshotSession.Find` would share (senior-dev).
- L32. `ObjectIdentityTexts.Matched`'s comment describes only moved objects; once the reworded note is approved it should name
  reused addresses too (gc-specialist).
- L33. M7's Backlog item could also cover Table 2's group texts, which are checked for presence only (solution-architect).
- L34. The approved Compare texts (`ObjectIdentityTexts.Matched`, the New and dead objects line) say a moved object counts as
  dead and new but not that an unpinned object the GC demoted does too; changing them needs the owner's approval
  (support-engineer, gc-specialist: "moved, or rarely demoted," would cover it).
- L35. The comment at `ClrMdDumpLoader.cs:419` says an async pinned handle pins its object; if ClrMD reports the OverlappedData
  and not the user buffers, a demoted async I/O buffer still counts as dead and new. Check against the runtime-semantics
  fixture (gc-specialist).
- L36. "A pinned object never moves" in `ObjectIdentity` and ADR-032 holds only while it is pinned: a pin seen in either
  snapshot stands in for one at the demoting collection (gc-specialist).
- L37. The matching log line's new clause has a comma inside it ("..., unpinned, {Reused} addresses ..."), so its clauses are hard
  to tell apart; semicolons or "unpinned addresses whose objects differ only in a younger generation" would read better
  (support-engineer).
