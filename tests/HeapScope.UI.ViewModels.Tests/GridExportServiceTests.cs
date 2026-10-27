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
using HeapScope.Application.Tests;
using HeapScope.Contracts;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>Exporting a grid to CSV (sprint 2.7, ADR-026).</summary>
public sealed class GridExportServiceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-export-").FullName;
    private readonly FakeUserInteraction _interaction = new();
    private readonly RecordingLogger<GridExportService> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_grid_is_written_to_the_picked_file_and_the_status_says_so()
    {
        _interaction.SaveFile = Path.Combine(_folder, "out.csv");
        var service = new GridExportService(_interaction, _logger);

        var rows = await service.ExportAsync("Types", new FakeGridExport(withValues: false), Ct);

        rows.ShouldBe(2);
        _interaction.SuggestedNames.ShouldBe(["Types.csv"]);
        _interaction.Questions.ShouldBeEmpty("no values to ask about");
        var text = await File.ReadAllTextAsync(_interaction.SaveFile, Ct);
        text.ShouldStartWith("Type,Bytes");
        (await File.ReadAllBytesAsync(_interaction.SaveFile, Ct))[..3].ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF }, "a BOM for spreadsheets");
        service.Status.ShouldBe("Exported 2 rows to out.csv.");
    }

    [Fact]
    public async Task Values_are_left_out_unless_the_person_says_yes()
    {
        _interaction.SaveFile = Path.Combine(_folder, "values.csv");
        _interaction.Answer = false;
        var service = new GridExportService(_interaction, _logger);

        await service.ExportAsync("Instances", new FakeGridExport(withValues: true), Ct);
        var without = await File.ReadAllTextAsync(_interaction.SaveFile, Ct);
        _interaction.Answer = true;
        await service.ExportAsync("Instances", new FakeGridExport(withValues: true), Ct);
        var with = await File.ReadAllTextAsync(_interaction.SaveFile, Ct);

        _interaction.Questions.ShouldBe([GridExportService.ValuesQuestion, GridExportService.ValuesQuestion]);
        without.ShouldNotContain("secret");
        with.ShouldStartWith(CsvExport.ValuesNotice);
        with.ShouldContain("secret");
        service.Status!.ShouldEndWith(", with values.");
    }

    [Fact]
    public async Task The_log_names_the_grid_and_counts_but_never_the_path_or_a_cell()
    {
        _interaction.SaveFile = Path.Combine(_folder, "private-name.csv");
        var service = new GridExportService(_interaction, _logger);

        await service.ExportAsync("Instances", new FakeGridExport(withValues: true), Ct);

        var line = _logger.Lines.ShouldHaveSingleItem();
        line.ShouldContain("Instances");
        line.ShouldContain("2 rows, 3 columns, values True");
        line.ShouldNotContain("private-name");
        line.ShouldNotContain("secret");
        line.ShouldNotContain("App.Order");
    }

    [Fact]
    public async Task Canceling_the_picker_or_an_empty_grid_writes_nothing()
    {
        var service = new GridExportService(_interaction, _logger);

        (await service.ExportAsync("Types", new FakeGridExport(withValues: false), Ct)).ShouldBeNull();
        (await service.ExportAsync("Types", new FakeGridExport(withValues: false, empty: true), Ct)).ShouldBeNull();

        service.Status.ShouldBe("Nothing to export: the grid is empty.");
        Directory.GetFiles(_folder).ShouldBeEmpty();
        _logger.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_is_reported_without_its_path_in_the_log()
    {
        _interaction.SaveFile = Path.Combine(_folder, "missing-folder", "out.csv");
        var service = new GridExportService(_interaction, _logger);

        (await service.ExportAsync("Types", new FakeGridExport(withValues: false), Ct)).ShouldBeNull();

        service.Status!.ShouldStartWith("Export failed: ");
        _interaction.Messages.ShouldHaveSingleItem();
        _logger.Lines.ShouldHaveSingleItem().ShouldNotContain("missing-folder");
        service.IsExporting.ShouldBeFalse();
    }

    [Fact]
    public async Task A_grid_whose_query_was_replaced_says_to_export_again()
    {
        _interaction.SaveFile = Path.Combine(_folder, "out.csv");
        var service = new GridExportService(_interaction, _logger);

        await service.ExportAsync("Types", new FakeGridExport(withValues: false) { Throw = new ObjectDisposedException("rows") }, Ct);

        service.Status.ShouldBe("The grid changed during the export; export it again.");
    }

    private sealed class FakeGridExport(bool withValues, bool empty = false) : IGridExport
    {
        public Exception? Throw { get; init; }

        public IReadOnlyList<ResultColumn> Columns { get; } = empty ? [] :
            withValues
                ? [new("Type", ResultColumnKind.Text), new("Bytes", ResultColumnKind.Bytes), new("Value", ResultColumnKind.Text, HasValues: true)]
                : [new("Type", ResultColumnKind.Text), new("Bytes", ResultColumnKind.Bytes)];

        public int RowCount => 2;

        public Task<long> WriteCsvAsync(TextWriter output, bool includeValues, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            IReadOnlyList<string?>[] rows = withValues ? [["App.Order", "24", "secret"], ["App.Line", "16", "secret"]] : [["App.Order", "24"], ["App.Line", "16"]];
            return CsvExport.WriteAsync(output, Columns, rows, includeValues, cancellationToken);
        }
    }
}
