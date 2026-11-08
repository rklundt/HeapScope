// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

namespace HeapScope.UI.ViewModels;

/// <summary>
/// What each info icon says (ADR-040). A page or tab's says what it is for, when to use it and how to start (approved 2026-11-05); a group
/// of settings' says what each one does, a line each (approved 2026-11-08). Each was checked against the code (`docs/design/info-text.md`);
/// a change to what a page or setting does may change its text here.
/// </summary>
public static class InfoTexts
{
    /// <summary>Home.</summary>
    public const string Home =
        "Start here when you want to see what a .NET process keeps in memory. A dump is a file holding a copy of a process's memory, and a session is a folder of what you captured. Press Open dump if you have a file, Capture from a process or Watch live if the process is running, or Open on a recent session.";

    /// <summary>Processes.</summary>
    public const string Processes =
        "Lists the running .NET processes, on Windows. Use it when the problem is happening now and you want a dump of the process, or a recording of what it allocates. Select a process, then press Capture dump or Record allocations.";

    /// <summary>Snapshots.</summary>
    public const string Snapshots =
        "Holds the dumps you have open, each one a snapshot of a process's memory at one moment. Use it to read them: Snapshot browses, Compare finds what grew, Inspections looks for common problems, and Retainers shows what holds the memory. Press Open dump, or open one from Home or Processes.";

    /// <summary>Snapshots: Snapshot tab.</summary>
    public const string SnapshotTab =
        "Shows one open snapshot: its totals on top, and below them what you opened from it, starting with its types. Use it to go from a type to its objects to one object. Select a snapshot on the left, then double-click a row to go a step deeper; Back returns.";

    /// <summary>Snapshot tab: Types.</summary>
    public const string Types =
        "Lists each type on the heap, the memory .NET manages for objects, with its object count and bytes. Use it first to see what fills the memory. It opens sorted by bytes; double-click a type to see its objects.";

    /// <summary>Snapshot tab: Instances.</summary>
    public const string Instances =
        "Lists the objects of one type, with each one's address, size and value, where value is a string's text or an array's length. Use it to find the objects worth a closer look, such as the biggest. It opens sorted by bytes; double-click an object to open it.";

    /// <summary>Snapshot tab: Object.</summary>
    public const string Object =
        "Shows one object: its fields, what it references, what references it, and why it stays in memory. Use it when you have found an object you expected to be gone. Double-click an object in any list to get here, then read Fields on the left and open the Retention tab on the right.";

    /// <summary>Object: Outgoing tab.</summary>
    public const string ObjectOutgoing =
        "Lists the objects this object references, with the field that holds each one. Use it to see what this object keeps hold of. Double-click a row to open that object.";

    /// <summary>Object: Incoming tab.</summary>
    public const string ObjectIncoming =
        "Lists the objects that reference this one. Use it to see who holds this object, one step away. Open the tab and it loads, building an index the first time for a snapshot; double-click a row to open that object.";

    /// <summary>Object: Retention tab.</summary>
    public const string ObjectRetention =
        "Use it to see why this object is still alive, as reference paths from roots. A root is something the runtime always keeps, such as a static field or a local variable on a thread's stack. Open the tab and it searches by itself; double-click a key path to open the root's object.";

    /// <summary>Snapshot tab: Why alive (paths).</summary>
    public const string WhyAlive =
        "Use it when a type's objects should be gone: each row is one chain of types from a root to those objects, largest first. A root is something the runtime always keeps, such as a static field. Press Why alive on a type's objects, then double-click a row to open an example object.";

    /// <summary>Snapshot tab: Referrers.</summary>
    public const string Referrers =
        "Lists the types whose objects reference this type's objects, with counts. Use it for a quick look at who holds them, one step away. Press Referenced by on a type's objects, then double-click a row to see that type's objects.";

    /// <summary>Snapshot tab: Set.</summary>
    public const string Set =
        "A set is a group of objects you treat as one, such as several types, a namespace, a comparison's new objects or your bookmarks. Use it to ask one question about all of them. Choose Open as a set, then pick a view across the top.";

    /// <summary>Set view: Types.</summary>
    public const string SetTypes =
        "Shows the set's objects by type, namespace, assembly or interface. Use it to see what the set is made of. It shows first; double-click a type to see the set's objects of that type.";

    /// <summary>Set view: Instances.</summary>
    public const string SetInstances =
        "Lists every object in the set, one per row. Use it to find single objects, such as the biggest. Pick Instances, then double-click an object to open it.";

    /// <summary>Set view: Generations.</summary>
    public const string SetGenerations =
        "Shows the set's objects and bytes per heap area: Gen0, Gen1 and Gen2 hold objects by age, LOH large objects and POH pinned objects. Use it to see how long the set's objects have stayed. Pick Generations; a gcdump has no generations, and the view says so.";

    /// <summary>Set view: Dominators.</summary>
    public const string SetDominators =
        "Lists the set's objects that no other member keeps alive, with the bytes each one retains: what it alone keeps alive. An object dominates another when every path to that other object from a root, something the runtime always keeps, passes through it. Use it to find the few objects that account for the set; double-click a row to open one.";

    /// <summary>Set view: Paths.</summary>
    public const string SetPaths =
        "Shows the paths from roots, things the runtime always keeps such as static fields, to the set's objects. Use it to see what keeps the whole set alive. Pick Paths, then double-click a row to open an example object, or tick Diagram to merge the paths into one graph.";

    /// <summary>Set view: SimilarRetention.</summary>
    public const string SetSimilarRetention =
        "Groups the set's objects by how they are held, looking at up to 16 direct holders of each object, not only its shortest path. Use it to tell apart objects that stay alive for different reasons. Pick the view, select a group to see its paths as a diagram, and double-click it to open the group as a set.";

    /// <summary>Snapshots: Compare tab, Two snapshots.</summary>
    public const string CompareTwo =
        "Shows what changed between two open snapshots, per type, in objects and bytes. Use it when memory grew and you want to know what grew. Open two dumps; the earlier one becomes the baseline, and the list sorts by bytes grown.";

    /// <summary>Snapshots: Compare tab, Trend across snapshots.</summary>
    public const string CompareTrend =
        "Lines up each type's objects and bytes across 2 to 20 open snapshots, and counts the steps in which it grew. Use it to find what keeps growing, rather than what changed once. Choose Trend across snapshots, then tick the snapshots to include.";

    /// <summary>Snapshots: Inspections tab.</summary>
    public const string Inspections =
        "Runs ready-made checks on the selected snapshot for common leaks and wasted memory, and gives each finding a likely cause and a fix. Use it first when you do not know what to look for. Open the tab and the checks run by themselves; select one, then double-click a row to open its objects.";

    /// <summary>Snapshots: Retainers tab.</summary>
    public const string Retainers =
        "Use it to find what really holds the memory: it ranks objects by retained bytes, what an object alone keeps alive. Each row sits under its dominator: the closest object on every path to it from a root, something the runtime always keeps. Open the tab, then expand a row, or select one and press Focus.";

    /// <summary>Allocations.</summary>
    public const string Allocations =
        "Shows which types and call stacks allocated the most memory in a recording, estimated from samples. Use it when you want to know which code creates all these objects. Press Open trace, or record one with Record allocations on the Processes page; then drag across the timeline to look at one moment.";

    /// <summary>Live.</summary>
    public const string Live =
        "Charts a running process's memory over time, by heap area, on Windows, and can take dumps by itself when a rule fires. Use it when a leak takes time to show, so you can leave it running. Choose a process, then press Start.";

    /// <summary>Jobs.</summary>
    public const string Jobs =
        "Lists long work such as opening a dump, capturing or recording, with its progress and how it ended. Use it to see what finished or failed, and why. Jobs appear here by themselves; press Cancel on one that is running.";

    /// <summary>About.</summary>
    public const string About =
        "Shows the version, the system, the log folder and every keyboard shortcut. Use it when you need a shortcut, or want to ask for support. F1 comes here from any page, and Copy details puts the support details on the clipboard.";

    /// <summary>Home: Recent sessions heading.</summary>
    public const string Sessions =
        "A session is a folder of the dumps, recordings and timelines you capture, and the first capture starts one. Opening a dump file from elsewhere does not add it to a session.\n" +
        "Open makes a session the current one, so new captures go into it, and opens its latest snapshot.\n" +
        "Delete removes a session's folder with its dumps and caches; Clean up does so for sessions not opened for the days you set. Both ask first, and both leave alone the current session, sessions in use, sessions with an open snapshot, and dumps stored outside the folder.";

    /// <summary>Processes: As gcdump.</summary>
    public const string CaptureOptions =
        "Capture dump copies the selected process's memory into a full dump, and the process is paused while it is written.\n" +
        "As gcdump captures only the objects, their sizes and their references, with no field values, string text or generations. The process runs one full garbage collection for it.";

    /// <summary>Snapshots: Analysis path heading.</summary>
    public const string AnalysisPath =
        "Lists the steps you took in this snapshot as a tree; choose one to go back to it. Opening something from an earlier step starts a branch, and at most 30 steps are kept. Ctrl+L (Cmd+L on macOS) comes here.";

    /// <summary>Snapshots: Bookmarks heading.</summary>
    public const string Bookmarks =
        "Objects you marked with Ctrl+D (Cmd+D on macOS), from every open snapshot; double-click one or press Enter to open it. They come back when the same dump is opened again.\n" +
        "Open as a set treats the bookmarks of the selected bookmark's snapshot as one set of objects. With bookmarks in one snapshot only, no selection is needed.";

    /// <summary>Snapshot tab: The row of totals.</summary>
    public const string SnapshotTotals =
        "Heap size is the total bytes of the objects here. A full dump counts objects not collected yet, while a gcdump counts only what survived its collection.\n" +
        "Roots counts what the runtime always keeps, such as static fields, local variables on thread stacks and GC handles. An object stays in memory while a chain of references from a root leads to it.\n" +
        "The line above says how this snapshot was loaded. The index cache is a file HeapScope saves on the first load, so later opens skip loading the dump. In memory or memory-mapped is chosen for you: in memory when the data fits the memory budget, otherwise read from the cache file as needed.";

    /// <summary>Types: Heap areas bar.</summary>
    public const string HeapAreas =
        "Shows how the bytes split across heap areas: Gen0, Gen1 and Gen2 hold objects by age, LOH large objects, POH pinned objects. Frozen objects are never collected, and a gcdump shows Unknown generation.";

    /// <summary>Types: The options row.</summary>
    public const string TypesOptions =
        "Group by adds up the rows by type, namespace, assembly or interface. Interface needs a full dump, and a type counts under each interface it implements, so those rows overlap.\n" +
        "Generations adds a column per heap area. It needs a full dump, because a gcdump records no generations.\n" +
        "Retained sizes adds the bytes each row keeps alive, as a lower bound, except for interface rows. The first time for a snapshot HeapScope has to compute them, which can take a while.\n" +
        "Unreachable objects opens as a set the objects no root reaches: garbage not collected yet, or objects held only weakly. It shows only for a full dump whose object graph is complete. Objects waiting for their finalizer are not in it.";

    /// <summary>Object: Retention tab, Show framework steps.</summary>
    public const string ObjectRetentionDetails =
        "Retains is what this object alone keeps alive. Dominated by links to its immediate dominator: the closest object that every path from a root to this one passes through.\n" +
        "Show framework steps shows every step of a path. When off, runs of framework types (System, Microsoft and similar) between your own steps fold into one line.\n" +
        "The list has one row per distinct path among the roots found, with how many roots share it. Double-click or Enter opens the root's object.\n" +
        "The tree below it starts at this object and branches out to what holds it, on toward the roots. Click an address to open that object.";

    /// <summary>Why alive, Set: Paths: Diagram and its options.</summary>
    public const string PathOptions =
        "Diagram merges the paths into one graph, with roots at the top and the objects at the bottom. Thicker lines hold more objects.\n" +
        "Fit to width, shown with Diagram on, shrinks it toward the page's width, to three quarters of its size at most, so its labels stay readable. A wider diagram keeps its scroll bar.\n" +
        "Show framework steps shows every step of each path. When off, runs of framework types (System, Microsoft and similar) between your own steps fold away.";

    /// <summary>Compare: two snapshots: Baseline and Current.</summary>
    public const string CompareSnapshots =
        "Baseline is the earlier snapshot, and each change is current minus baseline. The two latest captures are chosen for you by capture time, or by open order when a capture time is only a file's time. Swap exchanges them.";

    /// <summary>Compare: two snapshots: The options row.</summary>
    public const string CompareOptions =
        "Group by adds up rows by type, namespace, assembly or interface. Only type rows open objects or sets here, and interface rows need full dumps and have no retained sizes.\n" +
        "New and dead objects matches objects across the two snapshots by address, type, size and heap area, and an object a collection moved counts as dead and new. It needs two complete full dumps of one run of a process, with the earlier one as baseline; if not, a note says why.\n" +
        "Retained sizes adds each row's retained bytes, what its objects keep alive, in both snapshots, as lower bounds. Interface rows have none, and the first time for a snapshot it has to compute them, which can take a while.\n" +
        "Generations adds the change in bytes per heap area: Gen0, Gen1, Gen2, LOH and POH. It needs generations in both snapshots, so not a gcdump. Growth in Gen2 is usually what stays, though a full dump also counts objects not collected yet.";

    /// <summary>Compare: trend: The options row.</summary>
    public const string TrendOptions =
        "The tick boxes choose the open snapshots to line up, at least 2 and at most 20. Changing a tick runs the trend again.\n" +
        "Group by adds up rows by type, namespace, assembly or interface. Interface is always offered here, and it needs every ticked snapshot to be a full dump.";

    /// <summary>Inspections: Leak detectors, Memory waste headings.</summary>
    public const string Detectors =
        "Leak detectors look for objects that may be kept alive by mistake, and Memory waste for memory used poorly. A finding is a lead to check, not a verdict.";

    /// <summary>Inspections: Fragmentation heading.</summary>
    public const string Fragmentation =
        "Free gaps between live objects, as a share of each heap area, and what is pinned there. Pinned objects stop the runtime closing gaps around them, and a gcdump has no such figures.";

    /// <summary>Retainers: The chart.</summary>
    public const string RetainersChart =
        "Each bar is what the bar above it keeps alive, and its width is how much. The two band colors only separate the levels; a bar and its path turn amber when selected.\n" +
        "Hover for a bar's name and size, click to select it, double-click to show the tree below it. Bars too thin to point at are grouped as +N smaller.";

    /// <summary>Retainers: The buttons above the tree.</summary>
    public const string RetainersTree =
        "Retained is what the row alone keeps alive, % is its share of the reachable heap, Bytes the objects' own size, and Objects how many the row stands for. Each level lists 100 rows before a \"more\" row.\n" +
        "Select a row, then:\n" +
        "Focus shows only the tree below it, with the objects above it as a trail. Object rows only.\n" +
        "Open object opens it on the Snapshot tab. Object rows only.\n" +
        "Instances lists all objects of its type on the Snapshot tab. Object rows and type groups.\n" +
        "Why alive opens its Retention tab: the paths that keep it alive from roots, what the runtime always keeps. Object rows only.\n" +
        "Objects retained by this set computes exactly what every object of its type keeps alive together, shown below the tree. Object rows and type groups.";

    /// <summary>Allocations: Back traces and Call tree.</summary>
    public const string AllocationTrees =
        "Back traces starts from the methods that allocated, then goes up through their callers.\n" +
        "Call tree starts from each thread's outermost frames and goes down.\n" +
        "Total is the estimated bytes of the stacks through this row, and % its share of the bytes shown. Own is what the method allocated itself, and Samples how many allocation samples the row rests on.";

    /// <summary>Live: Snapshot rules heading.</summary>
    public const string LiveRules =
        "Rules take snapshots by themselves during a run. Each filled field is its own rule, so any one can fire, and they are read when you press Start.\n" +
        "Snapshot every takes one on a schedule, counted from the first sample, in minutes such as 5 or 0.5, whatever the heap's size.\n" +
        "when above takes one when the heap as of the last garbage collection reaches this size, such as 500MB or 2GB. Before the first collection, it uses the heap now.\n" +
        "or grown by takes one when that heap has grown this much over the first sample: a size such as 100MB, or a percent such as 50%.\n" +
        "then says what the two size rules do after firing: below fires again once the heap falls under 90 percent of the threshold, once never fires again, and step raises the threshold by its own amount.\n" +
        "at most is the most snapshots the rules may take in a run; empty means no limit. Get snapshot and failed captures do not count.\n" +
        "as gcdumps makes this run's snapshots gcdumps, with only objects and references, instead of full dumps. Each one runs a full garbage collection in the process.\n" +
        "Record allocations records sampled allocations with call stacks for the whole run, up to 1 GB. After the run, press Open allocations under Past runs.";

    /// <summary>Live: Past runs heading.</summary>
    public const string LivePastRuns =
        "The monitoring runs of the current session; press Refresh to list them.\n" +
        "Show draws the selected run's chart and snapshots, read only, and is off while a run is going.\n" +
        "Open allocations opens the allocation trace of the last run, or of the past run shown, on the Allocations page. It needs a run that recorded allocations.";

    /// <summary>Live: The chart legend.</summary>
    public const string LiveChart =
        "The chart stacks the heap areas as of the last collection, with what was allocated since then on top. A dashed line marks a snapshot and a dotted line a forced collection.\n" +
        "The line above it is the latest sample: the heap now, the heap after the last collection and its parts, bytes allocated, collections per generation, and the last collection's share of time.";

    /// <summary>The Types options in a set, where only Group by and Generations show.</summary>
    public const string TypesOptionsInSet =
        "Group by adds up the rows by type, namespace, assembly or interface. Interface needs a full dump, and a type counts under each interface it implements, so those rows overlap.\n" +
        "Generations adds a column per heap area. It needs a full dump, because a gcdump records no generations.";
}
