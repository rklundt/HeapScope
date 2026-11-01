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
- Spike (sprint 3.8, `perf/HeapScope.Benchmarks` `live-tail`): one process records the allocations profile HeapScope
  uses (the runtime provider's GC, Loader and JIT keywords at Verbose, with rundown) to a `.nettrace` file; a reader
  tails the growing file with TraceEvent's `EventPipeEventSource` over a stream that waits at the end of the file until
  the recording ends. The target allocates in a loop, flat out or pausing a millisecond every 200 allocations (about 70
  allocation ticks a second, an application under load). Measured on the 16 GB developer laptop:

  | Rate | Writer buffer | Ticks read | Lost | Delay, event to reader (p50 / p95 / max) | Peak working set |
  |---|---|---|---|---|---|
  | flat out, 15 s | 64 KB (as `TraceRecorder`) | 394,841 | 0 | 181 / 235 / 925 ms | 62 MB |
  | about 70 ticks/s, 15 s | 64 KB (as `TraceRecorder`) | 1,078 | 0 | 2,666 / 5,062 / 5,508 ms | 45 MB |
  | about 70 ticks/s, 15 s | none (each chunk written at once) | 1,079 | 0 | 157 / 217 / 240 ms | 45 MB |

  An earlier run with the GC and Type keywords and no rundown, flat out for 10 and 30 s, gave the same delay (179 ms
  median) and 1,143,130 ticks in 30 s with none lost. The delay is the runtime's buffer flush (100 to 200 ms) plus the
  reader's 20 ms poll when every chunk reaches the file at once; behind a 64 KB write buffer a realistic rate waits
  seconds for the buffer to fill. A reader that reaches the end before the writer flushed its last bytes fails with
  "read past end of stream", so it may stop only after the writer has flushed and said it is done.
- Not measured, and gates for Phase 4's live session (3.8 review): the reader ran in the recording process, so the
  memory figures include the writer; the reader counted ticks and resolved no stacks, so the cost of folding stacks as
  they arrive, in a separate worker process, is unknown. The first Phase 4 sprint measures both before building on
  this decision.
- Decision:
  - The front end keeps every EventPipe session: counters (cheap, already there) and the allocation recording, which
    writes the trace into the session folder. The file stays the record of the run (ADR-028), and a process that
    misbehaves never touches the worker.
  - A live recording writes each chunk of the session's stream to the file as it arrives (no write buffer), which the
    table shows is what keeps a live view a quarter of a second behind at a realistic rate.
  - The worker reads a live trace by tailing that file, as a resource of its own beside finished traces. It keeps
    aggregates, not every tick: types and sizes per time bucket, and stacks folded by instruction pointer (module and
    offset), named when names are known. Methods JIT-compiled before the session are named only by the rundown at its
    end, and a session attached to a warmed-up process has almost all of its frames in that state, so names cannot be
    the fold key; a short rundown-only session at the start that names them up front is to be measured. Its memory is
    reserved like any other work.
  - The front end asks for what changed since a time it already has: a request and an answer, as every call on the
    worker today. No push channel from the worker.
- Consequences, the contract Phase 4 builds (3.8 review):
  - The live file: written under its final name (no partial file and rename while live), shared for reading and
    deleting while it is written, owner-only from its creation as every file HeapScope writes (P15); a completion
    marker or the front end's notice says it ended. The tailing reader opens it with read, write and delete sharing so
    it never blocks the writer.
  - "The recording ended" is a new worker contract method (the front end's notice, sent after the file is flushed), so
    the live session is one protocol bump.
  - A live trace has its own identity for reopening after a worker restart: its path and creation time, with the file
    only ever growing. The finished-trace rule (the same size and write time, `TraceChanged` otherwise) does not apply
    while it grows.
  - A live view is about a quarter of a second behind the process. The worker gains no dependency (TraceEvent is
    already in Loaders.Trace). The session folder holds the live trace while it is read, so a run's allocations survive
    a worker restart. Building the live allocations view is Phase 4 work; this ADR fixes where it runs. The spike is
    measurement code only and does not ship.
  - gcdump capture stays in the front end (ADR-023 amended).
