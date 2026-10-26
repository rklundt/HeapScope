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
/// shows the next ones).
/// </summary>
public sealed record DominatorTreeRequest(
    Guid SnapshotId,
    IReadOnlyList<string> Expanded,
    DominatorGroupingDto Grouping = DominatorGroupingDto.None,
    ulong FocusAddress = 0,
    int MaxChildren = 100);

/// <summary>An ancestor of the focus in the dominator tree, from the top: its key, type, address and retained bytes.</summary>
public sealed record DominatorAncestorDto(string Key, string Name, ulong Address, long Retained);

/// <summary>
/// The answer to a dominator tree query: the rows, the focus (none for the root) and its ancestors, the bytes the focus
/// retains and the reachable heap's total, the number of top-level dominators, whether the rows were cut at the row
/// limit, and how the dominators were obtained.
/// </summary>
public sealed record DominatorTreeResult(
    ResultSetInfo Rows,
    string? FocusKey,
    IReadOnlyList<DominatorAncestorDto> Ancestors,
    long FocusRetained,
    long TotalRetained,
    int TopLevel,
    bool Truncated,
    DominatorIndexInfo Dominators);

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
/// <see cref="InstancesRequest"/>) keeps alive together, exactly.
/// </summary>
public sealed record RetainedBySetRequest(Guid SnapshotId, string TypeName, string? Module);

/// <summary>
/// The answer to <see cref="RetainedBySetRequest"/>: the retained objects by type, largest first (<see cref="RetainedBySetColumns"/>);
/// how many type table entries matched; the set's reachable objects and bytes; the objects and bytes the set retains
/// (exact, the set included); the type summary's lower bound for the same type, to compare; the reachable heap's bytes;
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
    DominatorIndexInfo Dominators);

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
