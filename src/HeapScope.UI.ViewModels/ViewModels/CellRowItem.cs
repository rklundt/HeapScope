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
using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// A grid row of any result set (sprint 1.6 for inspections; shared since sprint 2.4, 2.3.7 review M13): the row keeps its
/// cells, raw (for what the row opens) and formatted for display (numbers grouped in the current culture), so one row type
/// serves every grid whose columns come from the worker.
/// </summary>
public sealed class CellRowItem(int index, IReadOnlyList<string> blank) : ObservableObject, IIndexedRow
{
    private static readonly IReadOnlyList<string> s_empty = [];

    private IReadOnlyList<ResultColumn> _columns = [];

    public int Index { get; } = index;

    public IReadOnlyList<string?> Raw { get; private set; } = s_empty;

    /// <summary>
    /// Display text per column, in the result set's column order (the view binds <c>Display[i]</c>); blank cells until the
    /// row's page arrives, so no binding fails on a placeholder.
    /// </summary>
    public IReadOnlyList<string> Display { get; private set; } = blank;

    public bool IsLoaded { get; private set; }

    /// <summary>A raw cell by column name, or null.</summary>
    public string? this[string column]
    {
        get
        {
            for (var i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].Name == column)
                {
                    return i < Raw.Count ? Raw[i] : null;
                }
            }

            return null;
        }
    }

    public static VirtualRowList<CellRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var blank = Enumerable.Repeat(string.Empty, info.Columns.Count).ToArray();
        return new VirtualRowList<CellRowItem>(rows, info.TotalCount, i => new CellRowItem(i, blank), (row, cells) => row.Fill(cells, info.Columns), onError, logger);
    }

    private void Fill(IReadOnlyList<string?> cells, IReadOnlyList<ResultColumn> columns)
    {
        _columns = columns;
        Raw = [.. cells];
        Display = [.. columns.Select((c, i) => FormatCell(c.Kind, i < cells.Count ? cells[i] : null))];
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }

    private static string FormatCell(ResultColumnKind kind, string? text) =>
        text is null ? string.Empty
        : kind is ResultColumnKind.Integer or ResultColumnKind.Bytes && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("N0", CultureInfo.CurrentCulture)
        : kind == ResultColumnKind.Percent && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            ? percent.ToString("N2", CultureInfo.CurrentCulture) + " %"
        : text;
}
