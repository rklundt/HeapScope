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

/// <summary>
/// Column names of a type summary result set (sorting takes any of them, sprint 1.5). In Type grouping the rows also carry
/// the hidden <see cref="TypeName"/> and <see cref="Module"/> columns: the key that opens a row's instances.
/// </summary>
public static class TypeSummaryColumns
{
    public const string Name = "Name";
    public const string Objects = "Objects";
    public const string Bytes = "Bytes";
    public const string Percent = "Percent";

    /// <summary>The raw type name (without the module suffix of colliding names).</summary>
    public const string TypeName = "TypeName";

    /// <summary>The module file name, empty when the type has none.</summary>
    public const string Module = "Module";
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
/// <param name="SortColumn">A result column name (<see cref="TypeSummaryColumns"/>); null sorts by bytes.</param>
/// <param name="Descending">Sort direction.</param>
public sealed record TypeSummaryRequest(
    Guid SnapshotId,
    TypeGrouping GroupBy = TypeGrouping.Type,
    string? Filter = null,
    string? SortColumn = null,
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

/// <summary>A snapshot comparison query (sprint 1.4, ADR-018).</summary>
/// <param name="BaselineId">The earlier snapshot; deltas are current minus baseline.</param>
/// <param name="CurrentId">The later snapshot.</param>
/// <param name="GroupBy">Rows per type, namespace or assembly.</param>
/// <param name="Filter">Keeps rows whose name matches (<see cref="NameFilter"/>).</param>
/// <param name="SortColumn">The name of a result column to sort by; null sorts by <see cref="TypeDiffColumns.BytesDelta"/>.</param>
/// <param name="Descending">Sort direction.</param>
public sealed record TypeDiffRequest(
    Guid BaselineId,
    Guid CurrentId,
    TypeGrouping GroupBy = TypeGrouping.Type,
    string? Filter = null,
    string? SortColumn = null,
    bool Descending = true);

/// <summary>Column names of a type diff result set.</summary>
public static class TypeDiffColumns
{
    public const string Name = "Name";
    public const string Change = "Change";
    public const string BaselineTypes = "BaselineTypes";
    public const string CurrentTypes = "CurrentTypes";

    /// <summary>Hidden key column in Type grouping: the raw type name (sprint 1.5).</summary>
    public const string TypeName = "TypeName";

    /// <summary>Hidden key column in Type grouping: the module file name, empty when none (sprint 1.5).</summary>
    public const string Module = "Module";
    public const string BaselineObjects = "BaselineObjects";
    public const string CurrentObjects = "CurrentObjects";
    public const string ObjectsDelta = "ObjectsDelta";
    public const string BaselineBytes = "BaselineBytes";
    public const string CurrentBytes = "CurrentBytes";
    public const string BytesDelta = "BytesDelta";

    /// <summary>The <see cref="Change"/> value of a row present only in the current snapshot.</summary>
    public const string New = "new";

    /// <summary>The <see cref="Change"/> value of a row present only in the baseline.</summary>
    public const string Gone = "gone";

    /// <summary>
    /// The <see cref="Change"/> value of a row whose number of distinct types changed: the same type name and module
    /// loaded a different number of times (for example a plugin reloaded into a new load context while the old copy
    /// stays alive). The counts are in <see cref="BaselineTypes"/> and <see cref="CurrentTypes"/>.
    /// </summary>
    public const string Copies = "copies";
}

/// <summary>
/// The answer to a type diff: rows (columns in <see cref="TypeDiffColumns"/>) and totals. Snapshot totals cover every
/// object of each dump, unreachable ones included; the matching totals cover the rows that pass the filter (the whole
/// heap without one) and are what <c>diff --fail-on-growth</c> checks. <see cref="ExactCounts"/> is false when either
/// snapshot's counts are estimates (a sampled gcdump, ADR-003): the deltas are then estimates too.
/// </summary>
public sealed record TypeDiffResult(
    ResultSetInfo Rows,
    long BaselineObjects,
    long BaselineBytes,
    long CurrentObjects,
    long CurrentBytes,
    int NewRows,
    int GoneRows,
    int UnfilteredRows,
    long MatchingBaselineBytes,
    long MatchingBytesDelta,
    double ElapsedSeconds,
    bool ExactCounts);

/// <summary>Column names of an instances result set (sprint 1.5).</summary>
public static class InstanceColumns
{
    public const string Address = "Address";
    public const string Type = "Type";
    public const string Bytes = "Bytes";
    public const string Generation = "Generation";

    /// <summary>A string's text (when the snapshot has string contents) or an array's length.</summary>
    public const string Value = "Value";
}

/// <summary>
/// The objects of a type (sprint 1.5): every type whose name and module file name match, the key of the summary and
/// diff rows (ADR-018). The filter is an address prefix (<c>0x1f2a</c>), a regular expression on the value
/// (<c>/^Get.*Async$/</c>) or a case-insensitive substring of the value.
/// </summary>
/// <param name="Module">The module file name; null or empty matches types without one.</param>
/// <param name="SortColumn">A column of <see cref="InstanceColumns"/>; null sorts by bytes.</param>
public sealed record InstancesRequest(
    Guid SnapshotId,
    string TypeName,
    string? Module,
    string? Filter = null,
    string? SortColumn = null,
    bool Descending = true);

/// <summary>
/// The answer to an instances query. Totals cover every object of the matched types (before the filter);
/// <see cref="ValuesAvailable"/> is false when the snapshot cannot read string text, so string values are empty and
/// value filters match nothing.
/// </summary>
public sealed record InstancesResult(ResultSetInfo Rows, long TotalObjects, long TotalBytes, int MatchedTypes, bool ValuesAvailable, double ElapsedSeconds);

/// <summary>What an <see cref="ObjectFieldDto"/> holds (mirrors Core's <c>ObjectFieldKind</c>).</summary>
public enum ObjectFieldKindDto
{
    Primitive = 0,
    String = 1,
    Null = 2,
    Reference = 3,
    Struct = 4,
    Array = 5,
    More = 6,
}

/// <summary>A field or array element of an inspected object, formatted for display (sprint 1.5).</summary>
/// <param name="Address">The referenced object for references, strings and arrays; 0 otherwise.</param>
public sealed record ObjectFieldDto(string Name, string TypeName, ObjectFieldKindDto Kind, string Value, ulong Address, IReadOnlyList<ObjectFieldDto> Children);

/// <summary>Inspects one object (sprint 1.5).</summary>
public sealed record ObjectRequest(Guid SnapshotId, ulong Address);

/// <summary>
/// An object: what the graph knows (type, size, heap kind, outgoing reference count, whether a root holds it) and, when
/// the snapshot can read the dump, its fields (<see cref="Fields"/>) and string text (<see cref="Text"/>, the first 10,000
/// characters). <see cref="FieldsUnavailable"/> explains why fields are missing.
/// </summary>
public sealed record ObjectDetails(
    ulong Address,
    string TypeName,
    long Bytes,
    HeapKindDto HeapKind,
    int ReferenceCount,
    bool IsRooted,
    IReadOnlyList<ObjectFieldDto>? Fields,
    string? FieldsUnavailable,
    string? Text);

/// <summary>Column names of a references result set (sprint 1.5).</summary>
public static class ReferenceColumns
{
    /// <summary>The field path or array index holding the reference, when the snapshot has field values.</summary>
    public const string Field = "Field";
    public const string Address = "Address";
    public const string Type = "Type";
    public const string Bytes = "Bytes";
}

/// <summary>The objects one object references, in the graph's order (sprint 1.5).</summary>
public sealed record ReferencesRequest(Guid SnapshotId, ulong Address);

/// <summary>A result set held by the worker, fetched with <see cref="IWorkerService.GetRowsAsync"/>.</summary>
public sealed record ResultSetInfo(Guid Id, int TotalCount, IReadOnlyList<ResultColumn> Columns);

/// <summary>One page of a result set.</summary>
public sealed record ResultPage(Guid ResultSetId, int Offset, int TotalCount, IReadOnlyList<IReadOnlyList<string?>> Rows);
