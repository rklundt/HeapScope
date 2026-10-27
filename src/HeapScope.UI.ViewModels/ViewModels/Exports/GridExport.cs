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

using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Application.Exports;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// A grid's rows as an export (sprint 2.7): every row, not only those a virtualizing grid has loaded. A worker-backed
/// grid's row list is one (<see cref="VirtualRowList{TRow}"/>); a grid of rows held in memory is exported by the view.
/// </summary>
public interface IGridExport
{
    /// <summary>The columns, flagged hidden or carrying values; empty when nothing is shown.</summary>
    IReadOnlyList<ResultColumn> Columns { get; }

    /// <summary>The rows the export writes.</summary>
    int RowCount { get; }

    /// <summary>Writes the rows as CSV (<see cref="CsvExport"/>); returns the rows written.</summary>
    Task<long> WriteCsvAsync(TextWriter output, bool includeValues, CancellationToken cancellationToken);
}

/// <summary>
/// Exports a grid to a CSV file the person picks (sprint 2.7, ADR-026): asks for the file, asks whether to include
/// values when the grid has any (no unless they say yes), writes off the dispatcher and says how it went. The log names
/// the grid, the rows and columns, the opt-in and the time; never the file's path or a cell.
/// </summary>
public sealed partial class GridExportService(IUserInteraction interaction, ILogger<GridExportService>? logger = null) : ObservableObject
{
    /// <summary>The question asked before values are written.</summary>
    public const string ValuesQuestion =
        "This grid has columns with string text or field values read from the snapshot. A shared analysis leaves them out.\n\nInclude them in this file?";

    private readonly IUserInteraction _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>How the last export went, for the status bar; null before any.</summary>
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isExporting;

    /// <summary>
    /// Exports <paramref name="grid"/>, named <paramref name="name"/> (a fixed grid name such as "Instances": the file's
    /// suggested name and the log's). Returns the rows written, or null when nothing was written.
    /// </summary>
    public async Task<long?> ExportAsync(string name, IGridExport grid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(grid);
        if (IsExporting)
        {
            Status = "An export is already running.";
            return null;
        }

        if (grid.Columns.Count == 0)
        {
            Status = "Nothing to export: the grid is empty.";
            return null;
        }

        var path = await _interaction.PickSaveFileAsync("Export to CSV", name + ".csv");
        if (path is null)
        {
            return null;
        }

        var includeValues = CsvExport.HasValues(grid.Columns) && await _interaction.ConfirmAsync("Include values?", ValuesQuestion);
        using var activity = new Activity("HeapScope.Export").SetTag("grid", name).Start();
        var columns = CsvExport.Exported(grid.Columns, includeValues).Count;
        var watch = Stopwatch.StartNew();
        IsExporting = true;
        Status = string.Create(CultureInfo.CurrentCulture, $"Exporting {grid.RowCount:N0} rows...");
        try
        {
            var rows = await Task.Run(
                async () =>
                {
                    await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
                    await using var writer = new StreamWriter(stream, CsvExport.Encoding);
                    return await grid.WriteCsvAsync(writer, includeValues, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken);
            _logger.LogInformation(
                "Exported the {Grid} grid to CSV: {Rows} rows, {Columns} columns, values {IncludeValues}, in {Seconds:F2} s",
                name, rows, columns, includeValues, watch.Elapsed.TotalSeconds);
            Status = string.Create(CultureInfo.CurrentCulture, $"Exported {rows:N0} rows to {Path.GetFileName(path)}{(includeValues ? ", with values" : string.Empty)}.");
            return rows;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Exporting the {Grid} grid was canceled", name);
            Status = "Export canceled.";
        }
        catch (ObjectDisposedException)
        {
            // The grid's query was replaced (a new filter or sort) while it was written.
            _logger.LogInformation("Exporting the {Grid} grid stopped: the grid changed", name);
            Status = "The grid changed during the export; export it again.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkerException)
        {
            _logger.LogWarning("Exporting the {Grid} grid failed: {Error}", name, ex.GetType().Name);
            Status = "Export failed: " + ex.Message;
            await _interaction.ShowMessageAsync("Export failed", ex.Message);
        }
        finally
        {
            IsExporting = false;
        }

        return null;
    }
}
