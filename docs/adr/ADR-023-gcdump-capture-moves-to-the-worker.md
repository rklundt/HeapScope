# ADR-023 gcdump capture moves to the worker (added in sprint 1.9)

- Context: gcdump capture (sprint 1.8) runs the vendored dotnet-gcdump code in the process that asks for it, the UI or
  the CLI, and builds PerfView's in-memory graph there before writing the file. The UI process is meant to stay small
  and responsive (ADR-007, ADR-019).
- Measurement (sprint 1.9, Release CLI, LeakyConsole with the static cache leak): about 4 million objects peaked at
  381 MB private in the capturing process (10 s, a 65 MB gcdump); about 9 million objects, near the writer's 10 million
  node cap, peaked at 1,191 MB (a 149 MB gcdump).
- Decision: above the 1 GB threshold set before measuring (sprint 1.9 decision 3), so in Phase 2 gcdump capture runs in
  the worker: the front end asks the worker to capture, as it asks it to load; the worker's memory budget and
  out-of-process isolation then cover the in-memory graph. Raising the 10 million node cap waits for that move. Until
  then capture stays where it is, bounded by the cap.
- Measurement caveats: one run per size, in the CLI (the UI process starts larger); memory grew faster than the object
  count (2.25 times the objects, 3.1 times the memory), consistent with the graph's arrays doubling; a capture refused
  for the node cap reaches the cap in the capturing process first.
- Consequences, each a decision for the Phase 2 sprint that moves it (sprint 1.9 review):
  - Layering: the worker gains a reference to ThirdParty.GcDump (or a capture assembly) and the diagnostics client
    package; the table in 3.1 and the architecture test change with it.
  - Contract: a capture RPC with progress and cancellation, the failure kinds of `GcDumpCapturer` as error codes, and a
    protocol version bump.
  - Priority: the worker runs below normal priority with capped Server GC heaps, while the session's lossy buffering on
    .NET 10 loses events when the reader falls behind; the capture runs on a normal-priority thread, or in a short-lived
    child process of the worker.
  - Isolation: a capture that crashes the worker takes the open snapshots with it (ADR-007 the other way round); a child
    process avoids that. The choice is made with the measurement of a capture inside the worker.
  - Memory: the capture's graph competes with loaded snapshots for the budget; it takes the load gate and its memory is
    checked against the budget like a load.
  - Elevation: the worker inherits the front end's token, so it can capture what the front end could (ADR-014).
  - Schedule: with the gcdump scale item before any 2.x sprint that needs gcdumps above the cap.
  Capture keeps process discovery and full dump capture in the front end, where the runtime writes the file itself.
