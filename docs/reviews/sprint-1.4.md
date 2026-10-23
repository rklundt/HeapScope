# Review: Sprint 1.4 Snapshot diff

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-1.3`). Phase 1 runs in
autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided autonomously".

Totals after dedupe: 0 High, 13 Moderate, 27 Low (Lows that repeated a Moderate are folded into it).

## High / Urgent

None. All three reviewers checked layering, leases on both snapshots, the known-answer test, and that the refactor of
the types grid onto the shared base kept every 1.3 lifecycle fix (latest query wins, stale errors dropped, replaced sets
released, `Show(null)`, page retry).

## Moderate (decided autonomously)

- **M1 A stale load context could hide in a summed diff row** (gc-specialist; architect as Low). Method tables sharing
  a type name and module are summed (ADR-018), so a plugin context that failed to unload next to its reloaded copy
  could net to zero. **Fix now:** each row carries the distinct types per side (`BaselineTypes`, `CurrentTypes`), and a
  type row whose number of copies changed is marked "copies" (not for namespace or assembly rows, which gain types
  normally). Shown in the Compare grid with a header tooltip. ADR-018 records the summing and the marker. Test:
  `A_stale_copy_of_a_reloaded_plugin_is_flagged_even_when_the_summed_counts_do_not_grow`.
- **M2 Uncollected garbage in the CI gate** (gc-specialist). **Fix now (documentation):** `--fail-on-growth` help,
  DEVELOPER.md and ADR-018 say both dumps count unreachable objects and that a stable gate captures after a full GC in
  the target. **Deferred:** growth by generation (`--growth-of gen2` or per-kind delta columns), a per-row gate, and an
  induced-GC capture option, to the backlog.
- **M3 A filter that matches nothing passed the gate** (gc-specialist, architect). **Fix now:** with `--fail-on-growth`,
  a `--filter` that matches no rows exits 1 with "matches no rows". Test in `Cli_diff_prints_formats_and_exits_by_the_growth_threshold`.
- **M4 The threshold had no unit tests; non-finite and overflowing values passed** (all three). **Fix now:**
  `GrowthThreshold` moved to Application (`Snapshots/GrowthThreshold.cs`) with `IsExceededBy`; parsing refuses
  negative, non-finite, exponent and out-of-range values; the zero-baseline rule (any growth exceeds a percent) is
  documented in the help and DEVELOPER.md. Tests: `GrowthThresholdTests` (units, spaces, percent, refusals, growth at
  the limit passes and one byte more fails, zero baseline).
- **M5 Snapshot notes not shown by `diff`** (gc-specialist). **Fix now:** each side's notes (incomplete segments,
  dropped references) go to stderr labeled "Baseline" or "Current", and into the JSON (`BaselineNotes`, `CurrentNotes`);
  `summary` prints its snapshot's notes too.
- **M6 No drill-down key on diff rows** (architect). **Deferred to 1.5's scope** (recorded there): the instances query
  is what consumes the key, so the key (type name plus module file name, or per-snapshot type indices) is designed with
  it, for summary and diff rows alike.
- **M7 Estimated counts would look exact** (architect). **Fix now:** `TypeDiffResult.ExactCounts` (both sides have
  `ExactCounts`); the CLI warns and the Compare totals say "estimated" when it is false. 1.8 (gcdumps) is when it
  becomes reachable; ADR-018 records it.
- **M8 The shared grid base is specific to type grids; `IndexOf` scanned all rows** (architect). **Fix now:** rows
  implement `IIndexedRow`, so `IndexOf` and `Contains` are constant time. **Deferred to 1.5's scope:** splitting the base
  into a query-lifecycle core and a type-grid layer with column-name sorting, and the bounded page window (both needed
  by 1.5's instance grids, moved there from the backlog).
- **M9 An empty picker stayed empty** (senior-dev; architect as Low). **Fix now:** when one side is empty, it takes the
  nearest other capture (the first after the baseline, or the latest before the current); captures without a time count
  as the newest, as the comment now says. Test: `An_empty_picker_takes_the_next_open_snapshot_and_compares_again`.
- **M10 Compare had no async edge-case tests** (senior-dev). **Fix now:**
  `Closing_a_compared_snapshot_while_its_comparison_runs_leaves_the_view_empty`,
  `Only_the_latest_of_quick_sort_changes_owns_the_comparison`, `Estimated_counts_and_reloaded_copies_are_shown`; the
  snapshots view model is disposed by the tests.
- **M11 "Both snapshots must be loaded" and the reopen branch of `UseBothAsync` untested** (senior-dev). **Fix now:**
  `A_snapshot_the_worker_lost_is_reopened_and_an_unknown_one_is_refused` (the worker forgets one snapshot in the same
  generation and the session reopens it; an unknown id fails with `SnapshotNotLoaded`; a closed handle is refused).
- **M12 `summary` lagged behind `diff` in the CLI** (senior-dev). **Fix now:** `summary` uses the shared open and fetch
  helpers, validates `--top` (1 or more) and refuses `--top` with `--all` (exit 64), and releases after writing its
  output. Both commands share the option builders.
- **M13 First show of the Compare tab over budget (97 to 238 ms, cold)** (senior-dev). **Deferred:** a one-time cold
  cost like the first window render and the first job row (ARCHITECTURE.md 8), tracked by the backlog's UI cold start
  item; warm re-queries stay at 64 to 74 ms and the smoke run checks them.

## Low / Trivial (not fixed)

- The objects-delta cell is colored by the bytes delta's sign (architect, senior-dev).
- `--smoke-compare` without `--smoke-open` is ignored (architect, senior-dev).
- An unknown sort column is detected after both aggregations; the message carries "(Parameter 'request')" (architect).
- CLI table headers "Objects" and "Bytes" are the current values (architect).
- The WorkerProtocol comment stops at version 3 (architect).
- The same snapshot can be picked on both sides without a hint; Swap has no CanExecute (architect, senior-dev).
- "No type matches the filter" shows without a filter (senior-dev).
- "A grid query failed" does not name the grid (senior-dev).
- The CLI table reads cells by position (senior-dev).
- `diff` shows no progress and has no ascending sort (senior-dev).
- Diff progress assumes both aggregations run; aggregations run one after the other (gc-specialist, senior-dev).
- `Path.GetFileName` and `GetModule` computed per type where unused (gc-specialist, senior-dev).
- The known-answer test checks the leading type's delta equals the maximum, not strictly first (senior-dev).
- The worker still exits 2 on usage errors; decision 5 is CLI-only (senior-dev).
- Several public types per file in the new view model files (senior-dev).
- Unresolved type names (`<unknown type 0x...>`) and renamed module files split into gone plus new rows (gc-specialist).
- In-memory and dynamic modules share "unknown module" (gc-specialist).
- No warning when the baseline was captured after the current dump or the processes differ (gc-specialist).
- `MatchingBaselineBytes` and `MatchingBytesDelta` recomputed per access (gc-specialist).
