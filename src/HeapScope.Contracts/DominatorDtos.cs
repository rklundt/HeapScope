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
