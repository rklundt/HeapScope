# ADR-026 Sharing results: an exported analysis, analysis only by default (added in sprint 2.3.5)

- Context: teams want to share results (2026-10-25). A dump holds everything in the target's memory, secrets included
  (12), so it is never the thing shared.
- Decision: a shared result is an exported analysis, a file written where the user asks, never uploaded. By default it
  holds analysis only: type, namespace and assembly summaries, diffs, retained sizes, retention paths and dominators
  with type and field names, inspection results with counts and sizes, and the snapshot's metadata (runtime version,
  capture time, the target's process name). String text and field values are left out unless the person exporting opts
  in for that export, and the export then says so on its first line (product owner: "Analysis only").
- Consequences:
  - The export runs in the worker against an open snapshot: paths, field names and inspections need the dump, not only
    the index cache and the sidecars (which hold no values, 12). Opt-in values are read from the dump at export time,
    like inspection.
  - Values are marked where they are produced: result columns gain a flag for columns that carry values (the `Value`
    column of instances, a duplicate string's text), which the export leaves out unless the opt-in is given. The CLI's
    `--format csv|json` output is a local view for the person running it, not a shared export, and keeps its columns.
  - The export file has a versioned schema. HeapScope opens a JSON export as its own kind of document, with no graph
    capabilities (never presented as a snapshot).
  - Metadata left out by default: the dump's and the cache's file paths (they contain user names), the target's
    command line, the machine name.
  - The Backlog's "HTML report export" and 2.7's CSV export are forms of it: one export model, rendered as HTML, JSON
    or CSV. Opening a shared JSON export in HeapScope (without the dump) is part of the same item.
  - Amended in sprint 2.7 (review M2): the CSV export of a grid is a front-end renderer of the grid's result set, paged
    from the worker; it has no schema of its own and leaves out value columns on the front end side (pages still carry
    them until page requests can name their columns, a Backlog item). The whole-analysis export, with its versioned
    schema and the snapshot's metadata, runs in the worker as decided here and can feed the same renderer.
    Numbers that are not exact say so in their header ("at least", "estimated"); in-memory grids flag their value
    columns in the view (`GridExports.HasValues`, the Processes grid's command line).
  - A hosted or web view for teams is not planned; the export file is shared by the team's own means.
