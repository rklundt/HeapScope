using System.Globalization;
using HeapScope.Application.Jobs;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.Application.Snapshots;

/// <summary>
/// A snapshot the user opened, stable across worker restarts (ARCHITECTURE.md 4.3): it maps to the worker's snapshot
/// id for one worker generation, and is reopened lazily from the index cache when the worker changed.
/// </summary>
public sealed class SnapshotHandle
{
    internal SnapshotHandle(SnapshotDescriptor descriptor, int generation, SnapshotMemoryMode requestedMode)
    {
        Path = descriptor.Path;
        RequestedMode = requestedMode;
        Update(descriptor, generation);
    }

    /// <summary>Stable id of this handle in the session.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Full path of the snapshot file.</summary>
    public string Path { get; }

    /// <summary>What the worker reported when the snapshot was last opened.</summary>
    public SnapshotDescriptor Descriptor { get; private set; } = null!;

    /// <summary>The memory mode the user asked for; reopens after a worker restart use it again.</summary>
    public SnapshotMemoryMode RequestedMode { get; }

    /// <summary>True once the snapshot was closed; a closed handle is never reopened.</summary>
    public bool IsClosed { get; internal set; }

    internal int Generation { get; private set; }

    internal Guid WorkerSnapshotId => Descriptor.SnapshotId;

    internal void Update(SnapshotDescriptor descriptor, int generation)
    {
        Descriptor = descriptor;
        Generation = generation;
    }
}

/// <summary>
/// The snapshots open in this UI or CLI session. Every operation on a snapshot goes through
/// <see cref="UseAsync{T}"/>, which reopens it after a worker restart (stale ids fail with
/// <see cref="HeapScopeErrorCode.SnapshotNotLoaded"/> in the worker).
/// </summary>
public sealed class SnapshotSession(IWorkerClient worker, JobTracker jobs, Func<PhysicalMemory>? memory = null)
{
    private readonly Func<PhysicalMemory> _memory = memory ?? PhysicalMemory.Current;

    private readonly object _gate = new();
    private readonly List<SnapshotHandle> _snapshots = [];

    /// <summary>Raised on any thread when a snapshot is opened, reopened or closed.</summary>
    public event EventHandler? SnapshotsChanged;

    public IReadOnlyList<SnapshotHandle> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return [.. _snapshots];
            }
        }
    }

    /// <summary>Opens a snapshot file as a tracked job (load or index cache reuse happen in the worker).</summary>
    public TrackedJob<SnapshotHandle> StartOpen(string path, bool rebuild = false, SnapshotMemoryMode mode = SnapshotMemoryMode.Auto)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var title = string.Create(CultureInfo.InvariantCulture, $"Open {System.IO.Path.GetFileName(fullPath)}");
        return jobs.Start(title, async (progress, ct) =>
        {
            var request = new OpenSnapshotRequest(fullPath, rebuild, mode);
            var (descriptor, generation) = await OpenInWorkerAsync(request, progress, ct).ConfigureAwait(false);
            SnapshotHandle handle;
            lock (_gate)
            {
                handle = _snapshots.Find(h => string.Equals(h.Path, descriptor.Path, StringComparison.OrdinalIgnoreCase))
                    ?? Add(new SnapshotHandle(descriptor, generation, mode));
                handle.Update(descriptor, generation);
            }

            SnapshotsChanged?.Invoke(this, EventArgs.Empty);
            return handle;
        });
    }

    /// <summary>
    /// The warning to show before opening <paramref name="path"/>, or null: a first load (no valid index cache, or a
    /// rebuild) whose estimated peak exceeds the memory available now (ARCHITECTURE.md 10). The worker checks the cache
    /// where it really is (next to the dump or in the fallback folder), so UI and CLI share one rule.
    /// </summary>
    public async Task<string?> GetOpenWarningAsync(string path, bool rebuild, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var probe = await worker.InvokeAsync((service, ct) => service.ProbeSnapshotAsync(fullPath, ct), cancellationToken).ConfigureAwait(false);
        if (probe.IsOpen || (probe.CacheState == SnapshotCacheState.Valid && !rebuild))
        {
            return null;
        }

        return SnapshotMemoryEstimate.Warn(probe.FileBytes, _memory());
    }

    /// <summary>Closes a snapshot in the worker (when it is still there) and forgets the handle.</summary>
    public async Task CloseAsync(SnapshotHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (!_snapshots.Remove(handle))
            {
                return;
            }

            handle.IsClosed = true;
        }

        SnapshotsChanged?.Invoke(this, EventArgs.Empty);
        if (handle.Generation == worker.Generation && worker.State == WorkerState.Running)
        {
            await worker.InvokeAsync((service, ct) => service.CloseSnapshotAsync(handle.WorkerSnapshotId, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A type summary with grouping, filter and sort (sprint 1.3). The rows come back as a <see cref="ResultSetHandle"/>
    /// that runs the query again (reopening the snapshot if the worker restarted) when the set is gone.
    /// </summary>
    public async Task<TypeSummary> QueryTypeSummaryAsync(
        SnapshotHandle handle,
        TypeGrouping groupBy,
        string? filter,
        TypeSummarySort sort,
        bool descending,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        TypeSummaryResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(
                handle,
                (service, id, innerCt) => service.QueryTypeSummaryAsync(new TypeSummaryRequest(id, groupBy, filter, sort, descending), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new TypeSummary(new ResultSetHandle(worker, info, RunAsync), latest!);
    }

    /// <summary>
    /// Compares two snapshots by type (sprint 1.4, ADR-018): deltas are <paramref name="current"/> minus
    /// <paramref name="baseline"/>. Like the type summary, the rows re-run the query (reopening either snapshot after a
    /// worker restart) when the set is gone.
    /// </summary>
    public async Task<SnapshotDiff> QueryTypeDiffAsync(
        SnapshotHandle baseline,
        SnapshotHandle current,
        TypeGrouping groupBy,
        string? filter,
        string? sortColumn,
        bool descending,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        TypeDiffResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseBothAsync(
                baseline,
                current,
                (service, baselineId, currentId, innerCt) => service.QueryTypeDiffAsync(
                    new TypeDiffRequest(baselineId, currentId, groupBy, filter, sortColumn, descending), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new SnapshotDiff(new ResultSetHandle(worker, info, RunAsync), latest!);
    }

    /// <summary>Per-type count and shallow size of a snapshot, as a worker result set.</summary>
    public Task<ResultSetInfo> GetTypeSummaryAsync(SnapshotHandle handle, CancellationToken cancellationToken) =>
        UseAsync(handle, (service, id, ct) => service.GetTypeSummaryAsync(id, ct), cancellationToken);

    /// <summary>
    /// Runs <paramref name="call"/> with the snapshot's current worker id, reopening the snapshot first when the worker
    /// restarted since it was opened, and once more when the worker reports it as not loaded.
    /// </summary>
    public async Task<T> UseAsync<T>(SnapshotHandle handle, Func<IWorkerService, Guid, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(call);
        ThrowIfClosed(handle);
        await worker.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (handle.Generation != worker.Generation)
        {
            await ReopenAsync(handle, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await worker.InvokeAsync((service, ct) => call(service, handle.WorkerSnapshotId, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerOperationException ex) when (ex.ErrorCode == HeapScopeErrorCode.SnapshotNotLoaded)
        {
            ThrowIfClosed(handle);
            await ReopenAsync(handle, cancellationToken).ConfigureAwait(false);
            return await worker.InvokeAsync((service, ct) => call(service, handle.WorkerSnapshotId, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see cref="UseAsync{T}"/> for a call on two snapshots. When the worker reports a snapshot as not loaded, both are
    /// reopened from their index caches (reopening one that is still loaded returns it as it is) and the call runs once
    /// more.
    /// </summary>
    private async Task<T> UseBothAsync<T>(
        SnapshotHandle first,
        SnapshotHandle second,
        Func<IWorkerService, Guid, Guid, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        ThrowIfClosed(first);
        ThrowIfClosed(second);
        await worker.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        foreach (var handle in new[] { first, second }.Distinct())
        {
            if (handle.Generation != worker.Generation)
            {
                await ReopenAsync(handle, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            return await worker.InvokeAsync((service, ct) => call(service, first.WorkerSnapshotId, second.WorkerSnapshotId, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerOperationException ex) when (ex.ErrorCode == HeapScopeErrorCode.SnapshotNotLoaded)
        {
            ThrowIfClosed(first);
            ThrowIfClosed(second);
            foreach (var handle in new[] { first, second }.Distinct())
            {
                await ReopenAsync(handle, cancellationToken).ConfigureAwait(false);
            }

            return await worker.InvokeAsync((service, ct) => call(service, first.WorkerSnapshotId, second.WorkerSnapshotId, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reopens a snapshot after a worker restart, from its index cache only: a reopen is cheap and silent, never a
    /// multi-minute dump load the user did not ask for. A changed file fails with
    /// <see cref="HeapScopeErrorCode.SnapshotChanged"/> and the user opens it again (sprint 1.2 review).
    /// </summary>
    private async Task ReopenAsync(SnapshotHandle handle, CancellationToken cancellationToken)
    {
        var request = new OpenSnapshotRequest(handle.Path, Mode: handle.RequestedMode, RequireCache: true);
        var (descriptor, generation) = await OpenInWorkerAsync(request, null, cancellationToken).ConfigureAwait(false);
        bool closedMeanwhile;
        lock (_gate)
        {
            closedMeanwhile = handle.IsClosed;
            if (!closedMeanwhile)
            {
                handle.Update(descriptor, generation);
            }
        }

        if (closedMeanwhile)
        {
            // Closed while the reopen was running: do not leave an orphaned snapshot in the worker.
            await worker.InvokeAsync((service, ct) => service.CloseSnapshotAsync(descriptor.SnapshotId, ct), CancellationToken.None).ConfigureAwait(false);
            ThrowIfClosed(handle);
        }

        SnapshotsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ThrowIfClosed(SnapshotHandle handle)
    {
        if (handle.IsClosed)
        {
            throw new WorkerOperationException(HeapScopeErrorCode.SnapshotNotLoaded, $"{System.IO.Path.GetFileName(handle.Path)} was closed.", null);
        }
    }

    private async Task<(SnapshotDescriptor Descriptor, int Generation)> OpenInWorkerAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await worker.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var generation = worker.Generation;
        var descriptor = await worker.InvokeAsync((service, ct) => service.OpenSnapshotAsync(request, progress, ct), cancellationToken).ConfigureAwait(false);

        // A crash during the call restarts the worker on the next request only, so the generation read before the call
        // is the one that answered.
        return (descriptor, generation);
    }

    private SnapshotHandle Add(SnapshotHandle handle)
    {
        _snapshots.Add(handle);
        return handle;
    }
}
