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
