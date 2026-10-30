# ADR-007 Out-of-process worker

- Context: huge dumps can exhaust memory or crash a parser; the UI must never freeze or die with them.
- Decision: a separate x64 worker; named pipes with StreamJsonRpc; progress and cancellation on every long
  operation.
- Consequences: DTO contracts, paging of large results, crash detection and restart.
- Amended by ADR-025 (sprint 2.3.5): the worker is built per system and architecture; a dump is read by a worker of
  the system and architecture that took it.
