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

namespace HeapScope.Contracts;

/// <summary>How a node's children are shown in the dominator tree (sprint 2.4).</summary>
public enum DominatorGroupingDto
{
    /// <summary>Each child object on its own row.</summary>
    None = 0,

    /// <summary>Children grouped by type (with its module); a group's retained size is exact, since siblings retain disjoint sets.</summary>
    Type = 1,

    /// <summary>Children grouped by namespace.</summary>
    Namespace = 2,
}

/// <summary>
/// The dominator tree's row keys (sprint 2.4), for clients that link into the tree: an object's is <c>o:</c> and its
/// address in hex; a group's extends its list's key (the top level's is empty) with <c>|g:</c> and the group's name (a
/// type's name and module file name in brackets, or a namespace); a "more" row's extends it with <c>|m:</c> and how many
/// rows precede it. Contracts owns the format; the worker's tree mirrors it (ADR-016, a test checks every form).
/// </summary>
public static class DominatorTreeKeys
{
    /// <summary>The top level's list key.</summary>
    public const string Root = "";

    public static string Object(ulong address) => "o:" + address.ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A group's name as its key carries it.</summary>
    public static string GroupName(string name, string? module) => string.IsNullOrEmpty(module) ? name : name + " [" + module + "]";

    /// <summary>A type's group in the list <paramref name="listKey"/>, when children are grouped by type.</summary>
    public static string TypeGroup(string listKey, string typeName, string? module) => listKey + "|g:" + GroupName(typeName, module);

    /// <summary>A namespace's group in the list <paramref name="listKey"/>, when children are grouped by namespace.</summary>
    public static string NamespaceGroup(string listKey, string ns) => listKey + "|g:" + ns;

    /// <summary>The "more" row of the list <paramref name="listKey"/> after <paramref name="shown"/> rows.</summary>
    public static string More(string listKey, int shown) => listKey + "|m:" + shown.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The kinds of a dominator tree row (<see cref="DominatorTreeColumns.Kind"/>).</summary>
public static class DominatorRowKinds
{
    public const string Object = "Object";

    public const string Group = "Group";

    public const string More = "More";
}

/// <summary>The dominator tree query's limits (sprint 2.4), so clients can keep within them and say when they were met.</summary>
public static class DominatorTreeLimits
{
    /// <summary>Rows in one answer; beyond it the answer is cut (<see cref="DominatorTreeResult.Truncated"/>).</summary>
    public const int MaxRows = 50_000;

    /// <summary>Keys one request may expand.</summary>
    public const int MaxExpanded = 10_000;

    /// <summary>The range of children per node before a "more" row.</summary>
    public const int MinChildren = 1;

    public const int MaxChildren = 1_000;

    /// <summary>The deepest level shown; expansions below it are not followed.</summary>
    public const int MaxDepth = 1_000;

    /// <summary>The focus's dominators sent: the top ones and the nearest ones (<see cref="DominatorTreeResult.AncestorDepth"/> has them all).</summary>
    public const int TrailHead = 3;

    public const int TrailTail = 20;
}

/// <summary>Column names of a dominator tree result set (sprint 2.4).</summary>
public static class DominatorTreeColumns
{
    /// <summary>The row's key: <c>o:&lt;address&gt;</c> for an object, the parent's key plus the group for a group, <c>m:</c> for a "more" row.</summary>
    public const string Key = "Key";

    /// <summary>The parent row's key; empty at the top level.</summary>
    public const string ParentKey = "ParentKey";

    /// <summary>The indentation level, 0 at the top.</summary>
    public const string Depth = "Depth";

    /// <summary><c>Object</c>, <c>Group</c> or <c>More</c>.</summary>
    public const string Kind = "Kind";

    /// <summary>The object's type, the group's name, or what a "more" row stands for.</summary>
    public const string Name = "Name";

    /// <summary>The type's module file name (objects and type groups).</summary>
    public const string Module = "Module";

    /// <summary>The object's address (objects only).</summary>
    public const string Address = "Address";

    /// <summary>Objects the row stands for: 1 for an object, the members of a group or of a "more" row.</summary>
    public const string Objects = "Objects";

    /// <summary>Bytes the row retains (exact: dominator subtrees are disjoint).</summary>
    public const string Retained = "Retained";

    /// <summary>The objects' own bytes.</summary>
    public const string Bytes = "Bytes";

    /// <summary>The retained bytes as a share of the reachable heap.</summary>
    public const string Percent = "Percent";

    /// <summary>How many children expanding the row shows (objects, or members).</summary>
    public const string Children = "Children";

    /// <summary>1 when the row is expanded in this answer.</summary>
    public const string Expanded = "Expanded";
}

/// <summary>
/// The dominator tree as a flattened, paged list (sprint 2.4, ADR-027): from the virtual root (the biggest retainers are
/// its top level) or from <paramref name="FocusAddress"/>, with the rows of <paramref name="Expanded"/> keys expanded, each
/// node's children as objects or groups, at most <paramref name="MaxChildren"/> per node plus a "more" row (expanding it
/// shows the next ones). <paramref name="RevealKey"/> asks where a row landed (the one just expanded or linked to), so
/// the client can keep the user's place (<see cref="DominatorTreeResult.RevealIndex"/>; the 2.4 review).
/// </summary>
public sealed record DominatorTreeRequest(
    Guid SnapshotId,
    IReadOnlyList<string> Expanded,
    DominatorGroupingDto Grouping = DominatorGroupingDto.None,
    ulong FocusAddress = 0,
    int MaxChildren = 100,
    string? RevealKey = null);

/// <summary>An ancestor of the focus in the dominator tree, from the top: its key, type, address and retained bytes.</summary>
public sealed record DominatorAncestorDto(string Key, string Name, ulong Address, long Retained);

/// <summary>
/// The answer to a dominator tree query: the rows, the focus (none for the root) and its ancestors, the bytes the focus
/// retains and the reachable heap's total, the number of top-level dominators, whether the rows were cut at the row
/// limit, and how the dominators were obtained. <see cref="Ancestors"/> holds at most
/// <see cref="DominatorTreeLimits.TrailHead"/> from the top and <see cref="DominatorTreeLimits.TrailTail"/> nearest the
/// focus; <see cref="AncestorDepth"/> counts them all, so the ones between are <c>AncestorDepth - Ancestors.Count</c>,
/// after the first <see cref="DominatorTreeLimits.TrailHead"/>. <see cref="RevealIndex"/> is the row of the request's
/// <see cref="DominatorTreeRequest.RevealKey"/>, or -1 when it is not shown.
/// </summary>
public sealed record DominatorTreeResult(
    ResultSetInfo Rows,
    string? FocusKey,
    IReadOnlyList<DominatorAncestorDto> Ancestors,
    long FocusRetained,
    long TotalRetained,
    int TopLevel,
    bool Truncated,
    DominatorIndexInfo Dominators,
    int AncestorDepth = 0,
    int RevealIndex = -1);

/// <summary>Column names of an "objects retained by this set" result set (sprint 2.4): one row per retained type.</summary>
public static class RetainedBySetColumns
{
    /// <summary>The type's display name (with its module when another type has the same name); the key.</summary>
    public const string Name = "Name";

    /// <summary>Retained objects of the type.</summary>
    public const string Objects = "Objects";

    /// <summary>Their bytes (exact).</summary>
    public const string Retained = "Retained";

    /// <summary>The bytes as a share of everything the set retains.</summary>
    public const string Percent = "Percent";

    /// <summary>The type's name, which opens its instances (hidden).</summary>
    public const string TypeName = "TypeName";

    /// <summary>The type's module file name, with <see cref="TypeName"/> (hidden).</summary>
    public const string Module = "Module";
}

/// <summary>
/// "Objects retained by this set" (sprint 2.4): what every object of a type (by name and module, as in
/// <see cref="InstancesRequest"/>) keeps alive together, exactly; or, with <paramref name="Set"/> (sprint 3.1), what the
/// members of any object set keep alive together (the type name is then ignored).
/// </summary>
public sealed record RetainedBySetRequest(Guid SnapshotId, string TypeName, string? Module, ObjectSetSpec? Set = null);

/// <summary>
/// The answer to <see cref="RetainedBySetRequest"/>: the retained objects by type, largest first (<see cref="RetainedBySetColumns"/>);
/// how many type table entries the set's members have (the types that matched, for a set of types); the set's reachable objects and bytes (<see cref="AllObjects"/> counts the ones
/// no root reaches too, which retain nothing); the objects and bytes the set retains
/// (exact, the set included); the type summary's lower bound for the same types, to compare (0 for a set that is not
/// whole types); the reachable heap's bytes;
/// the seconds the reachability pass took; and how the dominators were obtained.
/// </summary>
public sealed record RetainedBySetResult(
    ResultSetInfo Types,
    int MatchedTypes,
    long SetObjects,
    long SetBytes,
    long RetainedObjects,
    long RetainedBytes,
    long LowerBoundBytes,
    long TotalRetained,
    double Seconds,
    DominatorIndexInfo Dominators,
    long AllObjects = 0);

/// <summary>
/// The icicle chart of the dominator tree (sprint 2.4): <paramref name="Levels"/> levels below <paramref name="FocusAddress"/>
/// (0 for the whole reachable heap), leaving out bars narrower than <paramref name="MinShare"/> of the top bar.
/// </summary>
public sealed record IcicleRequest(Guid SnapshotId, ulong FocusAddress = 0, int Levels = 6, double MinShare = 0.001);

/// <summary>
/// A bar of the icicle chart: its level (0 at the top), its left edge and width in bytes (the width is what it retains),
/// its tree key (as in <see cref="DominatorTreeColumns.Key"/>; empty for the whole heap), its type and its address.
/// </summary>
public sealed record IcicleSegmentDto(int Level, long Offset, long Width, string Key, string Name, ulong Address);

/// <summary>The icicle chart's bars in level order, left to right; the top bar's bytes; how the dominators were obtained.</summary>
public sealed record IcicleResult(IReadOnlyList<IcicleSegmentDto> Segments, long Total, DominatorIndexInfo Dominators);
