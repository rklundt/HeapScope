# Sprint 3.11 review: Screens that explain themselves

Reviewers: solution-architect, senior-dev, gc-specialist, support-engineer (2026-11-08), on `sprint/3.11` against `sprint-3.10`.
Each reviewer read the working tree (none had a shell for `git diff`). Findings that overlap are merged and keep the highest
severity. Counts: 3 High, 12 Moderate, 26 Low.

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

## Moderate

Decisions: pending the product owner.

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
