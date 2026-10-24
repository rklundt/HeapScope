using System.Globalization;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The objects one object references (sprint 1.5), in the graph's order. With an object reader each reference is named
/// by the field path or array index that holds it; otherwise by its position (<c>#1</c>, <c>#2</c>).
/// </summary>
internal sealed class ReferencesSource : IResultSource
{
    /// <summary>Array elements walked to name references.</summary>
    public const int MaxNamedElements = 1_000_000;

    private static readonly ResultColumn[] s_columns =
    [
        new(ReferenceColumns.Field, ResultColumnKind.Text),
        new(ReferenceColumns.Address, ResultColumnKind.Address, IsKey: true),
        new(ReferenceColumns.Type, ResultColumnKind.Text),
        new(ReferenceColumns.Bytes, ResultColumnKind.Bytes),
    ];

    private readonly HeapGraph _graph;
    private readonly int[] _targets;
    private readonly IReadOnlyDictionary<ulong, string>? _paths;

    private ReferencesSource(HeapGraph graph, int[] targets, IReadOnlyDictionary<ulong, string>? paths)
    {
        _graph = graph;
        _targets = targets;
        _paths = paths;
        EstimatedBytes = (4L * targets.Length) + (paths is null ? 0 : paths.Values.Sum(p => 48L + (2L * p.Length)));
    }

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => _targets.Length;

    public long EstimatedBytes { get; }

    public static ReferencesSource Create(HeapGraph graph, IObjectReader? reader, int objectIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var targets = graph.GetReferences(objectIndex).ToArray();
        var paths = targets.Length > 0 ? reader?.ReadReferencePaths(graph.Addresses[objectIndex], MaxNamedElements, cancellationToken) : null;
        return new ReferencesSource(graph, targets, paths);
    }

    public void FormatRow(int index, string?[] cells)
    {
        var target = _targets[index];
        var address = _graph.Addresses[target];
        cells[0] = _paths is null
            ? string.Create(CultureInfo.InvariantCulture, $"#{index + 1}")
            : _paths.GetValueOrDefault(address, string.Empty);
        cells[1] = string.Create(CultureInfo.InvariantCulture, $"0x{address:x}");
        cells[2] = _graph.Types.GetName(_graph.TypeIndices[target]);
        cells[3] = _graph.GetSize(target).ToString(CultureInfo.InvariantCulture);
    }
}
