# ADR-028 Allocation traces read in the worker from EventPipe allocation ticks (added in sprint 2.5)

- Context: 2.5 shows where allocations come from without an agent or dotnet-trace. The runtime's `GCAllocationTick`
  event fires about every 100 KB allocated on a heap, with the type, the bytes since the last one and, on EventPipe, a
  call stack; .NET 10 also has randomized allocation sampling, which older runtimes lack. Resolving stacks needs
  TraceEvent's `TraceLog`, which converts the trace into an `.etlx` file and holds it in memory, several times the
  trace's size (ADR-007 keeps such data out of the front end).
- Decision: a new trace profile records allocation ticks (GC keyword at verbose, with loader and JIT events, rundown on
  stop) for any .NET 6 or later process. The worker reads traces through `Loaders.Trace`, keeping only compact arrays:
  frames by name, a deduplicated stack tree, types, and per sample its time, innermost stack node, type and bytes. Sizes
  are estimates and marked so (`ValueBound.Estimate`). Queries take an interval and optionally a type; trees are served
  flattened from open keys like ADR-027. The `.etlx` lives in a private temporary folder for the read only.
- Alternatives: randomized sampling (better statistics, .NET 10 only; Backlog); reading in the front end (simpler, but
  hundreds of megabytes in the UI process); keeping `TraceLog` open (no re-read after a restart, but many times the
  memory).
- Consequences: sampled bytes favor large and frequent allocations; small, rare ones may not appear. A trace is read
  again after a worker restart (seconds for typical traces), once for all the queries that find it gone, and only when
  the file's size and write time are unchanged. Linking a trace to the snapshots it lies between (Compare's "allocated
  between") needs both on one clock in the session record: Backlog.
- Amended in the 2.5 review: EventPipe walks managed frames only, so a frame without a name is a managed method the
  trace does not name (usually the recording ended before the rundown); it is labeled `[unresolved frame in <module>]`,
  never merged with its neighbors, and counted; whether the rundown arrived and the problems TraceEvent reported while
  converting are part of the result and its notes. Each sample keeps its kind (small, large or pinned object heap; the
  runtime ticks per heap and kind). Types are told apart by name: two types of one name in different load contexts
  share a row, and joining trace types to a diff's rows (name and module) needs the tick's type id and the module, kept
  with the Compare item. TraceEvent's conversion cannot be canceled: a cancel takes effect when it returns (the job's
  status says so); reading the samples after it checks the token every 4,096 events.
