# ADR-020 Inspections are Analysis plugins with declared capabilities (added in sprint 1.6)

- Context: dotMemory's inspections (duplicate strings, sparse arrays, finalizable objects, fragmentation) are its
  quickest route to a finding, and Phase 2's leak detectors (sprint 2.6) take the same shape. Some need only the graph,
  others read values from the dump; gcdumps (1.8) cannot answer the latter.
- Decision: an inspection is an `IInspection` in Analysis: a CLI name, a title, a one-line cause and fix, the
  capabilities it requires, and a run over the graph and, when available, the typed reader. Its rows are a typed table
  formatted per page; hidden columns (`TypeName`, `Module`, `Scope`, or an `Address`) say what a row opens. The worker
  lists inspections with the reason each unavailable one cannot run and refuses to run those
  (`CapabilityNotSupported`); front ends show them as unavailable, never as empty. Results are result sets, computed
  on demand and not persisted.
- Requirements are capabilities and loader facts (`SnapshotFacts`); one rule (`InspectionAvailability`) explains a
  missing one for listing and for running. Contracts owns the names front ends rely on (`InspectionColumns`,
  `InspectionNames`); Analysis mirrors them, checked by the architecture tests.
- Consequences: new inspections and the 2.6 detectors are one class each, testable on synthetic graphs with known
  answers; the UI and CLI need no change to show them (fragmentation is also drawn as bars, by its contract name).
  Inspections that read the dump are bounded by the reader's speed (duplicate strings reads every string whose size
  occurs twice or more). The context grows additively for 2.x: derived data per snapshot (reverse references,
  dominators) through the context, an optional baseline snapshot for growth detectors, and requirements beyond
  capabilities; runs may become asynchronous then.

- Amended in sprint 2.6 (the leak detectors): an inspection has a kind (`Waste` or `Leak`); leak detectors name a leak
  pattern, its cause and its fix, and count reachable objects only (a walk from the roots, `Reachability`), since
  garbage not yet collected is no leak. The context grew as planned: an optional baseline snapshot (another open
  snapshot, matched by stable keys such as a static field's name, never by address, ADR-018) for the detectors that
  measure growth, and the incoming references for those that follow references back, both supplied by the worker only
  to the inspections that declare them; a baseline the inspection cannot read is refused. Detectors that read fields
  are unavailable on gcdumps, with the reason. Each threshold is a constant logged as the rule's parameters; the known
  answers name what each detector must report for each leak fixture, and a clean LeakyApi run must report nothing.
  Reachable means reached from a retaining root other than the finalizer queue (its objects are already dead), or
  frozen. Precision over recall (review): an event is a multicast delegate in a field of the publisher's own, or a
  single delegate in a field named as a field-like event; a timer is a periodic one in the queue; HttpClients count by
  the distinct handlers they own; what is left out is logged by reason with its most common keys. A detector row opens the type's reachable objects (`InstanceScope.Reachable`), never the whole
  type, so whole-type actions such as Retained by set stay with whole-type rows.
