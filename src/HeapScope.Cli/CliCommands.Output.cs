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
using HeapScope.Application.Exports;
using HeapScope.Application.Presentation;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;

namespace HeapScope.Cli;

/// <summary>
/// Output shared by the commands (sprint 1.9): fetching a result set's rows, CSV, JSON (rows keyed by column name with
/// typed numbers), and the small text helpers. Each command keeps its own table layout.
/// </summary>
internal static partial class CliCommands
{
    private static async Task WriteCsvAsync(TextWriter output, IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows)
    {
        // A local view for the person running the command (ADR-026): every column. Text read from a dump that a
        // spreadsheet would run as a formula is shown as text, as in the app's exports (2.7 review).
        await output.WriteLineAsync(string.Join(",", columns.Select(c => CsvExport.Quote(c.Name)))).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await output.WriteLineAsync(string.Join(",", row.Select((cell, i) => CsvExport.Cell(cell, i < columns.Count ? columns[i].Kind : ResultColumnKind.Text)))).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rows as objects keyed by column name, with numbers typed by the column kind; a null cell is JSON null in any column
    /// (sprint 2.3: an object no root reaches has no retained bytes, review H2).
    /// </summary>
    private static IEnumerable<Dictionary<string, object?>> JsonRows(IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows) =>
        rows.Select(row => columns.Select((c, i) => (c, i)).ToDictionary(
            x => x.c.Name,
            x => row[x.i] is null || x.c.Kind is ResultColumnKind.Text or ResultColumnKind.Address ? (object?)row[x.i]
                : x.c.Kind == ResultColumnKind.Percent ? double.Parse(row[x.i]!, CultureInfo.InvariantCulture)
                : long.Parse(row[x.i]!, CultureInfo.InvariantCulture)));

    /// <summary>Fetches the first <paramref name="wanted"/> rows of a result set.</summary>
    private static async Task<List<IReadOnlyList<string?>>> FetchRowsAsync(ResultSetHandle handle, int wanted, CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyList<string?>>();
        while (rows.Count < wanted)
        {
            var page = await handle.GetRowsAsync(rows.Count, Math.Min(WorkerProtocol.MaxPageSize, wanted - rows.Count), cancellationToken).ConfigureAwait(false);
            if (page.Rows.Count == 0)
            {
                break;
            }

            rows.AddRange(page.Rows);
        }

        return rows;
    }

    /// <summary>Prints a snapshot's notes (incomplete segments, dropped references) to stderr, labeled by its role.</summary>
    private static async Task WriteNotesAsync(TextWriter error, string role, SnapshotHandle snapshot)
    {
        foreach (var note in snapshot.Descriptor.Notes)
        {
            await error.WriteLineAsync($"{role} ({Path.GetFileName(snapshot.Path)}): {note}").ConfigureAwait(false);
        }
    }

    /// <summary>One line on how the dominators were obtained and how much of the heap the roots reach (the app's wording too).</summary>
    internal static string RetainedNote(DominatorIndexInfo dominators) => RetainedSizeTexts.Source(dominators, CultureInfo.InvariantCulture);

    private static readonly System.Text.Json.JsonSerializerOptions s_json = new() { WriteIndented = true };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 3)] + "...";

    private static string Mb(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("N1", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Writes <paramref name="document"/> as indented JSON, the format of every command's <c>--format json</c>.</summary>
    private static Task WriteJsonAsync(TextWriter output, object document) =>
        output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(document, s_json));
}
