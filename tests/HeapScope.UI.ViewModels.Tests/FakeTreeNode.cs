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

using System.Globalization;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>
/// A node of the fake worker's dominator tree (sprint 2.4), formatted by column name (2.3.7 review M15): a row's cells
/// come from <see cref="DominatorTreeColumns"/> names, in whatever order the fake's columns list them.
/// </summary>
public sealed record FakeTreeNode(string Key, string ParentKey, string Name, ulong Address, long Retained, string Kind = DominatorRowKinds.Object, long Objects = 1, string? Module = "App.dll", long Bytes = 16)
{
    /// <summary>The worker's tree columns, in the worker's order.</summary>
    public static readonly ResultColumn[] Columns =
    [
        new(DominatorTreeColumns.Key, ResultColumnKind.Text, IsKey: true), new(DominatorTreeColumns.ParentKey, ResultColumnKind.Text),
        new(DominatorTreeColumns.Depth, ResultColumnKind.Integer), new(DominatorTreeColumns.Kind, ResultColumnKind.Text),
        new(DominatorTreeColumns.Name, ResultColumnKind.Text), new(DominatorTreeColumns.Module, ResultColumnKind.Text),
        new(DominatorTreeColumns.Address, ResultColumnKind.Address), new(DominatorTreeColumns.Objects, ResultColumnKind.Integer),
        new(DominatorTreeColumns.Retained, ResultColumnKind.Bytes), new(DominatorTreeColumns.Bytes, ResultColumnKind.Bytes),
        new(DominatorTreeColumns.Percent, ResultColumnKind.Percent), new(DominatorTreeColumns.Children, ResultColumnKind.Integer),
        new(DominatorTreeColumns.Expanded, ResultColumnKind.Integer),
    ];

    /// <summary>The row's cells, by column name, with its real child count and share of <paramref name="total"/>.</summary>
    public IReadOnlyList<string?> Cells(int depth, bool expanded, int children, long total)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DominatorTreeColumns.Key] = Key,
            [DominatorTreeColumns.ParentKey] = ParentKey,
            [DominatorTreeColumns.Depth] = depth.ToString(CultureInfo.InvariantCulture),
            [DominatorTreeColumns.Kind] = Kind,
            [DominatorTreeColumns.Name] = Name,
            [DominatorTreeColumns.Module] = Module,
            [DominatorTreeColumns.Address] = Address == 0 ? null : "0x" + Address.ToString("x", CultureInfo.InvariantCulture),
            [DominatorTreeColumns.Objects] = Objects.ToString(CultureInfo.InvariantCulture),
            [DominatorTreeColumns.Retained] = Retained.ToString(CultureInfo.InvariantCulture),
            [DominatorTreeColumns.Bytes] = Bytes.ToString(CultureInfo.InvariantCulture),
            [DominatorTreeColumns.Percent] = (total == 0 ? 0 : 100.0 * Retained / total).ToString("0.##", CultureInfo.InvariantCulture),
            [DominatorTreeColumns.Children] = children.ToString(CultureInfo.InvariantCulture),
            [DominatorTreeColumns.Expanded] = expanded ? "1" : "0",
        };
        return [.. Columns.Select(c => values[c.Name])];
    }
}
