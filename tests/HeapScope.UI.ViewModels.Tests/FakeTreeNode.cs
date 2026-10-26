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

/// <summary>A node of the fake worker's dominator tree (sprint 2.4), formatted as the worker's columns.</summary>
public sealed record FakeTreeNode(string Key, string ParentKey, string Name, ulong Address, long Retained, string Kind = "Object", long Objects = 1, string? Module = "App.dll")
{
    /// <summary>The row's cells in <see cref="DominatorTreeColumns"/> order.</summary>
    public IReadOnlyList<string?> Cells(int depth, bool expanded) =>
    [
        Key, ParentKey, depth.ToString(CultureInfo.InvariantCulture), Kind, Name, Module,
        Address == 0 ? null : "0x" + Address.ToString("x", CultureInfo.InvariantCulture),
        Objects.ToString(CultureInfo.InvariantCulture), Retained.ToString(CultureInfo.InvariantCulture), "16", "1", "2", expanded ? "1" : "0",
    ];
}
