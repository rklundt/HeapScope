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

/// <summary>
/// Summary rows (per type, namespace or assembly) held as compact arrays and formatted only for the requested page
/// (ARCHITECTURE.md 4.6). Columns: name (key), objects, bytes, percent of all bytes, then bytes per heap kind when the
/// snapshot has generations (sprint 1.3), then in Type grouping the hidden type name and module that open a row's
/// instances (sprint 1.5). Sorting takes any column by name.
/// </summary>
internal sealed class TypeSummarySource : IResultSource
{
    /// <summary>The heap kinds shown as columns, in order.</summary>
    public static readonly HeapKind[] KindColumns =
        [HeapKind.Gen0, HeapKind.Gen1, HeapKind.Gen2, HeapKind.LargeObjectHeap, HeapKind.PinnedObjectHeap, HeapKind.Frozen];

    private static readonly ResultColumn[] s_baseColumns =
    [
        new(TypeSummaryColumns.Name, ResultColumnKind.Text, IsKey: true),
        new(TypeSummaryColumns.Objects, ResultColumnKind.Integer),
        new(TypeSummaryColumns.Bytes, ResultColumnKind.Bytes),
        new(TypeSummaryColumns.Percent, ResultColumnKind.Percent),
    ];

    private static readonly ResultColumn[] s_retainedColumns = [new(TypeSummaryColumns.Retained, ResultColumnKind.Bytes)];

    private static readonly ResultColumn[] s_kindColumns = [.. KindColumns.Select(k => new ResultColumn(k.ToString(), ResultColumnKind.Bytes))];

    private static readonly ResultColumn[] s_keyColumns =
    [
        new(TypeSummaryColumns.TypeName, ResultColumnKind.Text),
        new(TypeSummaryColumns.Module, ResultColumnKind.Text),
    ];

    private readonly SummaryRow[] _rows;
    private readonly ulong _totalBytes;
    private readonly bool _generations;
    private readonly bool _byType;
    private readonly bool _retained;

    private TypeSummarySource(SummaryRow[] rows, int unfilteredCount, ulong totalBytes, bool generations, bool byType, bool retained)
    {
        _rows = rows;
        UnfilteredCount = unfilteredCount;
        _totalBytes = totalBytes;
        _generations = generations;
        _byType = byType;
        _retained = retained;
        Columns = ColumnsFor(generations, byType, retained);
        EstimatedBytes = rows.Sum(r => 48L + (2L * r.Name.Length) + (8L * r.BytesByKind.Length) + (byType ? 2L * r.Name.Length : 0));
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public int Count => _rows.Length;

    /// <summary>Rows of the grouping before the filter.</summary>
    public int UnfilteredCount { get; }

    public long EstimatedBytes { get; }

    /// <summary>The default summary: one row per type, largest first (the 1.2 query and the synthetic job).</summary>
    public static TypeSummarySource Create(HeapGraph graph, CancellationToken cancellationToken) =>
        Create(graph, TypeAggregation.Compute(graph, cancellationToken: cancellationToken), new TypeSummaryRequest(Guid.Empty), generations: false);

    /// <summary>
    /// Checks <paramref name="request"/>'s grouping and sort column before the worker computes the dominators for it
    /// (sprint 2.3 review M7).
    /// </summary>
    /// <exception cref="ArgumentException">The grouping or the sort column is unknown.</exception>
    public static void Validate(TypeSummaryRequest request, bool generations)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.GroupBy))
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"Unknown grouping {(int)request.GroupBy}.");
        }

        Sort(generations, request.GroupBy == TypeGrouping.Type, request.IncludeRetained).Validate(request.SortColumn ?? TypeSummaryColumns.Bytes);
    }

    /// <summary>
    /// Groups, filters and sorts <paramref name="aggregation"/> as <paramref name="request"/> asks. With
    /// <paramref name="retainedByGroup"/> (sprint 2.3; numbered as <see cref="TypeGroups.GroupOfType"/>) the rows carry a
    /// retained bytes column.
    /// </summary>
    public static TypeSummarySource Create(HeapGraph graph, TypeAggregation aggregation, TypeSummaryRequest request, bool generations, ulong[]? retainedByGroup = null)
    {
        ArgumentNullException.ThrowIfNull(aggregation);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.GroupBy))
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"Unknown grouping {(int)request.GroupBy}.");
        }

        var byType = request.GroupBy == TypeGrouping.Type;
        var sortColumn = request.SortColumn ?? TypeSummaryColumns.Bytes;
        var retained = retainedByGroup is not null;
        var sort = Sort(generations, byType, retained);
        sort.Validate(sortColumn);
        var rows = TypeGroups.Build(graph, aggregation, request.GroupBy.ToGroupKey(), retainedByGroup);
        var unfiltered = rows.Count;
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            rows.RemoveAll(r => !NameFilter.IsMatch(r.Name, request.Filter));
        }

        sort.Sort(rows, sortColumn, request.Descending);

        return new TypeSummarySource([.. rows], unfiltered, aggregation.TotalBytes, generations, byType, retained);
    }

    public void FormatRow(int index, string?[] cells)
    {
        var row = _rows[index];
        cells[0] = row.Name;
        cells[1] = row.Count.ToString(CultureInfo.InvariantCulture);
        cells[2] = row.Bytes.ToString(CultureInfo.InvariantCulture);
        cells[3] = (_totalBytes == 0 ? 0 : 100.0 * row.Bytes / _totalBytes).ToString("0.##", CultureInfo.InvariantCulture);
        var next = 4;
        if (_retained)
        {
            cells[next++] = (row.Retained ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        if (_generations)
        {
            for (var k = 0; k < KindColumns.Length; k++)
            {
                cells[next++] = row.BytesByKind[(int)KindColumns[k]].ToString(CultureInfo.InvariantCulture);
            }
        }

        if (_byType)
        {
            cells[next++] = row.TypeName;
            cells[next] = row.Module;
        }
    }

    private static ResultColumn[] ColumnsFor(bool generations, bool byType, bool retained) =>
        [.. s_baseColumns, .. retained ? s_retainedColumns : [], .. generations ? s_kindColumns : [], .. byType ? s_keyColumns : []];

    private static ColumnSort<SummaryRow> Sort(bool generations, bool byType, bool retained)
    {
        var sort = new ColumnSort<SummaryRow>(r => r.Name)
            .On(TypeSummaryColumns.Name, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            .On(TypeSummaryColumns.Objects, r => r.Count)
            .On(TypeSummaryColumns.Bytes, r => r.Bytes)
            .On(TypeSummaryColumns.Percent, r => r.Bytes);
        if (retained)
        {
            sort.On(TypeSummaryColumns.Retained, r => r.Retained ?? 0);
        }

        if (generations)
        {
            for (var i = 0; i < s_kindColumns.Length; i++)
            {
                var kind = (int)KindColumns[i];
                sort.On(s_kindColumns[i].Name, r => r.BytesByKind[kind]);
            }
        }

        if (byType)
        {
            sort.On(TypeSummaryColumns.TypeName, (a, b) => string.CompareOrdinal(a.TypeName, b.TypeName))
                .On(TypeSummaryColumns.Module, (a, b) => string.Compare(a.Module, b.Module, StringComparison.OrdinalIgnoreCase));
        }

        return sort;
    }
}
