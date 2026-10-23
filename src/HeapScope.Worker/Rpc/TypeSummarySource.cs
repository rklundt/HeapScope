using System.Globalization;
using HeapScope.Analysis;
using HeapScope.Contracts;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Summary rows (per type, namespace or assembly) held as compact arrays and formatted only for the requested page
/// (ARCHITECTURE.md 4.6). Columns: name (key), objects, bytes, percent of all bytes, then bytes per heap kind when the
/// snapshot has generations (sprint 1.3).
/// </summary>
internal sealed class TypeSummarySource : IResultSource
{
    /// <summary>The heap kinds shown as columns, in order.</summary>
    public static readonly HeapKind[] KindColumns =
        [HeapKind.Gen0, HeapKind.Gen1, HeapKind.Gen2, HeapKind.LargeObjectHeap, HeapKind.PinnedObjectHeap, HeapKind.Frozen];

    private static readonly ResultColumn[] s_baseColumns =
    [
        new("Name", ResultColumnKind.Text, IsKey: true),
        new("Objects", ResultColumnKind.Integer),
        new("Bytes", ResultColumnKind.Bytes),
        new("Percent", ResultColumnKind.Percent),
    ];

    private static readonly ResultColumn[] s_generationColumns =
        [.. s_baseColumns, .. KindColumns.Select(k => new ResultColumn(k.ToString(), ResultColumnKind.Bytes))];

    private readonly SummaryRow[] _rows;
    private readonly ulong _totalBytes;
    private readonly bool _generations;

    private TypeSummarySource(SummaryRow[] rows, int unfilteredCount, ulong totalBytes, bool generations)
    {
        _rows = rows;
        UnfilteredCount = unfilteredCount;
        _totalBytes = totalBytes;
        _generations = generations;
        EstimatedBytes = rows.Sum(r => 48L + (2L * r.Name.Length) + (8L * r.BytesByKind.Length));
    }

    public IReadOnlyList<ResultColumn> Columns => _generations ? s_generationColumns : s_baseColumns;

    public int Count => _rows.Length;

    /// <summary>Rows of the grouping before the filter.</summary>
    public int UnfilteredCount { get; }

    public long EstimatedBytes { get; }

    /// <summary>The default summary: one row per type, largest first (the 1.2 query and the synthetic job).</summary>
    public static TypeSummarySource Create(HeapGraph graph, CancellationToken cancellationToken) =>
        Create(graph, TypeAggregation.Compute(graph, cancellationToken: cancellationToken), new TypeSummaryRequest(Guid.Empty), generations: false);

    /// <summary>Groups, filters and sorts <paramref name="aggregation"/> as <paramref name="request"/> asks.</summary>
    public static TypeSummarySource Create(HeapGraph graph, TypeAggregation aggregation, TypeSummaryRequest request, bool generations)
    {
        ArgumentNullException.ThrowIfNull(aggregation);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.GroupBy) || !Enum.IsDefined(request.Sort))
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"Unknown grouping {(int)request.GroupBy} or sort {(int)request.Sort}.");
        }

        var rows = TypeGroups.Build(graph, aggregation, (GroupKey)(int)request.GroupBy);
        var unfiltered = rows.Count;
        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            rows.RemoveAll(r => !NameFilter.IsMatch(r.Name, request.Filter));
        }

        Comparison<SummaryRow> compare = request.Sort switch
        {
            TypeSummarySort.Count => (a, b) => a.Count.CompareTo(b.Count),
            TypeSummarySort.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => a.Bytes.CompareTo(b.Bytes),
        };

        // Ties by name keep the order stable between queries.
        rows.Sort((a, b) =>
        {
            var primary = request.Descending ? compare(b, a) : compare(a, b);
            return primary != 0 ? primary : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return new TypeSummarySource([.. rows], unfiltered, aggregation.TotalBytes, generations);
    }

    public void FormatRow(int index, string?[] cells)
    {
        var row = _rows[index];
        cells[0] = row.Name;
        cells[1] = row.Count.ToString(CultureInfo.InvariantCulture);
        cells[2] = row.Bytes.ToString(CultureInfo.InvariantCulture);
        cells[3] = (_totalBytes == 0 ? 0 : 100.0 * row.Bytes / _totalBytes).ToString("0.##", CultureInfo.InvariantCulture);
        if (_generations)
        {
            for (var k = 0; k < KindColumns.Length; k++)
            {
                cells[4 + k] = row.BytesByKind[(int)KindColumns[k]].ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
