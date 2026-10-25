// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

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
    public Task<TypeSummary> QueryTypeSummaryAsync(
        SnapshotHandle handle,
        TypeGrouping groupBy,
        string? filter,
        string? sortColumn,
        bool descending,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken) =>
        QueryTypeSummaryAsync(handle, groupBy, filter, sortColumn, descending, includeRetained: false, progress, cancellationToken);

    /// <summary>
    /// A type summary, with a retained bytes column when <paramref name="includeRetained"/> is set (sprint 2.3; the first
    /// such query of a snapshot computes its dominators).
    /// </summary>
    public async Task<TypeSummary> QueryTypeSummaryAsync(
        SnapshotHandle handle,
        TypeGrouping groupBy,
        string? filter,
        string? sortColumn,
        bool descending,
        bool includeRetained,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        TypeSummaryResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(
                handle,
                (service, id, innerCt) => service.QueryTypeSummaryAsync(new TypeSummaryRequest(id, groupBy, filter, sortColumn, descending, includeRetained), progress, innerCt),
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

    /// <summary>
    /// The objects of a type (sprint 1.5): every type with this name and module file name (the key of summary and diff
    /// rows). The rows re-run the query when the set is gone, reopening the snapshot after a worker restart.
    /// </summary>
    public Task<InstanceList> QueryInstancesAsync(
        SnapshotHandle handle,
        string typeName,
        string? module,
        string? filter,
        string? sortColumn,
        bool descending,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken) =>
        QueryInstancesAsync(handle, new InstancesQuery(typeName, module, filter, sortColumn, descending), progress, cancellationToken);

    /// <summary>
    /// The objects of a type within a scope (sprint 1.6: what an inspection row opens, such as the type's objects on the
    /// large object heap, or the strings equal to one string).
    /// </summary>
    public async Task<InstanceList> QueryInstancesAsync(SnapshotHandle handle, InstancesQuery query, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(query);
        InstancesResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(
                handle,
                (service, id, innerCt) => service.QueryInstancesAsync(
                    new InstancesRequest(id, query.TypeName, query.Module, query.Filter, query.SortColumn, query.Descending, query.Scope, query.SameValueAs, query.IncludeRetained),
                    progress,
                    innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new InstanceList(new ResultSetHandle(worker, info, RunAsync), latest!);
    }

    /// <summary>The inspections for a snapshot, with why each unavailable one cannot run (sprint 1.6).</summary>
    public Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(SnapshotHandle handle, CancellationToken cancellationToken) =>
        UseAsync(handle, (service, id, ct) => service.ListInspectionsAsync(id, ct), cancellationToken);

    /// <summary>
    /// Runs an inspection (sprint 1.6). Its rows re-run it when the set is gone, reopening the snapshot after a worker
    /// restart, like any query.
    /// </summary>
    public async Task<InspectionRun> RunInspectionAsync(SnapshotHandle handle, string name, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(name);
        InspectionResultDto? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(handle, (service, id, innerCt) => service.RunInspectionAsync(new InspectionRequest(id, name), progress, innerCt), ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new InspectionRun(new ResultSetHandle(worker, info, RunAsync), latest!);
    }

    /// <summary>One object's details and, when the snapshot reads its dump, its fields (sprint 1.5).</summary>
    public Task<ObjectDetails> GetObjectAsync(SnapshotHandle handle, ulong address, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        UseAsync(handle, (service, id, ct) => service.GetObjectAsync(new ObjectRequest(id, address), progress, ct), cancellationToken);

    /// <summary>The objects one object references, as rows that survive eviction and worker restarts (sprint 1.5).</summary>
    public async Task<ResultSetHandle> QueryReferencesAsync(SnapshotHandle handle, ulong address, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Task<ResultSetInfo> RunAsync(CancellationToken ct) =>
            UseAsync(handle, (service, id, innerCt) => service.QueryReferencesAsync(new ReferencesRequest(id, address), progress, innerCt), ct);

        return new ResultSetHandle(worker, await RunAsync(cancellationToken).ConfigureAwait(false), RunAsync);
    }

    /// <summary>
    /// The objects referencing one object (sprint 2.1), as rows that survive eviction and worker restarts. The first such
    /// query of a snapshot builds its reverse references index, or opens it from its sidecar.
    /// </summary>
    public async Task<ReferrerList> QueryReferrersAsync(SnapshotHandle handle, ulong address, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ReferrersResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(handle, (service, id, innerCt) => service.QueryReferrersAsync(new ReferrersRequest(id, address), progress, innerCt), ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new ReferrerList(new ResultSetHandle(worker, info, RunAsync), latest!);
    }

    /// <summary>The objects referencing a type's objects, grouped by their own type (sprint 2.1).</summary>
    public async Task<TypeReferrerList> QueryTypeReferrersAsync(
        SnapshotHandle handle,
        string typeName,
        string? module,
        string? sortColumn,
        bool descending,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        TypeReferrersResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(
                handle,
                (service, id, innerCt) => service.QueryTypeReferrersAsync(new TypeReferrersRequest(id, typeName, module, sortColumn, descending), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);
        return new TypeReferrerList(new ResultSetHandle(worker, info, RunAsync), latest!);
    }

    /// <summary>
    /// Why one object is alive (sprint 2.2): its shortest paths from up to <paramref name="maxRoots"/> roots. The first such
    /// query of a snapshot builds its reverse references index, or opens it from its sidecar.
    /// </summary>
    public Task<ObjectPathsResult> QueryObjectPathsAsync(SnapshotHandle handle, ulong address, int maxRoots, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        UseAsync(handle, (service, id, ct) => service.QueryObjectPathsAsync(new ObjectPathsRequest(id, address, maxRoots), progress, ct), cancellationToken);

    /// <summary>One object's retained size and immediate dominator (sprint 2.3).</summary>
    public Task<RetainedResult> QueryRetainedAsync(SnapshotHandle handle, ulong address, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        UseAsync(handle, (service, id, ct) => service.QueryRetainedAsync(new RetainedRequest(id, address), progress, ct), cancellationToken);

    /// <summary>Why the objects of a type are alive (sprint 2.2): their key retention paths, largest first.</summary>
    public Task<TypePathsResult> QueryTypePathsAsync(SnapshotHandle handle, string typeName, string? module, int maxPaths, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        return UseAsync(handle, (service, id, ct) => service.QueryTypePathsAsync(new TypePathsRequest(id, typeName, module, maxPaths), progress, ct), cancellationToken);
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

        // A cache rebuilt meanwhile (another front end, a newer loader, --rebuild) is a different graph: the handle's rows,
        // totals and addresses would no longer match it (sprint 2.1 review M10).
        var opened = handle.Descriptor.CacheIdentity;
        if (opened != 0 && descriptor.CacheIdentity != 0 && descriptor.CacheIdentity != opened)
        {
            await worker.InvokeAsync((service, ct) => service.CloseSnapshotAsync(descriptor.SnapshotId, ct), CancellationToken.None).ConfigureAwait(false);
            throw new WorkerOperationException(
                HeapScopeErrorCode.SnapshotChanged,
                $"The index cache of {System.IO.Path.GetFileName(handle.Path)} was rebuilt since it was opened, so its results could differ. Open it again.",
                null);
        }

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
