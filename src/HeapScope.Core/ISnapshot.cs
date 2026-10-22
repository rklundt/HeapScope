using HeapScope.Core.Graph;

namespace HeapScope.Core;

/// <summary>
/// One loaded snapshot: metadata, capabilities and the compact object graph (ARCHITECTURE.md 5.1).
/// Capabilities that need the original file are exposed as optional services.
/// </summary>
public interface ISnapshot : IDisposable
{
    SnapshotInfo Info { get; }

    SnapshotCapabilities Capabilities { get; }

    HeapGraph Graph { get; }

    /// <summary>Returns an optional service (for example a field value reader) when this snapshot provides it.</summary>
    bool TryGetService<TService>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TService? service)
        where TService : class;
}
