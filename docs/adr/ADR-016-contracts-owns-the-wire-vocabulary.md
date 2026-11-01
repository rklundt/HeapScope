# ADR-016 Contracts owns the wire vocabulary (added in sprint 0.1)

- Context: the UI must bind feature availability to snapshot capabilities (ADR-003), but the UI may only use
  Application and Contracts. If DTOs carried Core enums, the UI would compile against Core and the layering rule
  would break the first time a DTO field was read.
- Decision: Contracts references no solution project. It defines its own wire enums (for example
  `SnapshotFeatures` mirroring Core's `SnapshotCapabilities`, `SnapshotKindDto`) and the user-facing capability
  descriptions. The worker maps Core values to Contracts values; a test asserts that mirrored enums keep
  identical numeric values. Core keeps its own types for the engine.
- Consequences: a small amount of mapping code in the worker; the protocol can evolve independently of the
  engine's internal types; UI and CLI never load a Core type through a DTO.
