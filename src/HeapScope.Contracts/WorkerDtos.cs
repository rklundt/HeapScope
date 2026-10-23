namespace HeapScope.Contracts;

// DTOs are records of primitives, strings, arrays and Contracts enums only (ARCHITECTURE.md 4.4, ADR-016).

/// <summary>Worker identity and resource state, returned by the handshake.</summary>
/// <remarks><see cref="InstanceId"/> changes whenever a new worker process starts, so clients can tell that worker-owned
/// ids (snapshots, result sets) from an earlier instance are gone.</remarks>
public sealed record WorkerInfo(
    Guid InstanceId,
    int ProtocolVersion,
    string ProductVersion,
    int ProcessId,
    bool ServerGc,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long TotalAvailableMemoryBytes,
    bool TestHooksEnabled);

/// <summary>Progress of a long-running operation. <see cref="Total"/> is null when unknown.</summary>
public sealed record JobProgress(string Phase, long Completed, long? Total, string? Message = null);

/// <summary>Parameters of the synthetic diagnostic job.</summary>
public sealed record SyntheticJobRequest(int ObjectCount, double AverageReferences = 3, int Seed = 42, int TypeCount = 1000);

/// <summary>Outcome of the synthetic diagnostic job.</summary>
public sealed record SyntheticJobResult(
    int ObjectCount,
    int ReferenceCount,
    ulong TotalSize,
    long ColumnBytes,
    double ElapsedSeconds,
    ResultSetInfo TypeSummary);

/// <summary>How a snapshot's graph is held in the worker (ARCHITECTURE.md 6.5, ADR-015).</summary>
public enum SnapshotMemoryMode
{
    /// <summary>Chosen from the memory budget (requests only).</summary>
    Auto = 0,

    /// <summary>Columns are arrays in the worker's memory.</summary>
    InMemory = 1,

    /// <summary>Columns are views of the index cache file.</summary>
    Mapped = 2,
}

/// <summary>Parameters of <see cref="IWorkerService.OpenSnapshotAsync"/>.</summary>
/// <param name="Path">Full path of the dump.</param>
/// <param name="Rebuild">Ignore an existing index cache and load the dump again.</param>
/// <param name="Mode">Memory mode; <see cref="SnapshotMemoryMode.Auto"/> follows the memory budget.</param>
/// <param name="RequireCache">Open only from a valid index cache (lazy reopens after a worker restart).</param>
public sealed record OpenSnapshotRequest(string Path, bool Rebuild = false, SnapshotMemoryMode Mode = SnapshotMemoryMode.Auto, bool RequireCache = false);

/// <summary>State of a snapshot's index cache.</summary>
public enum SnapshotCacheState
{
    Missing = 0,
    Valid = 1,
    Invalid = 2,
}

/// <summary>What <see cref="IWorkerService.ProbeSnapshotAsync"/> found, before any load.</summary>
public sealed record SnapshotProbe(string Path, long FileBytes, SnapshotCacheState CacheState, string CachePath, string? CacheDetail, bool IsOpen);

/// <summary>A loaded snapshot. <see cref="Notes"/> are plain-language data quality notes for the user.</summary>
public sealed record SnapshotDescriptor(
    Guid SnapshotId,
    string Path,
    SnapshotKindDto Kind,
    SnapshotFeatures Features,
    string? ProcessName,
    int? ProcessId,
    string? RuntimeVersion,
    DateTimeOffset? CapturedAtUtc,
    int ObjectCount,
    long ReferenceCount,
    int TypeCount,
    int RootCount,
    ulong TotalSize,
    SnapshotMemoryMode Mode,
    bool FromCache,
    string? CachePath,
    double OpenSeconds,
    IReadOnlyList<string> Notes);

/// <summary>Kind of the values in a result column; cells are sent as invariant-culture strings.</summary>
public enum ResultColumnKind
{
    Text = 0,
    Integer = 1,
    Bytes = 2,
}

/// <summary>One column of a result set.</summary>
public sealed record ResultColumn(string Name, ResultColumnKind Kind);

/// <summary>A result set held by the worker, fetched with <see cref="IWorkerService.GetRowsAsync"/>.</summary>
public sealed record ResultSetInfo(Guid Id, int TotalCount, IReadOnlyList<ResultColumn> Columns);

/// <summary>One page of a result set.</summary>
public sealed record ResultPage(Guid ResultSetId, int Offset, int TotalCount, IReadOnlyList<IReadOnlyList<string?>> Rows);
