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
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>The worker result set the rows come from, which the worker's log names with its query; null for rows in memory.</summary>
    Guid? ResultSetId => null;

    /// <summary>Writes the rows as CSV (<see cref="CsvExport"/>); returns the rows written.</summary>
    Task<long> WriteCsvAsync(TextWriter output, bool includeValues, CancellationToken cancellationToken);
}

/// <summary>
/// Exports a grid to a CSV file the person picks (sprint 2.7, ADR-026): asks for the file, asks whether to include
/// values when the grid has any (no unless they say yes), writes off the dispatcher into a temporary file moved into
/// place only when complete, and says how it went; <see cref="CancelCommand"/> stops it. The log names the grid, its
/// result set (the query that made it), the rows expected and written, the columns, the opt-in and the time; never the
/// file's path or a cell.
/// </summary>
public sealed partial class GridExportService(IUserInteraction interaction, ILogger<GridExportService>? logger = null) : ObservableObject
{
    /// <summary>The question asked before values are written.</summary>
    public const string ValuesQuestion =
        "This grid has columns with string text or field values read from the snapshot. A shared analysis leaves them out.\n\nInclude them in this file?";

    private readonly IUserInteraction _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private CancellationTokenSource? _cancel;

    /// <summary>How the last export went, for the status bar; null before any.</summary>
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isExporting;

    /// <summary>
    /// Exports <paramref name="grid"/>, named <paramref name="name"/> (a fixed grid name such as "Instances": the file's
    /// suggested name and the log's). Returns the rows written, or null when nothing was written. Never throws: an
    /// unexpected failure is logged and shown (the window does not await it).
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

        // Taken before the dialogs, so a second Command+E does not open a second picker (review).
        IsExporting = true;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? partial = null;
        try
        {
            var path = await _interaction.PickSaveFileAsync("Export to CSV", name + ".csv");
            if (path is null)
            {
                return null;
            }

            var includeValues = CsvExport.HasValues(grid.Columns) && await _interaction.ConfirmAsync("Include values?", ValuesQuestion);
            using var activity = new Activity("HeapScope.Export").SetTag("grid", name).Start();
            var columns = CsvExport.Exported(grid.Columns, includeValues).Count;
            var expected = grid.RowCount;
            var watch = Stopwatch.StartNew();
            Status = string.Create(CultureInfo.CurrentCulture, $"Exporting {expected:N0} rows...");
            _logger.LogInformation(
                "Exporting the {Grid} grid to CSV: result set {ResultSet}, {Expected} rows, {Columns} columns, values {IncludeValues}",
                name, grid.ResultSetId?.ToString() ?? "in memory", expected, columns, includeValues);

            // Written beside the chosen file and moved into place when complete: a stopped export leaves no short file
            // that looks whole (review).
            partial = path + ".partial";
            var token = _cancel.Token;
            var rows = await Task.Run(
                async () =>
                {
                    await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                    await using (var writer = new StreamWriter(stream, CsvExport.Encoding))
                    {
                        var written = await grid.WriteCsvAsync(writer, includeValues, token).ConfigureAwait(false);
                        await writer.FlushAsync(token).ConfigureAwait(false);
                        return written;
                    }
                },
                token);
            File.Move(partial, path, overwrite: true);
            partial = null;
            _logger.LogInformation(
                "Exported the {Grid} grid to CSV: {Rows} of {Expected} rows, {Columns} columns, values {IncludeValues}, in {Seconds:F2} s",
                name, rows, expected, columns, includeValues, watch.Elapsed.TotalSeconds);
            Status = string.Create(CultureInfo.CurrentCulture, $"Exported {rows:N0} rows to {Path.GetFileName(path)}{(includeValues ? ", with values" : string.Empty)}.");
            return rows;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Exporting the {Grid} grid was canceled", name);
            Status = "Export canceled; no file was written.";
        }
        catch (ObjectDisposedException)
        {
            // The grid's query was replaced (a new filter or sort) or its snapshot closed while it was written.
            _logger.LogInformation("Exporting the {Grid} grid stopped: the grid changed", name);
            Status = "The grid changed during the export, so no file was written; export it again.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkerException)
        {
            _logger.LogWarning("Exporting the {Grid} grid failed: {Error} (0x{HResult:x8})", name, ex.GetType().Name, ex.HResult);
            Status = "Export failed: " + ex.Message;
            await _interaction.ShowMessageAsync("Export failed", ex.Message + "\n\nNo file was written. Choose another folder or close the program holding the file, then export again.");
        }
        catch (Exception ex)
        {
            // Anything else (an odd path from the picker, a dialog failing) is logged and shown, never lost.
            _logger.LogError(ex, "Exporting the {Grid} grid failed unexpectedly", name);
            Status = "Export failed: " + ex.Message;
        }
        finally
        {
            if (partial is not null)
            {
                TryDelete(partial);
            }

            _cancel.Dispose();
            _cancel = null;
            IsExporting = false;
        }

        return null;
    }

    /// <summary>Stops the running export; nothing is written.</summary>
    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void Cancel() => _cancel?.Cancel();

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A stopped export's temporary file could not be deleted: {Error}", ex.GetType().Name);
        }
    }
}
