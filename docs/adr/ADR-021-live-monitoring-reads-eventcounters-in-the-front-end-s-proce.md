# ADR-021 Live monitoring reads EventCounters in the front end's process (added in sprint 1.7)

- Context: the "load, snapshot, more load, snapshot" workflow needs the heap's size over time and snapshots taken by
  rules, without installing tools on the target or the machine.
- Decision: the runtime's `System.Runtime` EventCounters are read over an EventPipe session by the diagnostics client
  HeapScope already uses (ADR-010), in the process of the front end that monitors, like capture; the worker is not
  involved. Front ends see Application's `HeapSample`, not Capture's types. Rules are a pure engine over the samples'
  own times (testable without a clock); a rule capture is a normal capture recorded as `Timed` with the rule as its
  label, at most one at a time. Each run writes its samples and capture markers to a CSV in the session folder.
- Rule metric (review H2): the heap after the last GC (the generation sizes, which change only when a GC runs), not
  `gc-heap-size` (the heap now, garbage since the last GC included, rising and falling by the gen0 budget); before the
  process had any GC, the heap now. The runtime's megabytes are decimal. Time in GC is the last GC's share, so GC
  pressure averages only intervals in which a GC ran.
- Alternatives: the `System.Runtime` Meter (.NET 9 and later) has better defined metrics (after-collection heap size
  per generation, cumulative pause time) and is what Microsoft recommends now; EventCounters remain published by .NET
  10 and cover .NET 6 to 8 as well, so they come first. A Meter source behind `ICounterSource` is a backlog item.
- Consequences: any .NET 6 or later process can be monitored without changes; counters are per-interval aggregates
  (no per-allocation data; allocation tracking is sprint 2.5, with its own session or provider); the sample interval
  is at least one second.
