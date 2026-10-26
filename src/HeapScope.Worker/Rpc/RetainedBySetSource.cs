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
using HeapScope.Analysis;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// What a set retains, by type (sprint 2.4): one row per type with retained objects, largest first, with the hidden type
/// name and module that open the type's instances (<see cref="RetainedBySetColumns"/>).
/// </summary>
internal sealed class RetainedBySetSource : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new(RetainedBySetColumns.Name, ResultColumnKind.Text, IsKey: true),
        new(RetainedBySetColumns.Objects, ResultColumnKind.Integer),
        new(RetainedBySetColumns.Retained, ResultColumnKind.Bytes),
        new(RetainedBySetColumns.Percent, ResultColumnKind.Percent),
        new(RetainedBySetColumns.TypeName, ResultColumnKind.Text),
        new(RetainedBySetColumns.Module, ResultColumnKind.Text),
    ];

    private readonly Row[] _rows;
    private readonly ulong _total;

    private RetainedBySetSource(Row[] rows, ulong total)
    {
        _rows = rows;
        _total = total;
    }

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => _rows.Length;

    public long EstimatedBytes => _rows.Sum(r => 64L + (4L * r.Name.Length));

    /// <summary>The rows (largest first, ties by name), as text for the log's first rows.</summary>
    public IEnumerable<string> Describe(int count) =>
        _rows.Take(count).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Name} {r.Objects} objects {r.Bytes} bytes"));

    /// <summary>The types <paramref name="result"/> retains objects of, named as the type summary names them.</summary>
    public static RetainedBySetSource Create(HeapGraph graph, TypeAggregation aggregation, SetRetentionResult result)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(result);
        var names = TypeGroups.DisplayNames(graph, aggregation);
        var rows = new List<Row>();
        for (var t = 0; t < result.ObjectsByType.Length; t++)
        {
            if (result.ObjectsByType[t] > 0)
            {
                rows.Add(new Row(names[t], result.ObjectsByType[t], result.BytesByType[t], graph.Types.GetName(t), TypeGroups.ModuleKey(graph.Types.GetModule(t))));
            }
        }

        rows.Sort((a, b) => b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes) : string.CompareOrdinal(a.Name, b.Name));
        return new RetainedBySetSource([.. rows], result.RetainedBytes);
    }

    public void FormatRow(int index, string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = _rows[index];
        cells[0] = row.Name;
        cells[1] = row.Objects.ToString(CultureInfo.InvariantCulture);
        cells[2] = row.Bytes.ToString(CultureInfo.InvariantCulture);
        cells[3] = (_total == 0 ? 0 : 100.0 * row.Bytes / _total).ToString("0.##", CultureInfo.InvariantCulture);
        cells[4] = row.TypeName;
        cells[5] = row.Module;
    }

    private sealed record Row(string Name, long Objects, ulong Bytes, string TypeName, string Module);
}
