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
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Snapshot diff rows (sprint 1.4, ADR-018), grouped, filtered and sorted once per query and formatted only for the
/// requested page (ARCHITECTURE.md 4.6). Sorting takes any column by name; ties keep name order.
/// </summary>
internal sealed class TypeDiffSource : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new(TypeDiffColumns.Name, ResultColumnKind.Text, IsKey: true),
        new(TypeDiffColumns.Change, ResultColumnKind.Text),
        new(TypeDiffColumns.BaselineObjects, ResultColumnKind.Integer),
        new(TypeDiffColumns.CurrentObjects, ResultColumnKind.Integer),
        new(TypeDiffColumns.ObjectsDelta, ResultColumnKind.SignedInteger),
        new(TypeDiffColumns.BaselineBytes, ResultColumnKind.Bytes),
        new(TypeDiffColumns.CurrentBytes, ResultColumnKind.Bytes),
        new(TypeDiffColumns.BytesDelta, ResultColumnKind.SignedInteger),
        new(TypeDiffColumns.BaselineTypes, ResultColumnKind.Integer),
        new(TypeDiffColumns.CurrentTypes, ResultColumnKind.Integer),
    ];

    private static readonly ResultColumn[] s_typeColumns =
    [
        .. s_columns,
        new(TypeDiffColumns.TypeName, ResultColumnKind.Text, IsHidden: true),
        new(TypeDiffColumns.Module, ResultColumnKind.Text, IsHidden: true),
    ];

    private readonly DiffRow[] _rows;
    private readonly bool _byType;

    private TypeDiffSource(DiffRow[] rows, bool byType, int unfilteredCount, int newRows, int goneRows)
    {
        _rows = rows;
        _byType = byType;
        UnfilteredCount = unfilteredCount;
        NewRows = newRows;
        GoneRows = goneRows;
        EstimatedBytes = rows.Sum(r => 64L + (2L * r.Name.Length));
    }

    public IReadOnlyList<ResultColumn> Columns => _byType ? s_typeColumns : s_columns;

    public int Count => _rows.Length;

    public long EstimatedBytes { get; }

    /// <summary>Rows of the grouping before the filter.</summary>
    public int UnfilteredCount { get; }

    /// <summary>Rows present only in the current snapshot (before the filter).</summary>
    public int NewRows { get; }

    /// <summary>Rows present only in the baseline (before the filter).</summary>
    public int GoneRows { get; }

    /// <summary>Baseline bytes of the rows that pass the filter.</summary>
    public long MatchingBaselineBytes => _rows.Sum(r => (long)r.BaselineBytes);

    /// <summary>Bytes delta of the rows that pass the filter.</summary>
    public long MatchingBytesDelta => _rows.Sum(r => r.BytesDelta);

    /// <param name="baselineKind">The baseline's snapshot kind: across kinds, each side's names are mapped as its kind's
    /// handler says (sprint 1.8: a full dump's names in their gcdump form).</param>
    /// <param name="currentKind">The current snapshot's kind.</param>
    public static TypeDiffSource Create(
        HeapGraph baselineGraph,
        TypeAggregation baseline,
        HeapGraph currentGraph,
        TypeAggregation current,
        TypeDiffRequest request,
        SnapshotKind baselineKind,
        SnapshotKind currentKind)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.GroupBy))
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"Unknown grouping {(int)request.GroupBy}.");
        }

        var sortColumn = request.SortColumn ?? TypeDiffColumns.BytesDelta;
        var byType = request.GroupBy == TypeGrouping.Type;
        var sort = new ColumnSort<DiffRow>(r => r.Name)
            .On(TypeDiffColumns.Name, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            .On(TypeDiffColumns.Change, (a, b) => string.CompareOrdinal(Change(a, byType), Change(b, byType)))
            .On(TypeDiffColumns.BaselineObjects, r => r.BaselineCount)
            .On(TypeDiffColumns.CurrentObjects, r => r.CurrentCount)
            .On(TypeDiffColumns.ObjectsDelta, r => r.CountDelta)
            .On(TypeDiffColumns.BaselineBytes, r => r.BaselineBytes)
            .On(TypeDiffColumns.CurrentBytes, r => r.CurrentBytes)
            .On(TypeDiffColumns.BytesDelta, r => r.BytesDelta)
            .On(TypeDiffColumns.BaselineTypes, r => r.BaselineTypes)
            .On(TypeDiffColumns.CurrentTypes, r => r.CurrentTypes);
        sort.Validate(sortColumn);

        var rows = TypeDiff.Build(
            baselineGraph,
            baseline,
            currentGraph,
            current,
            request.GroupBy.ToGroupKey(),
            Snapshots.SnapshotKinds.Describe(baselineKind).NamesWhenComparedWith(currentKind),
            Snapshots.SnapshotKinds.Describe(currentKind).NamesWhenComparedWith(baselineKind));
        var unfiltered = rows.Count;
        var newRows = rows.Count(r => r.IsNew);
        var goneRows = rows.Count(r => r.IsGone);
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            rows.RemoveAll(r => !NameFilter.IsMatch(r.Name, r.Module, request.Filter));
        }

        sort.Sort(rows, sortColumn, request.Descending);
        return new TypeDiffSource([.. rows], byType, unfiltered, newRows, goneRows);
    }

    public void FormatRow(int index, string?[] cells)
    {
        var row = _rows[index];
        cells[0] = row.Name;
        cells[1] = Change(row, _byType);
        cells[2] = row.BaselineCount.ToString(CultureInfo.InvariantCulture);
        cells[3] = row.CurrentCount.ToString(CultureInfo.InvariantCulture);
        cells[4] = row.CountDelta.ToString(CultureInfo.InvariantCulture);
        cells[5] = row.BaselineBytes.ToString(CultureInfo.InvariantCulture);
        cells[6] = row.CurrentBytes.ToString(CultureInfo.InvariantCulture);
        cells[7] = row.BytesDelta.ToString(CultureInfo.InvariantCulture);
        cells[8] = row.BaselineTypes.ToString(CultureInfo.InvariantCulture);
        cells[9] = row.CurrentTypes.ToString(CultureInfo.InvariantCulture);
        if (_byType)
        {
            cells[10] = row.TypeName;
            cells[11] = row.Module;
        }
    }

    // "copies" only for type rows: a namespace or assembly gaining types is normal, a type loaded once more is not.
    private static string Change(DiffRow row, bool byType) =>
        row.IsNew ? TypeDiffColumns.New
        : row.IsGone ? TypeDiffColumns.Gone
        : byType && row.CopiesChanged ? TypeDiffColumns.Copies
        : string.Empty;
}
