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

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Sorting a result set's rows by a column name (sprint 1.9, shared by the row-based result sources): one comparison per
/// sortable column, ascending or descending, ties broken by the row's name so the order is stable between queries, and
/// an unknown column refused with the list of sortable ones.
/// </summary>
internal sealed class ColumnSort<TRow>(Func<TRow, string> name)
{
    private readonly Dictionary<string, Comparison<TRow>> _byColumn = new(StringComparer.Ordinal);

    public IEnumerable<string> Columns => _byColumn.Keys;

    public ColumnSort<TRow> On(string column, Comparison<TRow> compare)
    {
        _byColumn[column] = compare;
        return this;
    }

    public ColumnSort<TRow> On<TKey>(string column, Func<TRow, TKey> key)
        where TKey : IComparable<TKey> =>
        On(column, (a, b) => key(a).CompareTo(key(b)));

    /// <summary>Fails fast, before any rows are built, when <paramref name="column"/> cannot be sorted on.</summary>
    /// <exception cref="ArgumentException">The column is unknown.</exception>
    public void Validate(string column)
    {
        if (!_byColumn.ContainsKey(column))
        {
            throw new ArgumentException($"Unknown sort column {column}. Sort by one of: {string.Join(", ", _byColumn.Keys)}.");
        }
    }

    public void Sort(List<TRow> rows, string column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Validate(column);
        var compare = _byColumn[column];
        rows.Sort((a, b) =>
        {
            var primary = descending ? compare(b, a) : compare(a, b);
            return primary != 0 ? primary : string.CompareOrdinal(name(a), name(b));
        });
    }
}
