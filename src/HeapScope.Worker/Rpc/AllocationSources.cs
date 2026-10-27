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
using HeapScope.Analysis.Allocations;
using HeapScope.Contracts;
using HeapScope.Core.Allocations;

namespace HeapScope.Worker.Rpc;

/// <summary>The types allocated in an interval (sprint 2.5), most sampled bytes first (<see cref="AllocationTypeColumns"/>).</summary>
internal sealed class AllocationTypesSource(AllocationTrace trace, AllocationTypeTotal[] rows, long totalBytes) : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new(AllocationTypeColumns.Type, ResultColumnKind.Text, IsKey: true),
        new(AllocationTypeColumns.Bytes, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTypeColumns.Samples, ResultColumnKind.Integer),
        new(AllocationTypeColumns.Percent, ResultColumnKind.Percent),
    ];

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => rows.Length;

    public long EstimatedBytes => rows.Length * 24L;

    public void FormatRow(int index, string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = rows[index];
        cells[0] = trace.Types[row.Type];
        cells[1] = row.Bytes.ToString(CultureInfo.InvariantCulture);
        cells[2] = row.Samples.ToString(CultureInfo.InvariantCulture);
        cells[3] = Percent(row.Bytes, totalBytes);
    }

    public static string Percent(long bytes, long total) =>
        total == 0 ? "0" : (100.0 * bytes / total).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>A flattened call tree or back traces (sprint 2.5), already in display order (<see cref="AllocationTreeColumns"/>).</summary>
internal sealed class AllocationTreeSource(AllocationTreeView view) : IResultSource
{
    public static readonly ResultColumn[] TreeColumns =
    [
        new(AllocationTreeColumns.Key, ResultColumnKind.Text, IsKey: true),
        new(AllocationTreeColumns.ParentKey, ResultColumnKind.Text),
        new(AllocationTreeColumns.Depth, ResultColumnKind.Integer),
        new(AllocationTreeColumns.Kind, ResultColumnKind.Text),
        new(AllocationTreeColumns.Name, ResultColumnKind.Text),
        new(AllocationTreeColumns.Total, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTreeColumns.Own, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTreeColumns.Samples, ResultColumnKind.Integer),
        new(AllocationTreeColumns.Percent, ResultColumnKind.Percent),
        new(AllocationTreeColumns.Children, ResultColumnKind.Integer),
        new(AllocationTreeColumns.Expanded, ResultColumnKind.Integer),
    ];

    public IReadOnlyList<ResultColumn> Columns => TreeColumns;

    public int Count => view.Rows.Count;

    public long EstimatedBytes => view.Rows.Count * 200L;

    public void FormatRow(int index, string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = view.Rows[index];
        cells[0] = row.Key;
        cells[1] = row.ParentKey;
        cells[2] = row.Depth.ToString(CultureInfo.InvariantCulture);
        cells[3] = row.Kind == AllocationRowKind.More ? AllocationRowKinds.More : AllocationRowKinds.Frame;
        cells[4] = row.Name;
        cells[5] = row.Total.ToString(CultureInfo.InvariantCulture);
        cells[6] = row.Own >= 0 ? row.Own.ToString(CultureInfo.InvariantCulture) : null;
        cells[7] = row.Samples.ToString(CultureInfo.InvariantCulture);
        cells[8] = AllocationTypesSource.Percent(row.Total, view.TotalBytes);
        cells[9] = row.Children.ToString(CultureInfo.InvariantCulture);
        cells[10] = row.Expanded ? "1" : "0";
    }
}
