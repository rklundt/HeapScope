using System.Globalization;
using HeapScope.Contracts;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>Per-type count and shallow size, sorted by size descending, held as parallel arrays.</summary>
internal sealed class TypeSummarySource : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new("Type", ResultColumnKind.Text),
        new("Count", ResultColumnKind.Integer),
        new("ShallowSize", ResultColumnKind.Bytes),
    ];

    private readonly string[] _names;
    private readonly long[] _counts;
    private readonly ulong[] _sizes;

    private TypeSummarySource(string[] names, long[] counts, ulong[] sizes, long nameBytes)
    {
        _names = names;
        _counts = counts;
        _sizes = sizes;
        EstimatedBytes = nameBytes + ((long)names.Length * (sizeof(long) + sizeof(ulong) + IntPtr.Size));
    }

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => _names.Length;

    public long EstimatedBytes { get; }

    /// <summary>Aggregates the graph by type. One pass over the objects; the result does not reference the graph.</summary>
    public static TypeSummarySource Create(HeapGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var typeCount = graph.Types.Count;
        var counts = new long[typeCount];
        var sizes = new ulong[typeCount];
        var types = graph.TypeIndices.Span;
        for (var i = 0; i < types.Length; i++)
        {
            counts[types[i]]++;
            sizes[types[i]] += graph.GetSize(i);
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var present = 0;
        for (var t = 0; t < typeCount; t++)
        {
            if (counts[t] > 0)
            {
                present++;
            }
        }

        var order = new int[present];
        var keys = new ulong[present];
        for (int t = 0, r = 0; t < typeCount; t++)
        {
            if (counts[t] > 0)
            {
                order[r] = t;
                keys[r] = ulong.MaxValue - sizes[t];
                r++;
            }
        }

        Array.Sort(keys, order);
        var names = new string[present];
        var rowCounts = new long[present];
        var rowSizes = new ulong[present];
        long nameBytes = 0;
        for (var r = 0; r < present; r++)
        {
            var t = order[r];
            names[r] = graph.Types.GetName(t);
            nameBytes += 24 + (2L * names[r].Length);
            rowCounts[r] = counts[t];
            rowSizes[r] = sizes[t];
        }

        return new TypeSummarySource(names, rowCounts, rowSizes, nameBytes);
    }

    public void FormatRow(int index, string?[] cells)
    {
        cells[0] = _names[index];
        cells[1] = _counts[index].ToString(CultureInfo.InvariantCulture);
        cells[2] = _sizes[index].ToString(CultureInfo.InvariantCulture);
    }
}
