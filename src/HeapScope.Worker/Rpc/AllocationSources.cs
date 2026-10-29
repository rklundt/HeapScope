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

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The types allocated in an interval (sprint 2.5), most sampled bytes first, with the part counted on the large and
/// pinned object heaps (<see cref="AllocationTypeColumns"/>). Holds the type names only, not the trace.
/// </summary>
internal sealed class AllocationTypesSource(IReadOnlyList<string> typeNames, AllocationTypeTotal[] rows, long totalBytes) : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new(AllocationTypeColumns.Type, ResultColumnKind.Text, IsKey: true),
        new(AllocationTypeColumns.Bytes, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTypeColumns.Percent, ResultColumnKind.Percent),
        new(AllocationTypeColumns.Samples, ResultColumnKind.Integer),
        new(AllocationTypeColumns.LargeObjectHeap, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTypeColumns.PinnedObjectHeap, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
    ];

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => rows.Length;

    public long EstimatedBytes => rows.Length * 40L;

    public void FormatRow(int index, string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = rows[index];
        cells[0] = typeNames[row.Type];
        cells[1] = row.Bytes.ToString(CultureInfo.InvariantCulture);
        cells[2] = AllocationShare.Of(row.Bytes, totalBytes);
        cells[3] = row.Samples.ToString(CultureInfo.InvariantCulture);
        cells[4] = row.LargeBytes.ToString(CultureInfo.InvariantCulture);
        cells[5] = row.PinnedBytes.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>A flattened call tree or back traces (sprint 2.5), already in display order (<see cref="AllocationTreeColumns"/>).</summary>
internal sealed class AllocationTreeSource(AllocationTreeView view) : IResultSource
{
    public static readonly ResultColumn[] TreeColumns =
    [
        new(AllocationTreeColumns.Key, ResultColumnKind.Text, IsKey: true, IsHidden: true),
        new(AllocationTreeColumns.ParentKey, ResultColumnKind.Text, IsHidden: true),
        new(AllocationTreeColumns.Depth, ResultColumnKind.Integer),
        new(AllocationTreeColumns.Kind, ResultColumnKind.Text),
        new(AllocationTreeColumns.Name, ResultColumnKind.Text),
        new(AllocationTreeColumns.Total, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTreeColumns.Own, ResultColumnKind.Bytes, Bound: ValueBound.Estimate),
        new(AllocationTreeColumns.Samples, ResultColumnKind.Integer),
        new(AllocationTreeColumns.Percent, ResultColumnKind.Percent),
        new(AllocationTreeColumns.Children, ResultColumnKind.Integer, IsHidden: true),
        new(AllocationTreeColumns.Expanded, ResultColumnKind.Integer, IsHidden: true),
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
        cells[8] = AllocationShare.Of(row.Total, view.TotalBytes);
        cells[9] = row.Children.ToString(CultureInfo.InvariantCulture);
        cells[10] = row.Expanded ? "1" : "0";
    }
}

/// <summary>A share of an interval's sampled bytes, as every allocation answer formats it (<see cref="AllocationTexts.ShareOf"/>).</summary>
internal static class AllocationShare
{
    public static string Of(long bytes, long total) =>
        total == 0 ? "0" : (100.0 * bytes / total).ToString("0.##", CultureInfo.InvariantCulture);
}
