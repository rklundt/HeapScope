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

        _logger.Lines.Count.ShouldBe(2);
        _logger.Lines[0].ShouldContain("result set in memory, 2 rows, 3 columns, values True");
        _logger.Lines[1].ShouldContain("Instances grid to CSV: 2 of 2 rows, 3 columns, values True");
        foreach (var line in _logger.Lines)
        {
            line.ShouldNotContain("private-name");
            line.ShouldNotContain("secret");
            line.ShouldNotContain("App.Order");
        }
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
        _interaction.Messages.ShouldHaveSingleItem().ShouldContain("No file was written");
        _logger.Lines.ShouldAllBe(l => !l.Contains("missing-folder", StringComparison.Ordinal));
        service.IsExporting.ShouldBeFalse();
    }

    [Fact]
    public async Task A_grid_whose_query_was_replaced_says_to_export_again()
    {
        _interaction.SaveFile = Path.Combine(_folder, "out.csv");
        var service = new GridExportService(_interaction, _logger);

        await service.ExportAsync("Types", new FakeGridExport(withValues: false) { Throw = new ObjectDisposedException("rows") }, Ct);

        service.Status.ShouldBe("The grid changed during the export, so no file was written; export it again.");
        Directory.GetFiles(_folder).ShouldBeEmpty("no short file is left behind");
    }

    [Fact]
    public async Task Cancel_stops_the_export_and_leaves_no_file()
    {
        _interaction.SaveFile = Path.Combine(_folder, "out.csv");
        var service = new GridExportService(_interaction, _logger);
        var started = new TaskCompletionSource();
        var grid = new FakeGridExport(withValues: false) { Started = started };

        var export = service.ExportAsync("Types", grid, Ct);
        await started.Task;
        service.IsExporting.ShouldBeTrue();
        service.CancelCommand.Execute(null);

        (await export).ShouldBeNull();
        service.Status.ShouldBe("Export canceled; no file was written.");
        Directory.GetFiles(_folder).ShouldBeEmpty();
        service.CancelCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_export_while_one_runs_is_refused_before_any_dialog()
    {
        _interaction.SaveFile = Path.Combine(_folder, "out.csv");
        var service = new GridExportService(_interaction, _logger);
        var started = new TaskCompletionSource();
        var first = service.ExportAsync("Types", new FakeGridExport(withValues: false) { Started = started }, Ct);
        await started.Task;

        (await service.ExportAsync("Types", new FakeGridExport(withValues: false), Ct)).ShouldBeNull();

        _interaction.SuggestedNames.Count.ShouldBe(1);
        service.CancelCommand.Execute(null);
        await first;
    }

    private sealed class FakeGridExport(bool withValues, bool empty = false) : IGridExport
    {
        private static async Task<long> WaitForCancelAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public Exception? Throw { get; init; }

        /// <summary>Set once writing has started; the write then waits for cancellation.</summary>
        public TaskCompletionSource? Started { get; init; }

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

            if (Started is not null)
            {
                Started.TrySetResult();
                return WaitForCancelAsync(cancellationToken);
            }

            IReadOnlyList<string?>[] rows = withValues ? [["App.Order", "24", "secret"], ["App.Line", "16", "secret"]] : [["App.Order", "24"], ["App.Line", "16"]];
            return CsvExport.WriteAsync(output, Columns, rows, includeValues, cancellationToken);
        }
    }
}
