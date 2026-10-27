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

using HeapScope.Application.Exports;
using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

/// <summary>CSV exports of grids (sprint 2.7, ADR-026).</summary>
public sealed class CsvExportTests
{
    private static readonly ResultColumn[] s_columns =
    [
        new("Address", ResultColumnKind.Address, IsKey: true),
        new("Type", ResultColumnKind.Text),
        new("Bytes", ResultColumnKind.Bytes),
        new("Value", ResultColumnKind.Text, HasValues: true),
        new("Module", ResultColumnKind.Text, IsHidden: true),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hidden_and_value_columns_are_left_out_by_default()
    {
        var text = await Write([["0x10", "App.Order", "24", "secret", "App.dll"]], includeValues: false);

        text.ShouldBe("Address,Type,Bytes\r\n0x10,App.Order,24\r\n".Replace("\r\n", Environment.NewLine, StringComparison.Ordinal));
        text.ShouldNotContain("secret");
        text.ShouldNotContain("App.dll");
    }

    [Fact]
    public async Task Values_opted_in_are_written_and_the_first_line_says_so()
    {
        var lines = Lines(await Write([["0x10", "App.Order", "24", "hello", "App.dll"]], includeValues: true));

        lines[0].ShouldBe(CsvExport.ValuesNotice);
        lines[1].ShouldBe("Address,Type,Bytes,Value");
        lines[2].ShouldBe("0x10,App.Order,24,hello");
    }

    [Fact]
    public async Task Opting_in_on_a_grid_without_values_adds_no_notice()
    {
        ResultColumn[] columns = [new("Type", ResultColumnKind.Text), new("Bytes", ResultColumnKind.Bytes)];
        using var writer = new StringWriter();

        await CsvExport.WriteAsync(writer, columns, [["App.Order", "24"]], includeValues: true, Ct);

        Lines(writer.ToString())[0].ShouldBe("Type,Bytes");
        CsvExport.HasValues(columns).ShouldBeFalse();
        CsvExport.HasValues(s_columns).ShouldBeTrue();
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-x", "'-x")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("=a,b", "\"'=a,b\"")]
    [InlineData("List<int>", "List<int>")]
    public void Text_cells_are_quoted_and_formulas_shown_as_text(string value, string expected) =>
        CsvExport.Cell(value, ResultColumnKind.Text).ShouldBe(expected);

    [Fact]
    public void Numbers_keep_their_sign()
    {
        CsvExport.Cell("-1024", ResultColumnKind.SignedInteger).ShouldBe("-1024");
        CsvExport.Cell(null, ResultColumnKind.Bytes).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Every_page_is_fetched_not_only_the_loaded_rows()
    {
        const int count = CsvExport.PageSize * 2 + 5;
        var calls = new List<(int Offset, int Count)>();
        using var writer = new StringWriter();

        var written = await CsvExport.WriteAsync(writer, s_columns, count, (offset, take, _) =>
        {
            calls.Add((offset, take));
            IReadOnlyList<string?>[] rows = [.. Enumerable.Range(offset, take).Select(i => (IReadOnlyList<string?>)[$"0x{i:x}", "T", "8", null, null])];
            return Task.FromResult(new ResultPage(Guid.Empty, offset, count, rows));
        }, includeValues: false, Ct);

        written.ShouldBe(count);
        calls.ShouldBe([(0, CsvExport.PageSize), (CsvExport.PageSize, CsvExport.PageSize), (CsvExport.PageSize * 2, 5)]);
        Lines(writer.ToString()).Length.ShouldBe(count + 1);
    }

    [Fact]
    public async Task Headers_say_when_numbers_are_lower_bounds_or_estimates()
    {
        ResultColumn[] columns = [new("Type", ResultColumnKind.Text), new("Retained", ResultColumnKind.Bytes, Bound: ValueBound.LowerBound), new("Bytes", ResultColumnKind.Bytes, Bound: ValueBound.Estimate)];
        using var writer = new StringWriter();

        await CsvExport.WriteAsync(writer, columns, [], includeValues: false, Ct);

        Lines(writer.ToString())[0].ShouldBe("Type,Retained (at least),Bytes (estimated)");
    }

    [Fact]
    public async Task A_canceled_export_stops()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        using var writer = new StringWriter();

        await Should.ThrowAsync<OperationCanceledException>(() => CsvExport.WriteAsync(writer, s_columns, [["0x10", "T", "8", null, null]], includeValues: false, cancel.Token));
    }

    private static async Task<string> Write(IEnumerable<IReadOnlyList<string?>> rows, bool includeValues)
    {
        using var writer = new StringWriter();
        await CsvExport.WriteAsync(writer, s_columns, rows, includeValues, Ct);
        return writer.ToString();
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
