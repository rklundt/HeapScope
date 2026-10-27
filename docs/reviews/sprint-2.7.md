# Review: Sprint 2.7 Navigation and polish

Changes since `sprint-2.6`. Four perspectives, run in parallel: solution-architect, senior-dev, gc-specialist, and a
support and system analyst (a general reviewer with a read-only brief, so the logs can back up any result a user
contests). Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go with your reco for moderate
recommendations"), marked "decided autonomously". None of the reviewers had a shell; each read the files of the change
set directly.

Totals after dedupe: 3 High, 14 Moderate, 27 Low (the re-review of the fixes added 11 Lows, 7 of them fixed). A
finding raised by more than one reviewer is merged and keeps the highest severity.

## High / Urgent

- **H1 Exports of grids held in memory wrote the targets' command lines without asking** (all four).
  `GridExports.ItemsGridExport` built every column as plain text with `HasValues` false, so the Processes grid's
  "Command line" column, which often carries connection strings and tokens, went into the CSV with no question and no
  notice. ADR-026 lists the command line as metadata left out by default. Fix: a column of an in-memory grid can say it
  carries the target's data (`ctl:GridExports.HasValues="True"`), which the export reads like a worker column's flag;
  the Processes grid's Command line is flagged, so it goes through the values question. Tests: `ExportTests`
  (`A_grid_of_rows_in_memory_exports_its_visible_columns_from_the_context_menu` checks the flag;
  `Columns_that_show_the_targets_data_are_flagged_in_every_view` checks the Processes view). Status: Fixed.
- **H2 Highlighting with overlapping terms drew names that do not exist** (senior-dev; solution-architect as Moderate).
  `NameFilter.Match` returned one range per positive term without merging, and `HighlightedText` drew each range, so
  `entry entry` showed "StaticCacheEntryEntry" in the types and Compare grids. Fix: `Match` merges overlapping and
  touching ranges; the highlighter never draws a character twice. Test: `NameFilterTests`
  (`Highlighting_marks_every_name_term_in_order_and_skips_module_terms`, the repeated and overlapping cases). Status:
  Fixed.
- **H3 Two HeapScope windows overwrote each other's bookmarks** (solution-architect; senior-dev as Low with the cache
  that drifted after a failed save, support's flush). The store read the file once per process and rewrote its own list
  through one fixed temporary name, so the last window to save won and the two could collide on the temporary file.
  Fix: every read reads the file; every change takes a lock file (`bookmarks.json.lock`, waited for up to 5 s), reads
  the file again, changes it and writes it through a per-process temporary file, flushed to disk before the move. No
  in-memory list is kept, so a failed save leaves nothing out of step. Test:
  `BookmarkStoreTests.Two_windows_changing_bookmarks_keep_each_others_marks` (40 changes from two stores in parallel).
  Status: Fixed.

## Moderate

All decided autonomously.

- **M1 Exports could not be canceled and left short files** (gc-specialist, solution-architect, senior-dev, support).
  No token reached `GridExportService`; a cancel, a replaced grid or an IO error left a truncated CSV at the chosen
  path; a second Command+E opened a second picker; an exception outside the caught types was lost by the window's
  fire-and-forget call. Fixed now: a Cancel export button in the status bar (`GridExportService.CancelCommand`); the file
  is written as `<name>.partial` and moved into place only when complete, and deleted otherwise; the export is marked
  running before the dialogs; any other exception is logged and shown. Progress per page is not shown (the status gives
  the row count): left as it is. Tests: `GridExportServiceTests` (`Cancel_stops_the_export_and_leaves_no_file`,
  `A_second_export_while_one_runs_is_refused_before_any_dialog`, no file left after a replaced grid or an IO error).
- **M2 An export without values still reads every value in the worker** (gc-specialist, solution-architect). Pages
  carry every column, so exporting a million strings without values still reads each string and sends its text to the
  front end, where it is dropped; ADR-026 described the export as worker-side with a schema. Deferred to the Backlog: a
  column projection on page requests (a protocol change). ADR-026 amended now: the CSV export is a front-end renderer
  of a grid; the whole-analysis export with its schema stays in the Backlog.
- **M3 Memory held by every snapshot's path** (gc-specialist, solution-architect). ADR-029 said the cost was unchanged;
  it now grows with the open snapshots (up to 30 steps each, each with its row lists in the front end), and the
  worker's result set store (32 sets, 256 MB) evicts sets of paths not shown, which re-run when shown again. Fixed in
  part now: ADR-029 states both. Deferred to the Backlog: releasing the result sets of snapshots not shown, and
  measuring front end and worker memory with three Medium snapshots open.
- **M4 CSV headers lost lower bounds and estimates** (solution-architect). Fixed now: `CsvExport.Header` writes
  "Retained (at least)" and "Bytes (estimated)" from `ResultColumn.Bound`. Test:
  `CsvExportTests.Headers_say_when_numbers_are_lower_bounds_or_estimates`.
- **M5 The default growth baseline trusted a file's time** (solution-architect). A copied dump's capture time is only
  the file's; the Compare tab already refuses to order by it. Fixed now: `IsEarlierOfSameProcess` matches nothing when
  either side's time is the file's, so no default is chosen. Test: the theory
  `An_earlier_capture_of_the_same_process_matches_by_id_else_by_name`.
- **M6 Inspection columns are still hidden by name, and the flags were untested** (solution-architect). Fixed in part
  now: a worker test checks the flags `InspectionSource` sends (`Inspection_columns_carry_their_hidden_and_value_flags_onto_the_wire`).
  The name rule (`InspectionColumns.IsHidden`) stays, applied in one place where Analysis columns become wire columns;
  every inspection's hidden columns share those three names.
- **M7 The CLI's CSV had no formula guard** (solution-architect). String values from a dump could run as spreadsheet
  formulas. Fixed now: the CLI writes cells through `CsvExport.Cell` (the columns are unchanged, ADR-026).
- **M8 Bookmark toggles raced** (senior-dev). A held Command+D added two items for one object; a snapshot closed during
  a save got an item; a failed load stayed cached. Fixed now: one toggle per object at a time, an item is added only
  while its snapshot is open, a failed load is retried. Test:
  `BookmarksViewModelTests.A_repeated_toggle_while_the_first_is_saving_makes_one_bookmark`.
- **M9 Exports of in-memory grids ignored the grid's sort** (senior-dev). Fixed now: rows are read through the grid's
  collection view. Test: the in-memory export test sorts and exports.
- **M10 An export could not be matched to its query** (support). Fixed now: the export's log lines name the worker
  result set (whose query the worker logged with its filter and sort), the rows expected and the rows written, and a
  failure's HResult.
- **M11 Analysis path lines could not name a step or a snapshot** (support). Fixed in part now: each step has an id on
  its path; open, show and close lines carry the step, its parent, its depth and the worker snapshot id; switching
  snapshots is logged. A subject's own query lines follow its step line; tying them by trace id is left as it is.
- **M12 Filter terms were logged by length only** (support). An exclusion term could not be seen when a type was
  missing. Fixed now: Provenance logs a filter's shape (its length and its counts of terms, excluding terms and module
  terms), never its text. Test: `ProvenanceLogTests.A_filter_is_logged_by_its_shape_never_its_text`.
- **M13 Problems with the bookmarks file were silent** (support). Fixed now: the store reports what it found
  (`BookmarkFileProblem`: newer format, damaged, unreadable); the list shows each with its next step and the log warns;
  a damaged file is set aside with its time in the name. Tests: `BookmarkStoreTests`,
  `BookmarksViewModelTests.A_damaged_bookmarks_file_is_said_when_the_snapshot_opens`.
- **M14 Edge cases without tests** (senior-dev). Fixed now with the findings above, plus `AnalysisPathTests` for a
  spliced Retainers step, closing the branch Forward would take, and `CloseAll`.

## Confirmations

The fixes went back to the reviewers who raised them (2026-10-27), who re-read the code and tests:
- solution-architect: H1, H2 and H3 confirmed; M1, M2, M3, M5, M6 and M7 accepted; M4 accepted in part (a gcdump's
  sampled counts still export without a mark, L17). New Lows L18 and L19.
- senior-dev: H1, H2 and H3 confirmed; its Moderates (M1, M8, M9, M14) confirmed. New Lows L20 to L23.
- gc-specialist: H1 and M1 confirmed; the M2 and M3 deferrals acceptable (it suggests trimming the row lists of
  subjects not shown, folded into the M3 Backlog item). New Lows L24 to L26.
- support: H1 and M1, M10, M11, M12 and M13 confirmed; nothing sensitive newly logged. New Lows L27 and the residuals
  noted under L12 and L13.
- H1 to H3 are Fixed and confirmed.

## Gate notes

The final full runs passed everything but one timing test each time, a different one in each run, all in areas this
sprint did not touch (`WorkerIpcTests.Cancellation_stops_a_large_synthetic_job_within_a_second` waiting 60 s for a
first progress report, `CounterMonitorTests`' growth rule capture, `SessionLibraryTests`' concurrent records); each
passes alone and in the integration project's own run. They are load-sensitive on this machine while several test
projects run at once; the Backlog's dispatcher lag item covers the same cause.

## Low / Trivial

Listed, not fixed (a few were fixed in passing where the code was open, as noted).

- L1 The instances Value column holds a string's text or an array's length; flagging the whole column drops lengths
  from exports without values (gc-specialist). Split into Length and Value later.
- L2 `CsvExport` allocates per row and per cell (`string.Join`, a char array per `IndexOfAny`) (gc-specialist).
- L3 `NameFilter` parses the filter for every row in the worker (gc-specialist, senior-dev).
- L4 The Bookmarks list shows the file name only, never prunes marks of missing files, and does not say a mark belongs
  to one dump file (gc-specialist).
- L5 The shortcut table holds gestures and texts, but the actions are a switch in the window; on macOS the window takes
  Option+Left and Option+digits from text boxes (solution-architect).
- L6 `bookmarks.json` goes in the parent of a configured `Sessions:Root` (solution-architect).
- L7 The shell's fallback export service would answer yes to the values question (its picker never picks a file)
  (solution-architect, senior-dev).
- L8 Task 8 in SPRINTS.md said "id and start time" (solution-architect). Fixed in passing: the wording matches
  decision 8.
- L9 Command+D ignores a selected field in the fields tree and inspection rows (senior-dev).
- L10 A closed step's subject stays referenced by the navigator's Shown properties until replaced (senior-dev).
- L11 After a close, Forward goes to the last child in the list rather than the one shown last (senior-dev).
- L12 Failure lines logged the exception type only (support). Fixed in passing for exports (the HResult is logged).
- L13 Opening a bookmark was not logged as such (support). Fixed in passing ("Bookmarks: opened one").
- L14 The baseline lines log full snapshot paths and not which rule matched (support).
- L15 The values notice comes before the CSV header, which tools reading the header from line 1 take as the header
  (support).
- L16 The bookmarks file was not flushed before its move (support). Fixed with H3.

Raised by the re-review of the fixes:
- L17 A gcdump's sampled counts (`ExactCounts` false) export with nothing to say so (solution-architect); with M2 in the
  Backlog.
- L18 The lock file was deleted on close, which on Linux and macOS lets two processes lock two files of one name
  (solution-architect). Fixed now: the lock file stays.
- L19 A Cancel landing as the export ended could reach a disposed token source (solution-architect). Fixed now: the
  source is cleared before it is disposed.
- L20 The export's catch-all logged the exception's message, which can name the chosen path (senior-dev). Fixed now:
  its type and HResult only.
- L21 The values question and notice said "read from the snapshot", wrong for a command line (senior-dev, support).
  Fixed now: "the target's own data (string text, field values or command lines)".
- L22 A reader in another window could make the bookmarks file's replace fail on Windows (senior-dev). Fixed now: the
  move is tried again briefly.
- L23 The bookmarks file problem was read after the store's lock (senior-dev, support). Fixed now: `For` returns the
  problem its own read found.
- L24 An existing `<name>.csv.partial` is overwritten, and a killed export leaves one (gc-specialist).
- L25 An export that ends short (an empty page) is moved into place as complete; the log says rows of expected
  (gc-specialist).
- L26 No progress during long exports (gc-specialist).
- L27 A lock wait that timed out showed the system's message (support). Fixed now: "Another HeapScope window is saving
  bookmarks; try again in a moment." Residuals: the export's end line does not repeat a re-run result set's id; a step
  and its query are joined by order, not a trace id; removals at the step cap are not logged; a negated module term is
  counted twice in the filter's shape (support).
