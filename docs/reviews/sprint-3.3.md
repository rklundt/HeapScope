# Review: Sprint 3.3 Comparison depth

Changes since `sprint-3.2`. Four perspectives, run in parallel: solution-architect, senior-dev, gc-specialist, and a
support engineer (a general reviewer with a read-only brief, so the logs can back up any result a user contests).
Moderates follow the lead engineer's recommendation (product owner, 2.3.7: "Go with your reco for moderate
recommendations"), marked "decided autonomously". None of the reviewers had a shell; each read the files directly.

Totals after dedupe: 5 High, 14 Moderate, 25 Low (5 from the re-review). A finding raised by more than one reviewer is merged and keeps the
highest severity.

## High / Urgent

- **H1 A test of every set kind missed the new `Compared` kind** (senior-dev). The fast suite failed. Fix: the test
  covers `ComparedOf`, and its cache key is checked for uniqueness with the others. Status: Fixed; confirmation below.
- **H2 Objects were matched for snapshots decision 1 excludes** (senior-dev, solution-architect). Only addresses and the
  process id were checked, so gcdumps, a full dump against a gcdump (names mapped in the rows but not in the match) and
  incomplete dumps were matched. Fix: both snapshots must be complete full dumps; every refusal says why and that
  comparing by type still works. Status: Fixed; confirmation below.
- **H3 An address reused by another object counted as a survivor** (gc-specialist, solution-architect). Only the type
  key was compared. Fix: a survivor also needs the same method table, size and heap area (small object heap, LOH, POH,
  frozen), and no younger generation; the match counts addresses reused by other objects. Test:
  `ComparisonTests.An_address_reused_by_another_size_a_younger_generation_...`. The remaining risk (a new object of the
  same type and size at a reused address) is stated in ADR-032. Status: Fixed; confirmation below.
- **H4 The per-row growth gate's log line was false** (support engineer). It logged the largest delta "from 0 bytes"
  with a 0 byte limit and no rows. Fix: per row, the log names each row over its limit (up to 20) with its baseline and
  limit, and the largest row; summed per kind, the kind; stderr names the kind. Status: Fixed; confirmation below.
- **H5 Two processes reusing an id were matched** (gc-specialist and solution-architect as Moderate, raised to High with
  H2 because it gives confident wrong results). Fix: a match whose baseline frozen objects are mostly not at their
  addresses is refused as "not one run" (frozen objects never move or die in one run); the match log names both sides'
  process, kind and capture time, the rule that decided, survivors, reused addresses and the frozen count. Test:
  `ComparisonTests.Frozen_objects_say_whether_two_dumps_are_one_run_of_a_process`. Status: Fixed; confirmation below.

### Confirmations

Each reviewer re-read the fixes of the findings it raised (read only):
- senior-dev: H1 and H2 confirmed; one new Low (cancellation in the frozen count loop) fixed at once.
- gc-specialist: H3 and H5 confirmed; four Lows (demotion, the frozen guard before .NET 8, an untested heap area case,
  the ADR's generation wording) fixed in the ADR and a test.
- solution-architect: H2, H3 and H5 confirmed, and the Moderates as described; two Lows recorded (L21).
- support engineer: H4 confirmed; the per-row baseline label fixed, the "nearest to its limit" row listed (L22).

## Moderate (all decided autonomously)

- **M1 What New, Dead and Survived mean was understated** (gc-specialist, support engineer). Fixed: ADR-032 states that
  Gen0 and Gen1 collections move survivors, where Survived is trustworthy, the reuse risk and that garbage counts; the
  column tooltips, set titles ("New by address: ...") and notes say "by address"; a caution comes with a match where
  under a tenth of the objects kept their address.
- **M2 Capture advice conflicted** (solution-architect, support engineer). Fixed: DEVELOPER.md and the gate's help say a
  forced GC suits the growth gate but not New and Dead; ADR-018 is marked amended by ADR-032.
- **M3 A percent with a heap kind was accepted per row** (senior-dev, solution-architect, gc-specialist, support
  engineer). Fixed: refused in every mode (exit 64).
- **M4 Sort columns of options not given failed late** (senior-dev). Fixed: the CLI refuses `--sort new|dead` without
  `--match-objects` and `--sort retained-delta` without `--retained` (exit 64); Compare falls back to bytes for a column
  the query will not have.
- **M5 The diff JSON and stderr left out where numbers came from** (senior-dev, support engineer). Fixed: the JSON has
  the identity, New and Dead totals, kind deltas, both dominators and a lower bound flag; stderr prints both sides'
  retained notes.
- **M6 The worker's diff log held totals only** (support engineer). Fixed: the first 10 rows with every optional column.
- **M7 A trend mixed snapshot kinds** (senior-dev, gc-specialist, solution-architect). Fixed: refused with the reason.
- **M8 The Trend view went blank over 20 snapshots and did not say its order** (support engineer, senior-dev). Fixed:
  a hint, an order note when capture times cannot order them, the order logged with capture times, progress shown, the
  double query on a tick removed, the filter box named so Ctrl+F reaches it.
- **M9 `Bind` wrote the other snapshot into an inspection row inside a compared set** (solution-architect). Fixed in part:
  the CLI refuses `--inspection` with `--compared-with`; per-node binding is in the Backlog.
- **M10 The retained delta was marked a lower bound** (solution-architect). Fixed: an estimate (a difference of lower
  bounds); summing a type's method tables' bounds is in the Backlog.
- **M11 Test gaps** (senior-dev). Fixed: `Bind` of compared specs, moved and reused objects, a per-kind gate that must
  fail, the column header checked case sensitively, refusals in the CLI.
- **M12 Matching runs again for each query and set** (solution-architect). Deferred: an identity per snapshot pair kept
  with the snapshots, and a Medium measurement of a matched and retained diff, are in the Backlog.
- **M13 The CLI trend keeps every dump open at once** (gc-specialist). Deferred to the Backlog (aggregate and close each
  dump in turn; measure a 20-dump trend on Medium).
- **M14 Refusal messages lacked next steps** (support engineer). Fixed: each says what still works and what to change.

## Low (listed, not raised)

1. No Frozen delta column per row; a row's kind deltas need not add up for strings (gc-specialist).
2. The reservation ends before the match's masks stop being used (small; gc-specialist).
3. A gcdump pair was told "different processes" (fixed with H2).
4. `UseBothAsync`'s summary sat above the trend method (fixed).
5. `Toggle` queried twice; `Start` queried every time (fixed with M8).
6. The trend log's `{Paths}` held snapshot ids (fixed).
7. The CLI trend lacked notes, order and duplicate checks (fixed: notes, an order warning, exit 64 for a repeated dump).
8. `spec.Change` was not range checked and the enum was cast (fixed: checked and switched).
9. The interface plus retained refusal came after matching (fixed: before any work).
10. "Swap them" did not say what to swap for a set (fixed).
11. `ObjectChange` has no mirror test in `ContractMirrorTests` (solution-architect).
12. ADR-029's "no identity across dumps" wording not updated (solution-architect).
13. The New and Dead checkbox is not disabled from the descriptors (solution-architect).
14. Trend offers Interface for gcdumps and shows no group note (support engineer).
15. CLI validator refusals are not logged (support engineer).
16. `TypeDiff.Collect` allocates per-kind arrays when not asked; double blank lines; `TrendRow` record equality by
    reference (senior-dev).
17. Addresses reused by other types are counted (fixed with H3: `ReusedAddresses`).
18. The capture order uses file times when that is all there is (logged; solution-architect).
19. `TypeTrendRequest` has no round trip test (senior-dev).
20. Retained bytes summed across a type's method tables can double count (gc-specialist, solution-architect).
21. (Re-review) the frozen-object guard needs .NET 8 or later (stated in ADR-032); the integration test's ClrMD check
    matches by address only, not size and generation like the product (solution-architect).
22. (Re-review) the frozen count loop had no cancellation check (fixed); the per-row gate log read the row's total
    baseline as the kind's (fixed: "row baseline"); the gate logs the largest row, not the one nearest its limit
    (senior-dev, support engineer).
