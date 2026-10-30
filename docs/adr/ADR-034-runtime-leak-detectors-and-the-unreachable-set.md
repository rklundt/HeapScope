# ADR-034 Runtime leak detectors and the unreachable set (added in sprint 3.5)

- Context: 2.6's detectors name application patterns (static collections, events, timers, disposal); dotMemory also
  finds what the runtime holds for an app that misuses it (waiting async calls, a stuck finalizer), shows types that
  grow, and opens the garbage a dump still holds.
- Decision: three leak detectors over the graph. `pending-async-methods` counts reachable `AsyncStateMachineBox<T>`
  objects (and the debugger's `DebugFinalizableAsyncStateMachineBox<T>`) whose task has not ended (`m_stateFlags`, so
  full dumps only), by async method, from 1,000 calls; the framework's own methods wait by design and are left out and
  logged. `finalizer-backlog` counts the objects the finalizer queue roots (ready for finalization), from 1,000, with a
  row per type of at least 100. `type-growth`, with a baseline, counts reachable objects per type key (ADR-018: name and
  module), framework types (by namespace or module) left out, from 1,000 more and 50% more; both snapshots must have a
  complete graph and exact counts (full dumps: a gcdump names nested types differently and may be sampled), and a
  baseline captured later is not compared. Each row opens its objects as a set. `ObjectSetKind.Unreachable` is the
  complement of `Reachability.FromPathRoots`, the retention views' roots (ADR-024, finalizer queue included), so no
  member has a path or retains anything; the leak detectors keep `Reachability.For`, which does not start from the
  finalizer queue (objects waiting for their finalizer are dead for them). Refused for gcdumps and incomplete dumps; its
  log line says how much only the finalizer queue holds. Protocol 19.
- Consequences: async calls that wait by design (requests in flight, background loops) count too, so the note says to
  compare with the work in flight. A blocked finalizer thread is named by its effect, not its stack (which needs a
  debugger). Type growth by type misses growth in framework types, which the growing static collections detector and
  Compare cover; type growth counts young objects too, so load alone can add some (old-generation growth is in the
  Backlog). Pooled `ValueTask` boxes have no task state and are not counted; methods compiled with .NET 10's opt-in
  runtime async have no box. Objects finalized between two snapshots are not reported: it needs the address match
  (ADR-032) of the baseline's finalizable objects not yet run, and is in the Backlog.
