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
/// What each page and tab's info icon says (ADR-040): what it is for, when to use it and how to start. The product owner approved these
/// on 2026-11-05, each checked against the code (`docs/design/info-text.md`). A change to what a page does may change its text here.
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
}
