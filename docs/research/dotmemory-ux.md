# dotMemory UX research for HeapScope

Date: 2026-10-23. Scope: JetBrains dotMemory as of the 2024.3 to 2026.x releases (standalone app, Rider and
Visual Studio integration), from public sources only (JetBrains help, product and What's New pages, the .NET
Tools blog). Nothing was downloaded or installed. Where a point comes from general familiarity with public
screenshots rather than a cited page, it is marked "(unverified)".

Status at 3.8: since built: interface grouping and Similar Retention (3.2), the unreachable set (3.5), Force GC and
session cleanup (3.4); the UI is Avalonia (ADR-025), not WPF, and no WPF detectors were built.

Goal: a similar look and feel for HeapScope, not feature parity. Note one structural difference up front:
dotMemory is a profiler that attaches an agent and records allocations; HeapScope is dump based (full dumps
and gcdumps, plus EventPipe counters). Several dotMemory features exist only because of its agent.

---

## 1. Overall workflow and window structure

**Home window (standalone).** Opens when dotMemory starts. Two questions drive it:
"Choose what you want to profile" (a New Process Run list of saved run configurations, or Running Process with a
"Show All Processes" button that elevates) and "Choose how you want to profile it" (profiling options). A single
Start button begins the session. Run configuration types: Standalone, .NET and .NET Core, IIS, IIS Express,
Windows Service, WCF Service, WinRT, External .NET Process (attach to the first matching process that starts),
MAUI. Options: allocation data Sampled (default, low overhead, works on attach) or Full (exact counts, slow, not
on attach), control Manually or via API (`MemoryProfiler.GetSnapshot()`), process filters with include and
exclude wildcard masks and "Profile entire process tree". Process dump import lives on the Home page left panel
(older versions) or the Recent Workspaces tab (Rider); an imported dump becomes a regular workspace.

**Workspace.** All data of one session (snapshots, allocation data) is saved to a workspace file (`.dmw`).
Rider's dotMemory Profiler tool window has a Recent Workspaces tab with columns Last Opened, Name, Created, Size.
Since 2025.1 there is temporary workspace storage with cleanup by size or age.

**Profiling session (live).** Four areas: process list (multi-process apps), controls bar, timeline graph,
collected snapshots area. Controls: Get Snapshot, Get Snapshot on Condition, Allocation data Sampled/Full
toggle, Force GC, Detach (attach sessions only), Kill, Kill All. Conditional triggers: memory increase by X MB or
X % over baseline, total memory above X MB, every X minutes, with a cap on snapshot count, and Start/Stop.

**Timeline graph.** Stacked area charts of Gen0, Gen1, Gen2, LOH, Pinned Object Heap and (since 2024.1) Frozen
Object Heap sizes, optional unmanaged memory ("Show unmanaged memory" checkbox), and an "Allocated in LOH since
GC" series that resets after each GC. Green dotted line marks intervals with sampled allocation data, solid green
marks full data. Click a point for exact values; drag to select an interval, then click "Allocated: N MB" to open
allocation analysis for that range. Real-time inspections appear above the graph without a snapshot: pinned
objects in Gen0, high GC pressure (GC over 30 % of time), fast LOH growth; clicking one jumps to its interval.
Snapshot markers sit on the timeline at capture time (unverified detail of marker style).

**Analysis Overview page (per workspace).** Opens in a new tab after collection. Shows the timeline, then a
card per collected snapshot: a grey total memory bar split into unmanaged, ".NET, total" (includes free heap
space) and ".NET, used"; the total matches Task Manager commit size. Each card has the snapshot name (opens it),
Rename, an object count link (all objects in Group by Types), "Marked objects", and "Memory allocation" (start to
this snapshot). Below is the comparison area: drag two snapshots in or click "Add to comparison", then Compare.
The earlier snapshot is always the baseline. Cross-workspace comparison is offered via "Compare with snapshot
from another workspace".

**Rider and Visual Studio.** Rider: dotMemory Profiler tool window (Running Processes, Recent Workspaces), a
"Profile running process" entry in the Run widget, profiling configurations under Run, and an Analysis editor
document that hosts the same controller and views. Rider 2025.2 reworked the Monitoring tool window (live CPU,
memory and GC charts that can launch a dotMemory session). Visual Studio gained in-IDE snapshot analysis in
2025.1. Types link to source via Navigate to Declaration (Ctrl+L repeats).

Sources:
- https://www.jetbrains.com/help/dotmemory/Getting_Started_with_dotMemory.html
- https://www.jetbrains.com/help/dotmemory/Profile_Running_Process.html
- https://jetbrains.com/help/dotmemory/Profile_NET_Process.html
- https://www.jetbrains.com/help/dotmemory/dotMemory_Profiler_Options.html
- https://www.jetbrains.com/help/dotmemory/Controlling_Profiling_Process.html
- https://www.jetbrains.com/help/dotmemory/Analysis_Overview_Page.html
- https://www.jetbrains.com/help/dotmemory/Comparing_Snapshots.html
- https://jetbrains.com/help/dotmemory/Importing_Process_Dumps.html
- https://www.jetbrains.com/help/rider/dotMemory_Profiler_window.html
- https://www.jetbrains.com/help/rider/Attach_to_and_profile_.NET_processes_memory.html
- https://www.jetbrains.com/dotmemory/whatsnew/

---

## 2. Snapshot overview page (Inspections view)

Opening a snapshot lands on the Inspections view (called Snapshot Overview in older versions). Sections:

1. **Largest Size**: a horizontal bar diagram of the types that occupy the most shallow memory. Clicking a bar
   makes that type the analysis subject and opens the Types view.
2. **Largest Retained Size**: the top dominators (objects that keep the most memory alive). Clicking opens the
   Dominators view.
3. **Automatic Inspections**: a list of findings, each with a header, a count, and a link that opens the
   flagged objects as an object set. Inspections: string duplicates, sparse arrays (mostly zero), finalizable
   objects (queued, plus finalized since the previous snapshot), event handlers leak, WPF binding leak, WPF
   collection binding leak, dependency property (`AddValueChanged`) leak, `x:Name` WPF leak. Each explains the
   cause and a typical fix in one or two sentences.
4. **Heap Fragmentation**: one row per segment kind (Gen1, Gen2, LOH, pinned object heap, frozen object heap),
   each bar split into pinned, unpinned and free. The header opens Group by Generations; links open pinned objects
   and objects unreachable from roots (relevant for raw dumps, which skip the GC) as separate sets.

The pattern to copy: every tile is a short ranked list whose rows are links into a detail view, with the clicked
item becoming a new step in the Analysis Path.

Sources:
- https://www.jetbrains.com/help/dotmemory/Snapshot_View.html
- https://www.jetbrains.com/help/dotmemory/Inspections.html
- https://www.jetbrains.com/help/dotmemory/Heap_Fragmentation.html

---

## 3. Analysis views

**Model.** Analysis is a sequence of subjects: snapshot, then object sets, then single instances. Each chosen
subject is appended to the **Analysis Path**, a vertical list in the left panel. Clicking an earlier step returns
to it so you can branch differently. Each subject has several **views**, picked from a view list. Back and forward
buttons (top left, plus mouse hardware buttons) walk the view history, not the subject list. Several analyses of
the same snapshot can run in tabs (Ctrl+Alt+T new analysis, Ctrl+T or the Add button copies the current one).

**Object set views** (the view list for a set):
- **Types** (Group by Types): columns Type, Objects count, Bytes (shallow), Minimum retained bytes. Groupings:
  plain list, by namespace, by assembly, by interface.
- **Dominators**: dominator tree with type, retained bytes, retained object count; a number before the type gives
  how many objects are on that path. Tree mode bolds the selected node's domination path; Path mode shows only that
  path. Paired with a **sunburst** chart (arc angle = retained size) or an **icicle** chart (vertical variant,
  2024.3). Click an arc to highlight its path in the tree, double-click to zoom in, click the center to zoom out.
  Alt-click opens what it retains, Shift-click opens the dominator's own set.
- **Similar Retention**: groups objects by retention pattern, each with the two most different shortest paths
  to roots drawn as a small graph; columns Name, Objects count, Bytes.
- **Instances**: Type, Address, Bytes, Retained bytes. Filter supports string values, regex on strings, and
  addresses (2024.2).
- **Generations**: Gen0, Gen1, Gen2, LOH, FOH with Total, Utilization, Object count, Used (and Fragmentation for
  the all-objects set).
- **Call Tree** and **Back Traces**: allocation stacks of the set's objects (needs allocation data). Back Traces is
  the inverted call tree.

**Instance views** (after opening one object):
- **Outgoing References**: the object's primitive field values (copyable) and a list of referenced objects with
  Name (field), Type, Address, References count, Bytes, Retained bytes.
- **Key Retention Paths**: a diagram of the retention paths that differ most, omitting near duplicates (if
  A>B>D and A>C>D are similar, only one is shown). Wheel scrolls vertically, Shift+wheel horizontally.
- **Incoming References**.
- **Shortest Paths to Roots**: two layouts, a flat list stepped with < > arrows (Field name, Type), and Merged
  Shortest Paths, a tree from the instance toward roots (Type, Reachable roots count, Bytes).
- **Creation Stack Trace** (returned in 2024.3; needs allocation data).

**Allocation (traffic) views**, from a timeline interval or between snapshots: Types (with Back Traces below),
Methods (with a sunburst of the top five allocated types), Call Tree, Subsystems (group by type, namespace or
assembly; exclude a subsystem with Del). Columns Objects, Bytes, Total, Own.

**Drill-down path.** Overview tile or Types row, double-click (or "Open this object set") adds the type's objects
as a subject, switch to Instances, double-click an instance ("Open this instance") adds it as a subject, then
Outgoing References, Shortest Paths or Key Retention Paths. From any set or instance, "Open objects retained by
this set" adds an "Exclusively retained objects" subject shown in Group by Types. Ctrl or Shift multi-select of
types opens them as one set. Instances can be **marked** (ten numbered marks, random, or custom color and name);
marked objects form a set reachable from every snapshot.

**Filter syntax** (shared by Types, Dominators, Similar Retention, Instances): type to highlight, CamelHumps,
`*` wildcard, `[]` and `!a` for arrays, `<`, `!g` for generics, `#c`, `#struct`, `#m`, `#ns` scopes,
`byte[10..100]` for array lengths.

Sources:
- https://www.jetbrains.com/help/dotmemory/First_Look_at_the_dotMemory_s_User_Interface.html
- https://www.jetbrains.com/help/dotmemory/Analyzing_Results.html
- https://www.jetbrains.com/help/dotmemory/Analyzing_an_Object_Set.html
- https://www.jetbrains.com/help/dotmemory/Type_List.html
- https://www.jetbrains.com/help/dotmemory/Retained_by.html
- https://www.jetbrains.com/help/dotmemory/Similar_Retention.html
- https://www.jetbrains.com/help/dotmemory/Instances.html
- https://www.jetbrains.com/help/dotmemory/Generations.html
- https://www.jetbrains.com/help/dotmemory/Analyzing_an_Instance.html
- https://www.jetbrains.com/help/dotmemory/Outgoing_References.html
- https://www.jetbrains.com/help/dotmemory/Key_Retention_Paths.html
- https://www.jetbrains.com/help/dotmemory/Shortest_Paths_to_Roots.html
- https://www.jetbrains.com/help/dotmemory/Analyze_Memory_Allocation.html

---

## 4. Snapshot comparison view

A grid with one row per type and columns grouped as: Survived objects and bytes, New objects and bytes, Dead
objects and bytes, Objects delta and Bytes delta (new minus dead). Groupings: plain list, by interface, by
namespace, by assembly. A Filter field hides non-matching types. Clicking a count in Survived, New or Dead asks
which snapshot to analyze (dead objects only exist in the baseline), then opens those objects as a subject in
Group by Types, so the comparison feeds straight into the normal drill-down. Memory allocation between the two
snapshots is also reachable from the comparison. Cross-workspace comparison shows the same grid.

Sources:
- https://www.jetbrains.com/help/dotmemory/Comparison_View.html
- https://www.jetbrains.com/help/dotmemory/Comparing_Snapshots.html

---

## 5. Visual style

Documented facts:
- Since 2024.3 all platforms share one UI (the cross-platform client is built with Avalonia, per Avalonia's
  case study). In Rider and Visual Studio the views follow the IDE theme, so dark is the common case there.
- Layout: tabs per analysis across the top, back and forward at top left, Add (new analysis) at top right,
  Analysis Path in a narrow left column, view list for the current subject, then the main grid or chart.
- Charts: stacked area timeline per heap, horizontal bar diagrams on the overview, sunburst and icicle charts for
  dominators and call trees, bar rows for fragmentation (pinned, unpinned, free).
- Grids: sortable columns (Back Traces sorts via Total and Own headers), type-to-filter with highlighted
  matches rather than a separate search dialog, context menus with "Open this object set", "Open this
  instance", "Navigate to Declaration", "Mark object", "Export object...".

From public screenshots (unverified, check against current builds):
- Standalone app is light by default: white content, light grey chrome, thin separators, no heavy borders;
  dark appearance is available in newer builds or via the IDE theme.
- Each heap in the timeline has its own fixed color, reused in legends, the snapshot cards and the Generations
  view, so the colors act as a legend across the app. Allocation interval shading is green.
- Dense, compact rows (about 20 to 22 px), system UI font around 12 px, right aligned numbers.
- Sizes on cards and charts are humanised (KB, MB, GB with one or two decimals); grid Bytes columns show full
  integers with thousands separators; deltas carry an explicit + or minus sign.
- Small monochrome type and namespace icons in the JetBrains IDE style; links are plain blue text, not buttons.

Sources:
- https://www.jetbrains.com/dotmemory/whatsnew/
- https://blog.jetbrains.com/dotnet/2024/11/13/dotnet-tools-243-release/
- https://avaloniaui.net/success/jetbrains
- https://www.jetbrains.com/dotmemory/features/
- https://www.jetbrains.com/help/dotmemory/Analyzing_Results.html

---

## 6. Command line and dotMemory Unit

**dotMemory command-line profiler** (free, zip or NuGet for Windows, Linux, macOS x64). Commands:
`get-snapshot` (by PID or name, `--with-max-mem` or `--all`), `attach`, `start`, `start-net-core`, and
Windows-only `start-iis`, `start-iis-express`, `start-windows-service`, `start-wcf-service`, `start-winrt`,
`profile-new-processes`, plus `list-*` helpers. Triggers: `--trigger-timer`, `--trigger-mem-inc`,
`--trigger-delay`, `--trigger-on-activation`. Output is a `.dmw` workspace (`--save-to-dir`, `--save-to-file`).
Control via stdin or `--service-input` with `##dotMemory` JSON messages; `--service-output` emits `connected` and
`workspace-saved` events. It captures only; analysis happens in the UI.

**dotMemory Unit.** A test framework add-on (NUnit, MSTest, xUnit) for memory assertions inside unit tests,
for example `dotMemory.Check(memory => memory.GetObjects(where => where.Type.Is<Foo>()).ObjectsCount)` asserted
equal to zero, plus traffic checks and snapshot comparison. Runs under the ReSharper or Rider test runner or a
standalone launcher; snapshots can be saved for later analysis in the UI.

Sources:
- https://www.jetbrains.com/help/dotmemory/Working_with_dotMemory_Command-Line_Profiler.html
- https://www.jetbrains.com/help/dotmemory-unit/Introduction.html

---

## 7. Recommendations for HeapScope

### Feature mapping

| Sprint | dotMemory feature to model | Adopt | Skip or defer |
|---|---|---|---|
| 1.1 Process discovery and capture | Home window: "what to profile" process list with Show All Processes (elevate); session = workspace | Process list grid with an elevation hint in place of "Show All Processes"; session library as our workspace with a Recent Sessions list (Name, Created, Last Opened, Size) | Run configurations for IIS, services, WinRT, MAUI; launching under a profiler; process filters |
| 1.2 Full dump loader | Import Process Dump on Home, dump becomes a regular workspace | "Open dump" next to capture on the home page, dump lands in the session library like a capture; progress with cancel | Workspace file format compatibility |
| 1.3 Heap summary by type | Types view (Type, Objects, Bytes), groupings, type-to-filter, Generations view | Types grid as the core view; plain, namespace and assembly grouping; CamelHumps plus `*` filter; generation split as a sub-view like dotMemory's Generations view (Gen0/1/2, LOH, POH, FOH); also the Largest Size tile | Group by interface (later); full `#c`/`#m` filter syntax (2.7) |
| 1.4 Snapshot diff | Comparison view; Analysis Overview comparison area | Grid with New, Dead, Survived (count and bytes) plus signed deltas, earlier snapshot as baseline; clicking a count opens that object set in Types | Drag and drop into a comparison area (a two-picker is enough); cross-workspace compare until sessions are stable |
| 1.5 Object search and inspection | Instances view, Outgoing References, string and address filter, back and forward | Instances grid (Address, Bytes, later Retained); filter by string value, regex, address; Outgoing References as fields plus references list; back and forward buttons top left with mouse buttons | Export object (2.7) |
| 1.6 Simple inspections | Inspections view: Largest Size, string duplicates, sparse arrays, finalizable objects, Heap Fragmentation | Overview page with tiles whose rows link into views; inspection rows show count, wasted bytes, one-line cause and fix; add sparse arrays and a fragmentation bar (pinned, unpinned, free) as cheap wins | WPF-specific inspections until 2.6 |
| 1.7 Live counters and timed snapshots | Timeline graph, Get Snapshot on Condition, real-time inspections | Stacked area chart per heap with fixed per-heap colors, snapshot markers on the timeline, snapshot cards with a total bar; trigger rules (MB over baseline, % over baseline, absolute MB, every N minutes, max snapshot count); a High GC pressure badge from counters | Sampled/Full allocation shading (2.5); Force GC button unless EventPipe allows it cleanly |
| 1.8 gcdump | No direct equivalent | Show capability gaps the way dotMemory greys allocation-only views: view present but disabled with a reason | None |
| 2.1 Reverse references | Incoming References, Merged Shortest Paths tree | Incoming references grid per object and per type | None |
| 2.2 Retention paths | Shortest Paths to Roots, Key Retention Paths, Similar Retention | Merged paths tree from the object toward roots with Reachable roots count; "top N distinct paths" is exactly Key Retention Paths, so present them as that diagram or as a list of distinct paths; plain-language root kinds | Similar Retention grouping (backlog) |
| 2.3 Dominators and retained size | Largest Retained Size tile, Minimum retained bytes column, Retained bytes in Instances | Retained bytes columns everywhere sizes appear; "Open objects retained by this set" action | None |
| 2.4 Retention explorer UI | Dominators view with Tree and Path modes, sunburst or icicle chart, Analysis Path | Dominator tree plus an icicle chart (simpler to draw and label in WPF than a sunburst, same data); click to highlight path, double-click to zoom; Biggest retainers tile from the overview | Sunburst as an optional toggle later |
| 2.5 Allocation tracking | Memory allocation view: Types, Methods, Call Tree, Back Traces, Subsystems, timeline interval selection | Select a timeline interval and open "Allocated: N MB"; Types with Back Traces below; Call Tree with Total and Own columns | Subsystems view, Methods sunburst, Full (exact) mode |
| 2.6 Smart leak detectors | Event handlers leak, WPF binding and collection binding leaks, dependency property leak, x:Name leak, finalizable objects | Same presentation as 1.6: one card per detector with cause, fix and a link to the objects; add WPF detectors since our users are WPF heavy | None |
| 2.7 Navigation and polish | Analysis Path sidebar, back and forward, analysis tabs, marks, filter syntax, Navigate to Declaration | Analysis Path breadcrumb sidebar (branch from any earlier step), multiple analysis tabs, object bookmarks as colored marks reachable across snapshots, full filter syntax, keyboard shortcuts, CSV export | Navigate to Declaration (needs IDE integration) |

### Look and feel to adopt

- Navigation model: subject then view. Left Analysis Path column (introduce a simple version with back and
  forward in 1.5, full branching sidebar in 2.7), view switcher above the content, analysis tabs (2.7).
- Overview first: opening a snapshot lands on a tile page (1.3 Largest Size, 1.6 inspections and fragmentation,
  2.3 Largest Retained Size). Every row in every tile is a link into a drill-down.
- One drill-down verb everywhere: double-click or Enter opens the row as a new subject; context menu offers
  "Open this set", "Open this object", "Open retained objects" (from 2.3).
- Dense grids with right aligned numbers, integer bytes with thousands separators in grids, humanised sizes in
  tiles and charts, signed deltas colored only by sign (1.3, 1.4).
- Type-to-filter with match highlighting instead of a search dialog (1.3), extended syntax in 2.7.
- Fixed per-heap color palette shared by timeline, cards, Generations and fragmentation bars (1.3 defines it,
  1.7 uses it), defined as theme resources so light and dark both work.
- Light and dark themes from day one via WPF resource dictionaries; quiet chrome, thin separators, plain text
  links.

### Skip

- Profiler-agent features: launching apps under a profiler, run configuration zoo, Full allocation mode, Kill
  and Detach controls, API-controlled snapshots.
- Drag and drop comparison area, sunburst as the primary chart, the full JetBrains icon set (use a small
  neutral icon set).
- `.dmw` compatibility and dotMemory Unit style test API (see backlog).

### Backlog candidates (no sprint today)

- Similar Retention view (groups by retention pattern).
- Group by interface in Types and diff.
- Sparse arrays and finalizable objects inspections if not taken into 1.6.
- Unreachable objects set for raw dumps (dumps include garbage the GC has not collected).
- Real-time inspections on the timeline (high GC pressure, fast LOH growth, pinned in Gen0).
- Cross-session comparison (snapshot from another session).
- Object marks persisted across snapshots by identity heuristics.
- Navigate to Declaration via a Visual Studio or Rider link.
- Sunburst toggle for dominators and call trees.
- Test-time memory assertions (a dotMemory Unit analogue) on top of the CLI `diff --fail-on-growth`.
- Process filters and child process capture for multi-process apps.
- Temporary session storage cleanup by size or age.
