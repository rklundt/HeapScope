# Review: Sprint 1.7 Live counters and timed snapshots

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, full change set since `sprint-1.6`). Phase 1 runs in
autonomous mode: Moderate findings were decided by the lead engineer and are marked "decided autonomously".

Totals after dedupe: 3 High, 16 Moderate, 17 Low (findings raised by more than one reviewer are merged and keep the
highest severity).

## High / Urgent

- **H1 The runtime's megabytes are decimal** (gc-specialist, solution-architect, senior-dev). `gc-heap-size` and
  `working-set` are published as bytes / 1,000,000; they were converted with 1024 x 1024, so every heap value, rule
  threshold, baseline, CSV cell and card was about 4.9 percent high. **Fixed:** decimal conversion, with a comment;
  `CounterSampleBuilderTests` expects the decimal values. Status: fixed; confirmation below.
- **H2 Size rules fired and re-armed on garbage** (gc-specialist). The rule metric was `gc-heap-size`, the heap now
  including everything allocated since the last GC, which rises and falls by the gen0 budget between collections: a rule
  could fire on garbage and, with `below`, fire again after every GC. Measured on .NET 10 (LeakyApi under load): the
  generation sizes change only at a GC (gen0 always 0 under regions), `gc-heap-size` climbs about 14 MB a second in
  between, `time-in-gc` stays at the last GC's share. **Fixed:** rules compare the heap after the last GC
  (`HeapSample.RuleBytes`: the generation sizes summed, or the heap now before the process had any GC), re-arm only
  under 90 percent of the threshold, take the first sample's metric as the baseline; GC pressure averages only
  intervals in which a GC ran; the chart stacks the generations as of the last GC with "allocated since the last GC"
  on top, so its top is the heap now. Tests: `Garbage_allocated_since_the_last_gc_never_fires_a_size_rule`,
  `A_rule_rearms_only_under_90_percent_of_its_threshold`,
  `Before_any_gc_the_metric_is_the_heap_now_and_pressure_counts_only_intervals_with_a_gc`; the integration growth
  rule still captures exactly one real dump. Status: fixed; confirmation below.
- **H3 A canceled rule capture broke the run's teardown** (senior-dev; solution-architect as Moderate). A capture
  canceled from the jobs panel or at app close, or a throwing listener, escaped `CaptureAsync`: no record, no marker,
  the timeline left open, the run reported as failed (the CLI exited 130). **Fixed:** `CaptureAsync` records a canceled
  capture ("Canceled.") and any other failure (logged) and never throws; events are raised through `Raise`, which logs
  a throwing listener; teardown waits for the capture without rethrowing and closes the timeline in its own try.
  Tests: `A_canceled_rule_capture_is_recorded_and_the_run_ends_normally_with_its_timeline_closed`,
  `A_listener_that_throws_does_not_end_the_run`. Status: fixed; confirmation below.

### Confirmations

Each fix went back to the reviewers that raised it; all were confirmed on re-reading the code and tests.
- H1: confirmed by gc-specialist, solution-architect and senior-dev.
- H2: confirmed by gc-specialist (the after-GC metric, the 90 percent re-arm, pressure from intervals with a GC, the
  chart's top layer; `HadGc` on the gen0 count is right because it includes higher-generation GCs).
- H3: confirmed by senior-dev (no escape from the capture, listeners isolated, teardown never throws, the stop bounded,
  monitoring stopped before jobs at close).

## Moderate (decided autonomously)

- **M1 Samples after Stop still ran the rules; the cut-short last interval read as zeros** (solution-architect).
  **Fix now:** no rules after Stop; a sample without the GC heap size is dropped. Test:
  `Samples_while_a_capture_runs_take_no_second_snapshot_and_none_after_stop`.
- **M2 Closing the app could hang on a rule capture started after the jobs were canceled** (solution-architect).
  **Fix now:** the Live run is stopped before running jobs are canceled.
- **M3 Timeline file: one-second names overwritten, a warning per sample after a failure, the session could change
  mid-run** (solution-architect, senior-dev). **Fix now:** created new with a numbered suffix, the session folder
  pinned when the run starts, writing stops after the first failure with one warning (`TimelinePath` null then).
  **Deferred** to the backlog: a `timelines` entry in `session.json` (run, process, interval, rules, baseline), snapshot
  ids in the markers and a format line in the CSV.
- **M4 Manual and rule captures of one target could overlap** (solution-architect). **Deferred** to the backlog: one
  capture per target across the Processes page and monitoring runs (`CaptureService` serializes per process).
- **M5 A throwing listener ended the run** (senior-dev). **Fixed with H3.**
- **M6 Stopping could hang on a frozen target** (senior-dev). **Fix now:** the EventPipe stop is bounded at 5 seconds,
  then the session is disposed, which ends the stream.
- **M7 Zero thresholds captured back to back** (senior-dev). **Fix now:** rules refuse zero sizes and percents; the CLI
  validates `--above`, `--growth` (above zero) and `--max` (1 or more); the Live page says so.
- **M8 The CLI test could leak before the baseline** (senior-dev). **Fix now:** it waits for two samples first.
- **M9 Monitor tests never sent samples during a capture, nor canceled one** (senior-dev). **Fix now:** gated capture,
  cancel and throwing listener tests.
- **M10 Time in GC is the last GC's share, not a per-interval mean** (gc-specialist). **Fixed with H2** (averaged over
  intervals with a GC).
- **M11 Missing counters read as zero** (gc-specialist). **Fix now (test):** the integration test asserts gen2 and LOH
  sizes are published. **Deferred** to the backlog: a warning when an expected counter never arrives.
- **M12 ADR-021 does not weigh the System.Runtime Meter** (gc-specialist). **Fix now (documentation):** ADR-021 records
  the trade-off; **deferred** to the backlog: a Meter-based `ICounterSource` for .NET 9 and later targets, EventCounters
  kept for .NET 6 to 8.
- **M13 The baseline was one point of the sawtooth** (gc-specialist). **Fixed with H2** (the after-GC size).
- **M14 Chart layers and the rule metric differed** (gc-specialist; architect as Low). **Fixed with H2** (the stack's top
  is the heap now; the legend says the generations are as of the last GC).
- **M15 A capture started inside the run's lock** (senior-dev, solution-architect as Low). **Fix now:** decided under the
  lock, started on the thread pool.
- **M16 Old runs and token sources in the Live page** (senior-dev, Low folded in). **Deferred** with the Lows below.

## Low (listed only)

- L1 `gen-0-gc-count` includes gen1 and gen2 collections (and gen1 includes gen2); the "GCs 0/1/2" display counts a GC
  more than once (gc-specialist).
- L2 Samples are stamped when read, not with the event's time stamp; the first interval's increments may count from
  process start (gc-specialist).
- L3 All keywords of `System.Runtime` are enabled and the session buffer is 16 MB; a non-integer interval is truncated
  (gc-specialist, solution-architect).
- L4 The chart allocates per sample per redraw (gc-specialist, senior-dev).
- L5 The capture kind is not a monitoring option yet (gcdumps by rules, 1.8); 2.5 needs its own session or provider
  (solution-architect).
- L6 The architecture layout line still lists timed snapshots under Capture (solution-architect).
- L7 Previous runs are not disposed when a new one starts; `Stop` after `DisposeAsync` throws (senior-dev).
- L8 "Stopped after N samples" counts the chart window, not the whole run (senior-dev).
- L9 `TimelinePath` was read without the lock (senior-dev; fixed in passing with M3).
- L10 The CLI's `--max` accepted zero (senior-dev; fixed in passing with M7).
- L11 Stop before the session connects was logged as a failure (senior-dev; fixed in passing).
- L12 The rule capture started under the lock (folded into M15).
- L13 The smoke run's one stall: a Live smoke run after the unit tests measured a 491 ms lag with a longest operation of
  22 ms; the next two runs measured 20 and 80 ms (another sample for the 0.3 backlog item on lag under load).
- L14 Step mode fires once per step on consecutive samples after one large jump (senior-dev).
- L15 The documentation named the timeline `counters-<time>.csv` (now `counters-<pid>-<time>.csv`, fixed in passing).
- L16 (raised while confirming) When the first sample comes before the target's first GC, the baseline is the heap
  now (garbage included), so a growth rule may fire later than expected once the metric switches to the after-GC size
  (gc-specialist).
- L17 (raised while confirming) The timeline writer is disposed inside the failure catch and could throw on flush
  (senior-dev).
