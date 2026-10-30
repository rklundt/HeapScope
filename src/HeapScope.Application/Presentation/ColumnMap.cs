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
using HeapScope.Contracts;

namespace HeapScope.Application.Presentation;

/// <summary>
/// Reads a row's cells by column name from a result's columns, so a change in the worker's column order cannot put a
/// value under the wrong header. A required column that is missing throws (<see cref="ResultColumns.Require"/>); a number
/// read from an empty or garbled cell throws a FormatException naming the column, where a blank means something the caller must handle (<see cref="LongOrNull"/>).
/// </summary>
public sealed class ColumnMap
{
    private readonly Dictionary<string, int> _indexes = new(StringComparer.Ordinal);

    public ColumnMap(IReadOnlyList<ResultColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Columns = columns;
        for (var i = 0; i < columns.Count; i++)
        {
            _indexes.TryAdd(columns[i].Name, i);
        }
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    /// <summary>Whether the result has the column.</summary>
    public bool Has(string name) => _indexes.ContainsKey(name);

    /// <summary>The column's index, or -1.</summary>
    public int IndexOf(string name) => _indexes.TryGetValue(name, out var index) ? index : -1;

    /// <summary>The cell of a required column, as the worker sent it (null for a blank).</summary>
    public string? Text(IReadOnlyList<string?> row, string name)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row[IndexOf(name) is >= 0 and var index ? index : ResultColumns.Require(Columns, name)];
    }

    /// <summary>A required column's integer; a blank or garbled cell throws <see cref="FormatException"/> naming the column.</summary>
    public long Long(IReadOnlyList<string?> row, string name) =>
        LongOrNull(row, name) ?? throw new FormatException($"The worker's answer has no value in column {name}. Run the query again; if it repeats, report it with the log.");

    /// <summary>A required column's integer, or null for a blank cell (an object no root reaches retains nothing).</summary>
    public long? LongOrNull(IReadOnlyList<string?> row, string name)
    {
        var text = Text(row, name);
        if (text is null)
        {
            return null;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"The worker's answer has no number in column {name}. Run the query again; if it repeats, report it with the log.");
    }
}
