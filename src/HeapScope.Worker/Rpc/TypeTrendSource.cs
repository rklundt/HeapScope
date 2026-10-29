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
using HeapScope.Contracts;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>Trend rows (sprint 3.3): grouped, filtered and sorted once, formatted per page.</summary>
internal sealed class TypeTrendSource : IResultSource
{
    private readonly TrendRow[] _rows;
    private readonly int _snapshots;
    private readonly bool _byType;

    private TypeTrendSource(TrendRow[] rows, int snapshots, bool byType, int unfiltered)
    {
        _rows = rows;
        _snapshots = snapshots;
        _byType = byType;
        UnfilteredCount = unfiltered;
        Columns =
        [
            new(TypeTrendColumns.Name, ResultColumnKind.Text, IsKey: true),
            .. Enumerable.Range(1, snapshots).Select(i => new ResultColumn(TypeTrendColumns.Objects(i), ResultColumnKind.Integer)),
            .. Enumerable.Range(1, snapshots).Select(i => new ResultColumn(TypeTrendColumns.Bytes(i), ResultColumnKind.Bytes)),
            new(TypeTrendColumns.ObjectsDelta, ResultColumnKind.SignedInteger),
            new(TypeTrendColumns.BytesDelta, ResultColumnKind.SignedInteger),
            new(TypeTrendColumns.GrowthSteps, ResultColumnKind.Integer),
            .. byType ? new ResultColumn[] { new(TypeTrendColumns.TypeName, ResultColumnKind.Text, IsHidden: true), new(TypeTrendColumns.Module, ResultColumnKind.Text, IsHidden: true) } : [],
        ];
        EstimatedBytes = rows.Sum(r => 64L + (2L * r.Name.Length) + (16L * snapshots));
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public int Count => _rows.Length;

    public long EstimatedBytes { get; }

    public int UnfilteredCount { get; }

    public static TypeTrendSource Create(IReadOnlyList<(HeapGraph Graph, TypeAggregation Aggregation, TypeInterfaces? Interfaces)> snapshots, TypeTrendRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(request);
        var n = snapshots.Count;
        var sort = new ColumnSort<TrendRow>(r => r.Name)
            .On(TypeTrendColumns.Name, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            .On(TypeTrendColumns.ObjectsDelta, r => r.CountDelta)
            .On(TypeTrendColumns.BytesDelta, r => r.BytesDelta)
            .On(TypeTrendColumns.GrowthSteps, r => r.GrowthSteps);
        for (var i = 0; i < n; i++)
        {
            var at = i;
            sort.On(TypeTrendColumns.Objects(i + 1), r => r.Counts[at]);
            sort.On(TypeTrendColumns.Bytes(i + 1), r => (long)r.Bytes[at]);
        }

        var sortColumn = request.SortColumn ?? TypeTrendColumns.BytesDelta;
        sort.Validate(sortColumn);
        var rows = TypeTrend.Build(snapshots, request.GroupBy.ToGroupKey());
        var unfiltered = rows.Count;
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            rows.RemoveAll(r => !NameFilter.IsMatch(r.Name, r.Module, request.Filter));
        }

        sort.Sort(rows, sortColumn, request.Descending);
        return new TypeTrendSource([.. rows], n, request.GroupBy == TypeGrouping.Type, unfiltered);
    }

    public void FormatRow(int index, string?[] cells)
    {
        var row = _rows[index];
        var next = 0;
        cells[next++] = row.Name;
        for (var i = 0; i < _snapshots; i++)
        {
            cells[next++] = row.Counts[i].ToString(CultureInfo.InvariantCulture);
        }

        for (var i = 0; i < _snapshots; i++)
        {
            cells[next++] = row.Bytes[i].ToString(CultureInfo.InvariantCulture);
        }

        cells[next++] = row.CountDelta.ToString(CultureInfo.InvariantCulture);
        cells[next++] = row.BytesDelta.ToString(CultureInfo.InvariantCulture);
        cells[next++] = row.GrowthSteps.ToString(CultureInfo.InvariantCulture);
        if (_byType)
        {
            cells[next++] = row.TypeName;
            cells[next] = row.Module;
        }
    }

    /// <summary>The first <paramref name="count"/> rows for the log: name, bytes first and last, and growth steps.</summary>
    public string Describe(int count) =>
        string.Join("; ", _rows.Take(count).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Name} {r.Bytes[0]} to {r.Bytes[^1]} bytes, grew in {r.GrowthSteps} of {_snapshots - 1} steps")));
}
