# ADR-015 Adaptive memory modes (added 2026-10-22, follows from the ADR-004 amendment)

- Context: on a 16 GB laptop the target API, an IDE and the worker compete for memory; a Medium snapshot with
  2.x indices needs about 3 GB of graph data on top of everything else.
- Decision: graph data lives in `Column<T>` values backed by either managed arrays (in-memory mode) or
  read-only memory-mapped index cache sections (mapped mode). The worker picks the mode per snapshot from a
  memory budget derived from physical and available memory; configuration can force either mode.
- Consequences: index cache sections are page aligned and stored in the in-memory layout so they can be mapped
  without conversion; algorithms take spans only; the index cache moves from "nice to have" to required for
  mapped mode.
- Amended in sprint 2.3: optional indices follow the snapshot's mode (`ReverseIndexBuilder.ChooseMode`) and, with an
  index cache, are computed straight into their mapped sidecars, so a mapped computation holds only its working arrays.
  The 1.9 decision to reconsider the graph's own mode when indices are computed is not built: an index whose working
  arrays do not fit is refused with InsufficientMemory and a next step (decision 4); switching a live in-memory graph
  to mapped is a Backlog item.
