# ADR-029 Analysis path per snapshot; bookmarks by snapshot file (added in sprint 2.7)

- Context: 2.7's acceptance asks that history survive view switches. Until 2.6 the Snapshot tab kept one linear
  history of 30 subjects, reset whenever another snapshot was selected; going back and opening something dropped the
  later subjects. dotMemory's Analysis Path lets a person branch from any earlier step. Objects have no identity across
  dumps (ADR-018), so a mark on an object belongs to one dump file.
- Decision: each open snapshot keeps an `AnalysisPath`, a tree of steps rooted at the types grid, while it is open.
  Opening something adds a child of the current step; Back goes to the parent, Forward to the child shown last; a link
  into the Retainers tab is a step that replays its target. At most 30 steps per snapshot: the least recently shown
  leaf off the current trail is closed first; a trail longer than that loses its oldest step below the root. A step can
  be closed with its branch. Closing a snapshot closes its path. Bookmarks mark objects of a snapshot file, keyed by its
  full path, size and write time (a new capture at the same path starts clean), with one of five colors, and are kept in
  `bookmarks.json` beside the session library.
- Consequences: subjects of every open snapshot's path keep their row lists in the front end (up to 30 steps per
  snapshot, so the front end's share grows with the open snapshots; the 2.7 review M3) and their worker result sets,
  which the worker's result set store (32 sets, 256 MB in all) may evict: a step shown again then re-runs its query.
  Releasing the sets of snapshots not shown and measuring three Medium snapshots open are in the Backlog. The types
  grid is shared by the paths and re-queried for the selected snapshot. Every change to the bookmarks file re-reads it
  under a lock file, so two HeapScope windows keep each other's marks (review H3). Several analyses side by side
  (analysis tabs) stay in the Backlog: the branching path covers coming back to an earlier one without more result sets. Bookmarks are not matched to objects of another dump.
