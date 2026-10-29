# HeapScope

Powered by [HeapScope](https://github.com/rklundt/HeapScope) by Ray Klundt.

HeapScope is a desktop memory profiler for modern .NET applications (.NET 10) on Windows, Linux and macOS. It opens
memory snapshots (and captures them on Windows), shows what is on the heap and what grew between two snapshots, and
explains why objects are retained, through a desktop app or an equivalent command line (`heapscope-cli`). Snapshots
never leave the machine.

- Build: `dotnet build HeapScope.sln`
- Test: `dotnet test --solution HeapScope.sln`
- UI: `dotnet run --project src/HeapScope.UI`
- CLI: `dotnet run --project src/HeapScope.Cli -- --help`

Developer documentation: [docs/DEVELOPER.md](docs/DEVELOPER.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Finding your way

- **Analysis path.** Each open snapshot keeps the steps you took (types, a type's objects, an object, referrers,
  retention, links into the Retainers tab) in the Snapshots page's left column. Opening something from an earlier step
  starts a branch; the later steps stay. Back and Forward (Alt+Left, Alt+Right, the mouse buttons) move along it.
- **Bookmarks.** Ctrl+D (Cmd+D on macOS) marks the shown object, or the selected row's object, with a color. Bookmarks
  of every open snapshot are listed together and come back when the same dump is opened again.
- **Object sets.** Select several types (Ctrl or Shift and click, or Shift+Down) and press Ctrl+Shift+O (Cmd+Shift+O), or
  open a namespace or assembly row, a Compare row, an inspection row, your bookmarks or what a set retains: the set
  gets its own step with Types, Instances, Generations, Dominators, Paths and Similar Retention views and "Objects
  retained by this set". A leak detector's row opens exactly the objects it counted. The CLI's `set` command shows the
  same views.
- **Retention views.** Similar Retention groups a set's objects by how they are retained (the paths of all their direct
  holders, not only the shortest one), each group with its most different paths; a group opens as a set of its own.
  Key retention paths can be shown as a diagram, the paths merged into one graph from the roots down. A type's "Why
  alive" page has a Similar retention button.
- **Group by interface.** The types grid and Compare group by interface on full dumps: a type counts under each
  interface it implements, so the rows overlap. An interface row opens as a set.
- **Filters.** In the types and Compare grids, terms separated by spaces must all match; `-term` leaves names out;
  `module:name` (or `m:name`) matches the module. Each term is a substring, CamelHumps with capitals (`SCE` finds
  `StaticCacheEntry`), or uses `*` for anything.
- **CSV export.** Every grid has "Export to CSV..." in its context menu (Ctrl+E or Cmd+E). It writes all rows, not only
  those on screen. String text and field values are left out unless you say yes for that export.
- **Keyboard.** F1 lists every shortcut on the About page: Ctrl+1 to 7 for the pages, Alt+1 to 4 for the Snapshots
  tabs, Ctrl+F for the filter, Ctrl+L for the analysis path, F5 to refresh.

## Logs and getting help

HeapScope writes a log file per run, which records the versions, the snapshots opened and why each result was produced
(never values from a dump): `%LOCALAPPDATA%\HeapScope\logs` on Windows, `~/.local/share/HeapScope/logs` on Linux and
`~/Library/Application Support/HeapScope/logs` on macOS. The About page shows the folder and copies the version details
for a support request; `--Logging:File:MinimumLevel Debug` adds detail for one run. Logs are kept 14 days. They hold
file paths and type names, so share them only with someone you trust.

## License

Copyright (C) 2026 Ray Klundt.

HeapScope is licensed under the [GNU Affero General Public License, version 3 only](LICENSE), with
[additional terms](ADDITIONAL-TERMS.md) under Section 7 of that license. In short, and the files themselves govern:

- **Copyleft, including over a network.** Modified versions, and services built on them, must be released under the
  same license with their source code.
- **Attribution.** Every work based on HeapScope must keep the notice "Powered by HeapScope by Ray Klundt" (or "Based
  on HeapScope by Ray Klundt"), with HeapScope linked to https://github.com/rklundt/HeapScope. In a user interface it
  must be readable on the main screen (or a splash screen shown for at least five seconds): real text on an opaque
  background, at least 4.5:1 contrast, no smaller than the body text. Command line tools show it in help, version and
  startup output. Modified versions must be marked as such.
- **AI and machine learning (Section 6).** Text and data mining rights are reserved. Models trained, fine-tuned or
  evaluated on HeapScope must credit it in their documentation, and code that an AI system produces from HeapScope is a
  work based on it, carrying the attribution and the same license.
- **Commercial licenses** that waive some or all of these requirements are available; see [COMMERCIAL.md](COMMERCIAL.md).

Third-party components keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
