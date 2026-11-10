# HeapScope user guide

The [README](../README.md) shows what HeapScope does. This guide covers how each part works, and
[INSTALL.md](../INSTALL.md) takes you from download to a first snapshot.

## Finding your way

- **Info icons.** The (i) beside a page or tab title says what it is for, when to use it and how to start. One beside a group
  of settings says what each of them does, a line each, so it helps even when a setting is turned off. Hover over the icon,
  click it, or reach it with Tab and press Enter; Escape closes it. A tooltip shows even on a button, menu item or input that
  is turned off, and many say there what they need.
- **Analysis path.** Each open snapshot keeps your last 30 steps (types, a type's objects, an object, referrers,
  retention, links into the Retainers tab) in the Snapshots page's left column. Opening something from an earlier step
  starts a branch; the later steps stay. Back and Forward (Alt+Left, Alt+Right, the mouse buttons) move along it.
- **Bookmarks.** Ctrl+D (Cmd+D on macOS) marks the shown object, or the selected row's object, with a color. Bookmarks
  of every open snapshot are listed together and come back when the same dump is opened again.
- **Object sets.** Select several types (Ctrl or Shift and click, or Shift+Down) and press Ctrl+Shift+O (Cmd+Shift+O), or
  open a namespace or assembly row, a Compare row, an inspection row, your bookmarks or what a set retains: the set
  gets its own step with Types, Instances, Generations, Dominators, Paths and Similar Retention views and "Objects
  retained by this set". A leak detector's row opens the objects behind it: a growing collection's row opens the collection itself, not its elements, an event row opens each subscriber delegate and the object it calls, not the publisher, so its object count can differ from the row's in either direction, and a timer row opens the queue entries, plus the timer object for a `System.Timers.Timer`, not the owner the row names. The CLI's `set` command shows the
  same views.
- **Retention views.** Similar Retention groups a set's objects by how they are retained (the paths of all their direct
  holders, not only the shortest one), each group with its most different paths; a group opens as a set of its own.
  Key retention paths can be shown as a diagram, the paths merged into one graph from the roots down; "Fit to width"
  scales it down to the page, to three quarters of its size at most; a wider diagram keeps its scroll bar. A type's "Why alive" page has a Similar retention button.
- **Comparison depth.** Compare can show each type's New and Dead objects, matched by address for two dumps of one
  process, and open a row's new, dead or survived objects as a set. It also shows each type's retained bytes in both
  snapshots, and the change per generation. The match is an estimate. A GC moves most young objects, so those usually
  count as dead and new. Survived means most for Gen2, LOH (unless compacted), POH and frozen objects, which keep their
  addresses, but a new object of the same type and size at a dead one's address still counts as survived. New and
  survived objects can include garbage not yet collected. "Trend across snapshots" puts the
  objects and bytes of up to 20 ticked snapshots side by side, with how often each type grew. The CLI's `diff`
  matches objects (`--match-objects`), adds retained bytes (`--retained`), and gates on one heap kind's growth
  (`--fail-on-growth 50MB --growth-of gen2`) or on each row (`--fail-on-growth-per-row`). For a stable gate, capture after a full GC in the target or compare
  gcdumps, since a full dump counts garbage not yet collected. That GC moves objects, so skip it when you match them by
  address. `--growth-of` a heap kind needs full dumps, since a gcdump records no generations. `trend` compares several
  dumps.
- **Reading the Retainers chart.** Each bar is what the bar above it keeps alive, and its width is how much. The two band
  colors only separate the levels; a selected bar and its path turn amber. Most bars are too narrow for a label, so hover
  for a bar's name and size. Click selects a bar, and a double-click shows the tree below it. Bars too narrow to point at
  are grouped into one bar, "+N smaller", that says how many it holds and their size. The tree below has them (expand a
  group or a "more" row if they are not shown); deeper in the chart, a double-click on the bar above them focuses on it, and
  the tree then has them.
- **Live workflow.** When a process cannot be monitored, a warning says why: it runs elevated or as another user, so
  run HeapScope as administrator. A warning also appears when no samples arrive after about 10 seconds (or three
  intervals, if longer), since a process can turn off the runtime events Live reads; Get snapshot still works then. The Live page takes a snapshot of the monitored process now (Get snapshot) and forces a full
  garbage collection (Force GC, a dotted line on the chart). Under "Snapshot rules" it captures on a schedule, when the heap has grown by an amount you set, or when the
  heap passes a size, and records an allocation trace for the whole run when asked; "Past runs of this session" reopens
  earlier timelines and snapshots. One capture per process runs at a time, from any page or rule. Snapshots can be closed
  and deleted with their caches, sessions deleted, and old sessions cleaned up from the Home page; each asks first.
- **Runtime leaks.** Detectors for async methods that never complete (their state machines waiting by the thousand), a
  finalizer backlog behind a blocked or slow finalizer thread, and, against a baseline, the application's types that
  keep growing. The objects no root reaches open as a set from a full dump's Types view (`set --unreachable`).
- **Group by interface.** The types grid and Compare group by interface on full dumps: a type counts under each
  interface it implements, so the rows overlap. In the types grid, an interface row opens as a set.
- **Filters.** In the types and Compare grids, terms separated by spaces must all match; `-term` leaves names out;
  `module:name` (or `m:name`) matches the module. Each term is a substring, CamelHumps with capitals (`SCE` finds
  `StaticCacheEntry`), or uses `*` for anything.
- **CSV export.** Every grid has "Export to CSV..." in its context menu (Ctrl+E or Cmd+E). It writes all rows, not only
  those on screen. String text, field values and command lines are left out unless you say yes for that export.
- **Jobs.** Long work shows a progress row above the status line while it runs, with Cancel. Finished jobs move to the
  Jobs page; the status line says how many finished and how many failed, and a click on it opens the page.
- **Keyboard.** F1 lists every shortcut on the About page: Ctrl+1 to 7 (Cmd on macOS) for the pages, Alt+1 to 4 for the Snapshots
  tabs, Ctrl+F for the filter, Ctrl+L for the analysis path, F5 to refresh.
- **Appearance.** HeapScope follows your system's light or dark theme, and changes with it while it runs. To choose one,
  start the app with `--Ui:Theme Light` or `--Ui:Theme Dark`.

## Logs and getting help

HeapScope writes a log file per run, which records the versions, the snapshots opened and why each result was produced
(never values from a dump): `%LOCALAPPDATA%\HeapScope\logs` on Windows, `~/.local/share/HeapScope/logs` on Linux and
`~/Library/Application Support/HeapScope/logs` on macOS (under the folder `HEAPSCOPE_DATA_FOLDER` names, when set).
The About page shows the folder and copies the version details for a support request. For more detail in one run,
start the app with `--Logging:File:MinimumLevel Debug`, or set the environment variable
`Logging__File__MinimumLevel=Debug` (the CLI reads settings from the environment, not from its arguments). Logs are kept
14 days. They hold file paths and type names, so share them only with someone you trust.
