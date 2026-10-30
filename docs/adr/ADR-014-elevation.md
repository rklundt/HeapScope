# ADR-014 Elevation

- Decision: dumping or attaching to elevated processes requires HeapScope to run elevated. Detect this and
  explain it clearly; never fail silently.
- Implemented in sprint 1.1: the process list marks such targets ("Needs administrator", with a hint), and a capture
  is refused before the runtime is asked, with "Run HeapScope as administrator". Tested with a fake probe, and against
  the real elevated .NET services on the development machine. HeapScope never elevates itself.
