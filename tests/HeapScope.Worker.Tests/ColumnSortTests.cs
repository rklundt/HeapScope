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

using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>The sort-by-column shared by the result sources (sprint 1.9).</summary>
public class ColumnSortTests
{
    private static readonly ColumnSort<(string Name, int Size)> s_sort = new ColumnSort<(string Name, int Size)>(r => r.Name)
        .On("Size", r => r.Size)
        .On("Name", (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Sorts_by_the_column_either_way_and_breaks_ties_by_name()
    {
        List<(string Name, int Size)> rows = [("b", 2), ("c", 1), ("a", 2)];

        s_sort.Sort(rows, "Size", descending: true);
        rows.Select(r => r.Name).ShouldBe(["a", "b", "c"]);

        s_sort.Sort(rows, "Size", descending: false);
        rows.Select(r => r.Name).ShouldBe(["c", "a", "b"]);
    }

    [Fact]
    public void An_unknown_column_is_refused_with_the_sortable_ones()
    {
        var ex = Should.Throw<ArgumentException>(() => s_sort.Validate("Color"));

        ex.Message.ShouldBe("Unknown sort column Color. Sort by one of: Size, Name.");
    }
}
