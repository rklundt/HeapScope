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

    /// <summary>A percentage, 0 to 100, with up to two decimals.</summary>
    Percent = 3,

    /// <summary>An object address, as 0x-prefixed hexadecimal.</summary>
    Address = 4,

    /// <summary>A signed integer such as a delta (diffs, sprint 1.4).</summary>
    SignedInteger = 5,
}

/// <summary>One column of a result set. <see cref="IsKey"/> marks the column that names a row (for example the type).</summary>
public sealed record ResultColumn(string Name, ResultColumnKind Kind, bool IsKey = false);

/// <summary>What the rows of a type summary are grouped by.</summary>
public enum TypeGrouping
{
    Type = 0,
    Namespace = 1,
    Assembly = 2,
}

/// <summary>Columns a type summary can be sorted by.</summary>
public enum TypeSummarySort
{
    Bytes = 0,
    Count = 1,
    Name = 2,
}

/// <summary>
/// Heap kinds, in the order the per-kind columns and totals use (mirrors Core's <c>HeapKind</c>). Unknown covers objects
/// whose generation the loader could not tell; it appears in the totals only when some bytes have it.
/// </summary>
public enum HeapKindDto
{
    Unknown = 0,
    Gen0 = 1,
    Gen1 = 2,
    Gen2 = 3,
    LargeObjectHeap = 4,
    PinnedObjectHeap = 5,
    Frozen = 6,
}

/// <summary>A type summary query (sprint 1.3).</summary>
/// <param name="SnapshotId">The snapshot.</param>
/// <param name="GroupBy">Rows per type, namespace or assembly.</param>
/// <param name="Filter">
/// Keeps rows whose name matches (<see cref="NameFilter"/>): an all lower-case filter ignores case; a filter with capitals
/// matches as typed or as CamelHumps; <c>*</c> matches any run of characters, ignoring case.
/// </param>
/// <param name="Sort">Sort column.</param>
/// <param name="Descending">Sort direction.</param>
public sealed record TypeSummaryRequest(
    Guid SnapshotId,
    TypeGrouping GroupBy = TypeGrouping.Type,
    string? Filter = null,
    TypeSummarySort Sort = TypeSummarySort.Bytes,
    bool Descending = true);

/// <summary>Totals for one heap kind.</summary>
public sealed record HeapKindTotal(HeapKindDto Kind, long Objects, long Bytes);

/// <summary>
/// The answer to a type summary: the rows as a result set (columns: name, objects, bytes, percent, then bytes per heap
/// kind when <see cref="HasGenerations"/>), and totals over the whole snapshot (not only the filtered rows). Totals and
/// rows count every object in the dump, including unreachable objects the GC has not collected yet; reachability comes
/// with the retention analysis (2.x).
/// </summary>
/// <param name="TotalTypes">Distinct types present in the snapshot.</param>
/// <param name="UnfilteredRows">Rows of the requested grouping before the filter (types, namespaces or assemblies).</param>
public sealed record TypeSummaryResult(
    ResultSetInfo Rows,
    long TotalObjects,
    long TotalBytes,
    int TotalTypes,
    bool HasGenerations,
    IReadOnlyList<HeapKindTotal> HeapKinds,
    double ElapsedSeconds,
    int UnfilteredRows);

/// <summary>A result set held by the worker, fetched with <see cref="IWorkerService.GetRowsAsync"/>.</summary>
public sealed record ResultSetInfo(Guid Id, int TotalCount, IReadOnlyList<ResultColumn> Columns);

/// <summary>One page of a result set.</summary>
public sealed record ResultPage(Guid ResultSetId, int Offset, int TotalCount, IReadOnlyList<IReadOnlyList<string?>> Rows);
