using System.Globalization;
using HeapScope.Analysis;
using HeapScope.Contracts;
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
        new(TypeDiffColumns.TypeName, ResultColumnKind.Text),
        new(TypeDiffColumns.Module, ResultColumnKind.Text),
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

    public static TypeDiffSource Create(HeapGraph baselineGraph, TypeAggregation baseline, HeapGraph currentGraph, TypeAggregation current, TypeDiffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.GroupBy))
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"Unknown grouping {(int)request.GroupBy}.");
        }

        var sortColumn = request.SortColumn ?? TypeDiffColumns.BytesDelta;
        var byType = request.GroupBy == TypeGrouping.Type;
        Comparison<DiffRow> compare = sortColumn switch
        {
            TypeDiffColumns.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            TypeDiffColumns.Change => (a, b) => string.CompareOrdinal(Change(a, byType), Change(b, byType)),
            TypeDiffColumns.BaselineTypes => (a, b) => a.BaselineTypes.CompareTo(b.BaselineTypes),
            TypeDiffColumns.CurrentTypes => (a, b) => a.CurrentTypes.CompareTo(b.CurrentTypes),
            TypeDiffColumns.BaselineObjects => (a, b) => a.BaselineCount.CompareTo(b.BaselineCount),
            TypeDiffColumns.CurrentObjects => (a, b) => a.CurrentCount.CompareTo(b.CurrentCount),
            TypeDiffColumns.ObjectsDelta => (a, b) => a.CountDelta.CompareTo(b.CountDelta),
            TypeDiffColumns.BaselineBytes => (a, b) => a.BaselineBytes.CompareTo(b.BaselineBytes),
            TypeDiffColumns.CurrentBytes => (a, b) => a.CurrentBytes.CompareTo(b.CurrentBytes),
            TypeDiffColumns.BytesDelta => (a, b) => a.BytesDelta.CompareTo(b.BytesDelta),
            _ => throw new ArgumentException(
                $"Unknown sort column {sortColumn}. Sort by one of: {string.Join(", ", s_columns.Select(c => c.Name))}.",
                nameof(request)),
        };

        var rows = TypeDiff.Build(baselineGraph, baseline, currentGraph, current, (GroupKey)(int)request.GroupBy);
        var unfiltered = rows.Count;
        var newRows = rows.Count(r => r.IsNew);
        var goneRows = rows.Count(r => r.IsGone);
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            rows.RemoveAll(r => !NameFilter.IsMatch(r.Name, request.Filter));
        }

        rows.Sort((a, b) =>
        {
            var primary = request.Descending ? compare(b, a) : compare(a, b);
            return primary != 0 ? primary : string.CompareOrdinal(a.Name, b.Name);
        });

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
