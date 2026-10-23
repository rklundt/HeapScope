namespace HeapScope.Contracts;

/// <summary>
/// The worker's JSON-RPC service, called by UI and CLI through Application's worker host (ARCHITECTURE.md 4).
/// Long-running methods take <see cref="IProgress{T}"/> and <see cref="CancellationToken"/> as their last two
/// parameters; StreamJsonRpc marshals both. Large results are returned as result sets and fetched in pages.
/// </summary>
public interface IWorkerService
{
    /// <summary>Handshake: fails with <see cref="HeapScopeErrorCode.ProtocolMismatch"/> when versions differ.</summary>
    Task<WorkerInfo> GetInfoAsync(int clientProtocolVersion, CancellationToken cancellationToken);

    /// <summary>Diagnostic job: builds a synthetic graph and returns a per-type summary result set.</summary>
    Task<SyntheticJobResult> RunSyntheticJobAsync(SyntheticJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Looks at a snapshot file without loading it: its size, whether a valid index cache exists (header and
    /// fingerprint checked, sections not hashed) and whether it is open. Fails with
    /// <see cref="HeapScopeErrorCode.InvalidArgument"/> when the file does not exist.
    /// </summary>
    Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a snapshot file: reuses a valid index cache or loads the dump and writes the cache, then keeps the snapshot
    /// loaded until <see cref="CloseSnapshotAsync"/>. Opening a file that is already open returns the open snapshot
    /// (in the mode it was opened with). With <see cref="OpenSnapshotRequest.RequireCache"/> a missing or invalid cache
    /// fails with <see cref="HeapScopeErrorCode.SnapshotChanged"/> instead of loading the dump.
    /// </summary>
    Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Unloads a snapshot. Closing an unknown snapshot is not an error.</summary>
    Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Per-type count and shallow size of a loaded snapshot as a result set sorted by size. Unknown snapshots fail with
    /// <see cref="HeapScopeErrorCode.SnapshotNotLoaded"/>.
    /// </summary>
    Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// A type summary with grouping, filter and sort, plus totals (sprint 1.3). The first query of a snapshot aggregates
    /// all objects once; later queries reuse that aggregation. Unknown snapshots fail with
    /// <see cref="HeapScopeErrorCode.SnapshotNotLoaded"/>.
    /// </summary>
    Task<TypeSummaryResult> QueryTypeSummaryAsync(TypeSummaryRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Returns rows [offset, offset + count) of a result set; count is capped at
    /// <see cref="WorkerProtocol.MaxPageSize"/>. Unknown, released or evicted sets fail with
    /// <see cref="HeapScopeErrorCode.ResultSetNotFound"/>.
    /// </summary>
    Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken);

    /// <summary>Frees a result set. Releasing an unknown set is not an error.</summary>
    Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken);

    /// <summary>Asks the worker to exit after replying.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken);

    /// <summary>Test hook: terminates the worker abruptly. Needs <c>--enable-test-hooks</c>.</summary>
    Task CrashForTestingAsync(CancellationToken cancellationToken);

    /// <summary>Test hook: waits until canceled. Needs <c>--enable-test-hooks</c>.</summary>
    Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}
