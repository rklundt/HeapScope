namespace HeapScope.Core;

/// <summary>The kind of file a snapshot was loaded from (ADR-003).</summary>
public enum SnapshotKind
{
    /// <summary>A full process memory dump.</summary>
    FullDump = 0,

    /// <summary>A gcdump: the managed object graph only, possibly sampled.</summary>
    GcDump = 1,
}
