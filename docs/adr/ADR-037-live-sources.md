# ADR-037 Live sources (added in sprint 3.8, amends ADR-023)

- Context: Phase 4's live session shows a running process's allocations as they happen, beside its counters. Today
  (peer review P6) counters and the allocation trace run in the front end: `EventPipeCounterSource` samples counters
  (ADR-021), and a monitoring run records the allocation EventPipe session to a `.nettrace` file in the session folder
  through Capture (`AllocationRecording`). The worker reads only finished trace files with TraceEvent (ADR-028) and can
  send nothing to a client but progress. Three placements were open: parse the live stream in the front end (it would
  hold TraceEvent's parsing and the stacks, which ADR-007 and ADR-028 keep out of the UI process); run the EventPipe
  session in the worker (the dependency table keeps Capture and the diagnostics client out of the worker, and a target
  that misbehaves would take the open snapshots with it); or keep recording in the front end and let the worker read
  the file while it grows.
- Spike (sprint 3.8, `perf/HeapScope.Benchmarks` `live-tail`): one process records a runtime EventPipe session (GC and
  Type keywords at Verbose) to a `.nettrace` file. A reader tails the growing file with TraceEvent's
  `EventPipeEventSource` over a stream that waits at the end of the file until the recording ends. The target allocates
  in a tight loop, a heavier rate than a real application. Measured on the 16 GB developer laptop:

  | Recorded | Allocation ticks read | Events lost | File | Delay, event to reader (p50 / p95 / max) | Reader peak working set |
  |---|---|---|---|---|---|
  | 10 s | 361,455 | 0 | 46 MB | 179 / 234 / 400 ms | 59 MB |
  | 30 s | 1,143,130 | 0 | 145 MB | 179 / 233 / 599 ms | 97 MB |

  The delay is the session's buffer flush (the runtime writes about every 100 to 200 ms) plus the reader's 20 ms poll; it
  does not grow with the recording. A reader that reaches the end before the writer flushed its last bytes fails with
  "read past end of stream", so the reader may only stop after the writer has flushed and said it is done.
- Decision:
  - The front end keeps every EventPipe session: counters (cheap, already there) and the allocation recording, which
    writes the `.nettrace` file into the session folder as today. The file stays the record of the run (ADR-028), and a
    process that misbehaves never touches the worker.
  - The worker reads a live trace by tailing that file, as a resource of its own beside finished traces (a live trace
    handle, reopened like any other through `WorkerResources`). It keeps aggregates, not every tick: types and sizes per
    time bucket, and stacks folded as they arrive. Its memory is reserved like any other work. Methods JIT-compiled
    before the session are named only when the rundown arrives at the end; until then their frames show as
    unresolved.
  - The front end asks for what changed since a time it already has: a request and an answer, as every call on the
    worker today. No push channel from the worker, no protocol for streaming.
  - The front end tells the worker when the recording ended, after the file is flushed; only then does the worker stop
    waiting at the end of the file.
  - gcdump capture stays in the front end (ADR-023 amended below).
- Consequences: a live view is about a quarter of a second behind the process, which is enough for a chart and a type
  list. The worker gains no dependency (TraceEvent is already in Loaders.Trace). The session folder holds the live
  trace while it is read, so a run's allocations survive a worker restart: the worker reads the file again. Building
  the live allocations view is Phase 4 work; this ADR fixes where it runs. The spike is measurement code only and does
  not ship.
