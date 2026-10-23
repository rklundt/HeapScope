using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Loaders.ClrMd;

/// <summary>Progress of a dump load. <see cref="Total"/> is null when unknown.</summary>
public sealed record LoadProgress(string Phase, long Completed, long? Total);

/// <summary>How long each part of a load took.</summary>
public sealed record LoadTimings(TimeSpan Open, TimeSpan Objects, TimeSpan References, TimeSpan Roots, TimeSpan Build)
{
    public TimeSpan Total => Open + Objects + References + Roots + Build;
}

/// <summary>
/// Data quality facts found while loading, shown to the user as notes (ARCHITECTURE.md 6.1). Never contains field
/// values or string contents.
/// </summary>
/// <param name="DebuggableModules">Modules compiled as debuggable code: stack roots in their frames can be missing.</param>
/// <param name="UnreadableObjects">Heap entries skipped because their type could not be read.</param>
/// <param name="StaticFieldRoots">Static field roots found by enumerating statics.</param>
/// <param name="IncompleteSegments">Heap segments whose walk covered less than their object range (missing memory).</param>
public sealed record LoadDiagnostics(IReadOnlyList<string> DebuggableModules, long UnreadableObjects, int StaticFieldRoots, int IncompleteSegments = 0);

/// <summary>The outcome of loading a full dump. Owns the graph until it is handed on.</summary>
public sealed class DumpLoadResult(HeapGraph graph, SnapshotInfo info, SnapshotCapabilities capabilities, LoadDiagnostics diagnostics, LoadTimings timings)
{
    public HeapGraph Graph { get; } = graph ?? throw new ArgumentNullException(nameof(graph));

    public SnapshotInfo Info { get; } = info ?? throw new ArgumentNullException(nameof(info));

    public SnapshotCapabilities Capabilities { get; } = capabilities;

    public LoadDiagnostics Diagnostics { get; } = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

    public LoadTimings Timings { get; } = timings ?? throw new ArgumentNullException(nameof(timings));
}
