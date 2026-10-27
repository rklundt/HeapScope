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

using System.Text;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;

namespace HeapScope.Application.Exports;

/// <summary>
/// A grid written as CSV (sprint 2.7, the first form of ADR-026's shared analysis export). Every row is written, fetched
/// page by page from the worker, not only the rows a grid has loaded. Hidden link columns are left out; columns that
/// carry values (string text, field values) are left out unless the person exporting opted in, and the file then says
/// so on its first line. A text cell a spreadsheet would read as a formula gets a leading apostrophe.
/// </summary>
public static class CsvExport
{
    /// <summary>The first line of an export that includes values (ADR-026).</summary>
    public const string ValuesNotice = "# This export includes string text and field values read from the snapshot. Share it with care.";

    /// <summary>Rows fetched per worker call.</summary>
    public const int PageSize = WorkerProtocol.MaxPageSize;

    /// <summary>UTF-8 with a byte order mark, which spreadsheets need to read non-ASCII names.</summary>
    public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>The indices of the columns an export writes.</summary>
    public static IReadOnlyList<int> Exported(IReadOnlyList<ResultColumn> columns, bool includeValues)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return [.. Enumerable.Range(0, columns.Count).Where(i => !columns[i].IsHidden && (includeValues || !columns[i].HasValues))];
    }

    /// <summary>Whether any column carries values, so the person exporting is asked about them.</summary>
    public static bool HasValues(IReadOnlyList<ResultColumn> columns) => columns.Any(c => c.HasValues && !c.IsHidden);

    /// <summary>Writes every row of <paramref name="rows"/>; returns the rows written.</summary>
    public static Task<long> WriteAsync(TextWriter output, ResultSetHandle rows, bool includeValues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(output, rows.Info.Columns, rows.Info.TotalCount, rows.GetRowsAsync, includeValues, cancellationToken);
    }

    /// <summary>Writes <paramref name="count"/> rows fetched through <paramref name="fetch"/> (offset, count) page by page.</summary>
    public static async Task<long> WriteAsync(
        TextWriter output,
        IReadOnlyList<ResultColumn> columns,
        int count,
        Func<int, int, CancellationToken, Task<ResultPage>> fetch,
        bool includeValues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(fetch);
        var exported = await WriteHeaderAsync(output, columns, includeValues).ConfigureAwait(false);
        long written = 0;
        while (written < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetch((int)written, (int)Math.Min(PageSize, count - written), cancellationToken).ConfigureAwait(false);
            if (page.Rows.Count == 0)
            {
                break;
            }

            foreach (var row in page.Rows)
            {
                await WriteRowAsync(output, columns, exported, row).ConfigureAwait(false);
            }

            written += page.Rows.Count;
        }

        return written;
    }

    /// <summary>Writes rows held in memory (grids that are not a worker result, such as the process list).</summary>
    public static async Task<long> WriteAsync(
        TextWriter output,
        IReadOnlyList<ResultColumn> columns,
        IEnumerable<IReadOnlyList<string?>> rows,
        bool includeValues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rows);
        var exported = await WriteHeaderAsync(output, columns, includeValues).ConfigureAwait(false);
        long written = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRowAsync(output, columns, exported, row).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    /// <summary>A CSV field: quoted when it holds a comma, a quote or a line break (RFC 4180).</summary>
    public static string Quote(string? value) =>
        value is null ? string.Empty
        : value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : value;

    /// <summary>
    /// A cell of an export: a text cell starting with a formula character (<c>= + - @</c>, tab, carriage return) gets a
    /// leading apostrophe so a spreadsheet shows it rather than running it; numbers are written as they are.
    /// </summary>
    public static string Cell(string? value, ResultColumnKind kind) =>
        Quote(kind == ResultColumnKind.Text && value is { Length: > 0 } && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value);

    private static async Task<IReadOnlyList<int>> WriteHeaderAsync(TextWriter output, IReadOnlyList<ResultColumn> columns, bool includeValues)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var exported = Exported(columns, includeValues);
        if (includeValues && exported.Any(i => columns[i].HasValues))
        {
            await output.WriteLineAsync(ValuesNotice).ConfigureAwait(false);
        }

        await output.WriteLineAsync(string.Join(",", exported.Select(i => Quote(Header(columns[i]))))).ConfigureAwait(false);
        return exported;
    }

    /// <summary>
    /// A column's header: its name, and what its numbers are when not exact (a lower bound, an estimate), so a shared file
    /// is not read as exact figures (2.7 review).
    /// </summary>
    public static string Header(ResultColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.Bound switch
        {
            ValueBound.LowerBound => column.Name + " (at least)",
            ValueBound.Estimate => column.Name + " (estimated)",
            _ => column.Name,
        };
    }

    private static Task WriteRowAsync(TextWriter output, IReadOnlyList<ResultColumn> columns, IReadOnlyList<int> exported, IReadOnlyList<string?> row) =>
        output.WriteLineAsync(string.Join(",", exported.Select(i => Cell(i < row.Count ? row[i] : null, columns[i].Kind))));
}
