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

using HeapScope.Application.Presentation;
using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

/// <summary>Reading a worker result's cells by column name, as the app and the CLI both do (sprint 3.8, P10 and P18).</summary>
public sealed class ResultColumnsTests
{
    private static readonly ResultColumn[] s_columns =
    [
        new("Name", ResultColumnKind.Text, IsKey: true),
        new("Objects", ResultColumnKind.Integer),
        new("Percent", ResultColumnKind.Percent),
        new("Address", ResultColumnKind.Address),
        new("Retained", ResultColumnKind.Bytes),
    ];

    private static readonly string?[] s_row = ["App.Entry", "2000", "2.39", "0x1f2a", null];

    [Fact]
    public void Find_gives_a_column_index_or_minus_one_and_Require_refuses_a_missing_column()
    {
        ResultColumns.Find(s_columns, "Objects").ShouldBe(1);
        ResultColumns.Find(s_columns, "Bytes").ShouldBe(-1);
        ResultColumns.Require(s_columns, "Retained").ShouldBe(4);

        Should.Throw<InvalidOperationException>(() => ResultColumns.Require(s_columns, "Bytes")).Message.ShouldContain("no Bytes column");
    }

    [Fact]
    public void Cells_read_invariant_numbers_and_take_a_missing_column_or_blank_as_zero_or_empty()
    {
        Cells.Long(s_row, 1).ShouldBe(2000);
        Cells.Double(s_row, 2).ShouldBe(2.39);
        Cells.Text(s_row, 0).ShouldBe("App.Entry");
        Cells.Address("0x1f2a").ShouldBe(0x1f2aUL);

        (Cells.Long(s_row, -1), Cells.Long(s_row, 4), Cells.Text(s_row, 4), Cells.Double(s_row, 9), Cells.Address("1f2a")).ShouldBe((0L, 0L, string.Empty, 0.0, 0UL));
    }

    [Fact]
    public void A_column_map_reads_by_name_whatever_the_column_order()
    {
        var map = new ColumnMap(s_columns);
        var reversed = new ColumnMap([.. s_columns.Reverse()]);
        string?[] reversedRow = [.. s_row.Reverse()];

        (map.Long(s_row, "Objects"), map.Text(s_row, "Name")).ShouldBe((2000L, "App.Entry"));
        (reversed.Long(reversedRow, "Objects"), reversed.Text(reversedRow, "Name")).ShouldBe((2000L, "App.Entry"));
        (map.Has("Retained"), map.Has("Bytes"), map.IndexOf("Percent"), map.IndexOf("Bytes")).ShouldBe((true, false, 2, -1));
    }

    [Fact]
    public void A_column_map_refuses_a_missing_column_and_a_blank_number_unless_asked_for_null()
    {
        var map = new ColumnMap(s_columns);

        Should.Throw<InvalidOperationException>(() => map.Text(s_row, "Bytes"));
        Should.Throw<ArgumentNullException>(() => map.Long(s_row, "Retained"));
        map.LongOrNull(s_row, "Retained").ShouldBeNull();
        map.LongOrNull(s_row, "Objects").ShouldBe(2000);
    }
}
