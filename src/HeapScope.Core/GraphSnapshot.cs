using System.Diagnostics.CodeAnalysis;
using HeapScope.Core.Graph;

namespace HeapScope.Core;

/// <summary>
/// An <see cref="ISnapshot"/> over a graph with no optional services (synthetic graphs, tests, the worker's
/// diagnostic jobs). Capabilities that need a service (<see cref="ServiceBackedCapabilities"/>) are removed, so a
/// snapshot never claims what it cannot answer (ADR-003). A cache-backed full dump needs its own
/// <see cref="ISnapshot"/> that reopens the dump for those services.
/// </summary>
public sealed class GraphSnapshot : ISnapshot
{
    /// <summary>Capabilities that are only real when the snapshot provides the matching service.</summary>
    public const SnapshotCapabilities ServiceBackedCapabilities =
        SnapshotCapabilities.FieldValues | SnapshotCapabilities.StringContents | SnapshotCapabilities.ThreadStacks;

    private readonly LeaseTracker _leases = new();

    public GraphSnapshot(SnapshotInfo info, SnapshotCapabilities capabilities, HeapGraph graph)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Capabilities = capabilities & ~ServiceBackedCapabilities;
    }

    public SnapshotInfo Info { get; }

    public SnapshotCapabilities Capabilities { get; }

    public HeapGraph Graph { get; }

    public bool TryGetService<TService>([NotNullWhen(true)] out TService? service)
        where TService : class
    {
        service = null;
        return false;
    }

    public SnapshotLease AcquireLease() => _leases.Acquire();

    /// <summary>
    /// Refuses new leases and releases the graph (and any mapped view it owns) once the last lease is returned. Never
    /// blocks or throws; safe to call more than once and from several threads.
    /// </summary>
    public void Dispose() => _leases.Close(Graph.Dispose);
}
