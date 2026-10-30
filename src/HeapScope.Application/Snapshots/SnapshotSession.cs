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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HeapScope.Application.Events;

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
public sealed class SnapshotSession(IWorkerClient worker, JobTracker jobs, Func<PhysicalMemory>? memory = null, ILogger<SnapshotSession>? logger = null, TrustedDacs? trustedDacs = null)
{
    /// <summary>The DACs trusted to load from next to a dump, sent with every open (sprint 3.6); null trusts none.</summary>
    public TrustedDacs? TrustedDacs { get; } = trustedDacs;

    private readonly Func<PhysicalMemory> _memory = memory ?? PhysicalMemory.Current;
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

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

    /// <summary>
    /// Whether opens from an index cache refuse a full dump whose DAC lies untrusted next to it (sprint 3.6): true in the
    /// app, which asks; false in the CLI, whose commands that need the DAC refuse when they read with it and name
    /// <c>--trust-dac</c>.
    /// </summary>
    public bool CheckDacOnCacheOpen { get; set; } = true;

    /// <summary>
    /// Opens a snapshot file as a tracked job (load or index cache reuse happen in the worker). <paramref name="checkDac"/>
    /// overrides <see cref="CheckDacOnCacheOpen"/> (false after the user declined to trust the DAC).
    /// </summary>
    public TrackedJob<SnapshotHandle> StartOpen(string path, bool rebuild = false, SnapshotMemoryMode mode = SnapshotMemoryMode.Auto, bool? checkDac = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var title = string.Create(CultureInfo.InvariantCulture, $"Open {System.IO.Path.GetFileName(fullPath)}");
        return jobs.Start(title, async (progress, ct) =>
        {
            var request = new OpenSnapshotRequest(fullPath, rebuild, mode, TrustedDacs: TrustedDacs?.All(), CheckDac: checkDac ?? CheckDacOnCacheOpen);
            LogTrustedDacs(fullPath);
            var (descriptor, generation) = await OpenInWorkerAsync(request, progress, ct).ConfigureAwait(false);
            SnapshotHandle handle;
            lock (_gate)
            {
                handle = _snapshots.Find(h => string.Equals(h.Path, descriptor.Path, StringComparison.OrdinalIgnoreCase))
                    ?? Add(new SnapshotHandle(descriptor, generation, mode));
                handle.Update(descriptor, generation);
            }

            // The worker's identity line (sprint 2.3.7 review H2) has the details; this ties the front end's snapshot to it.
            _logger.LogInformation(
                "Snapshot {Path} opened as worker snapshot {WorkerSnapshotId} (worker generation {Generation}, {Source}, cache identity {Identity:x16}, rebuild {Rebuild}, mode {Mode})",
                descriptor.Path,
                descriptor.SnapshotId,
                generation,
                descriptor.FromCache ? "index cache" : "dump",
                descriptor.CacheIdentity,
                rebuild,
                descriptor.Mode);
            SafeEvents.Raise(SnapshotsChanged, this, _logger, nameof(SnapshotsChanged));
            return handle;
        });
    }

    /// <summary>
    /// Says, under the open's trace id, which DACs the open trusts and who trusted each when (sprint 3.6 review H2): the
    /// worker's DAC choice line names the hash it loaded, this line where that trust came from.
    /// </summary>
    private void LogTrustedDacs(string path)
    {
        if (TrustedDacs is { } trusted)
        {
            _logger.LogInformation("Opening {Path} with these DACs trusted to load from next to a dump: {Trusted}", path, trusted.Describe());
        }
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

        _logger.LogInformation("Snapshot {Path} (worker snapshot {WorkerSnapshotId}) closed", handle.Path, handle.WorkerSnapshotId);
        SafeEvents.Raise(SnapshotsChanged, this, _logger, nameof(SnapshotsChanged));
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
        CancellationToken cancellationToken,
        ObjectSetQuery? set = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        TypeSummaryResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseWithSetAsync(
                handle,
                set,
                (service, id, spec, innerCt) => service.QueryTypeSummaryAsync(new TypeSummaryRequest(id, groupBy, filter, sortColumn, descending, includeRetained, spec), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new TypeSummary(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
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
        CancellationToken cancellationToken,
        bool matchObjects = false,
        bool includeRetained = false)
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
                    new TypeDiffRequest(baselineId, currentId, groupBy, filter, sortColumn, descending, matchObjects, includeRetained), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new SnapshotDiff(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
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
            latest = await UseWithSetAsync(
                handle,
                query.Set,
                (service, id, spec, innerCt) => service.QueryInstancesAsync(
                    new InstancesRequest(id, query.TypeName, query.Module, query.Filter, query.SortColumn, query.Descending, query.Scope, query.SameValueAs, query.IncludeRetained, spec),
                    progress,
                    innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new InstanceList(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    /// <summary>The inspections for a snapshot, with why each unavailable one cannot run (sprint 1.6).</summary>
    public Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(SnapshotHandle handle, CancellationToken cancellationToken) =>
        UseAsync(handle, (service, id, ct) => service.ListInspectionsAsync(id, ct), cancellationToken);

    /// <summary>
    /// Runs an inspection (sprint 1.6). Its rows re-run it when the set is gone, reopening the snapshot after a worker
    /// restart, like any query. <paramref name="baseline"/> is an earlier snapshot for the inspections that measure growth
    /// (sprint 2.6); both are reopened together when needed.
    /// </summary>
    public async Task<InspectionRun> RunInspectionAsync(SnapshotHandle handle, string name, IProgress<JobProgress>? progress, CancellationToken cancellationToken, SnapshotHandle? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(name);
        InspectionResultDto? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = baseline is null
                ? await UseAsync(handle, (service, id, innerCt) => service.RunInspectionAsync(new InspectionRequest(id, name), progress, innerCt), ct).ConfigureAwait(false)
                : await UseBothAsync(handle, baseline, (service, id, baselineId, innerCt) => service.RunInspectionAsync(new InspectionRequest(id, name, baselineId), progress, innerCt), ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return new InspectionRun(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
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

        return new ResultSetHandle(worker, await RunAsync(cancellationToken).ConfigureAwait(false), RunAsync, _logger);
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
        return new ReferrerList(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
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
        return new TypeReferrerList(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    /// <summary>
    /// The dominator tree's rows (sprint 2.4): the top level (the biggest retainers) or the tree below
    /// <paramref name="focusAddress"/>, with the <paramref name="expanded"/> rows expanded. A re-run after a worker restart
    /// keeps the expansion: the keys are addresses and group names. A node lists at most <paramref name="maxChildren"/>
    /// children before a "more" row; the answer says at which row <paramref name="revealKey"/> landed.
    /// </summary>
    public async Task<DominatorTreeAnswer> QueryDominatorTreeAsync(
        SnapshotHandle handle,
        IReadOnlyList<string> expanded,
        DominatorGroupingDto grouping,
        ulong focusAddress,
        int maxChildren,
        string? revealKey,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(expanded);
        string[] keys = [.. expanded];
        DominatorTreeResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(
                handle,
                (service, id, innerCt) => service.QueryDominatorTreeAsync(new DominatorTreeRequest(id, keys, grouping, focusAddress, maxChildren, revealKey), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);
        return new DominatorTreeAnswer(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    /// <summary>The icicle chart below <paramref name="focusAddress"/> (0 for the whole reachable heap). Sprint 2.4.</summary>
    public Task<IcicleResult> QueryIcicleAsync(SnapshotHandle handle, ulong focusAddress, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        UseAsync(handle, (service, id, ct) => service.QueryIcicleAsync(new IcicleRequest(id, focusAddress), progress, ct), cancellationToken);

    /// <summary>
    /// What every object of a type retains together, exactly (sprint 2.4), by type; the totals next to the type summary's
    /// lower bound.
    /// </summary>
    public async Task<RetainedBySetAnswer> QueryRetainedBySetAsync(SnapshotHandle handle, string typeName, string? module, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        RetainedBySetResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(
                handle,
                (service, id, innerCt) => service.QueryRetainedBySetAsync(new RetainedBySetRequest(id, typeName, module), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Types;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);
        return new RetainedBySetAnswer(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    /// <summary>What the members of any object set retain together, exactly (sprint 3.1), by type.</summary>
    public async Task<RetainedBySetAnswer> QueryRetainedBySetAsync(SnapshotHandle handle, ObjectSetQuery set, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(set);
        RetainedBySetResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseWithSetAsync(
                handle,
                set,
                (service, id, spec, innerCt) => service.QueryRetainedBySetAsync(new RetainedBySetRequest(id, string.Empty, null, spec), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Types;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);
        return new RetainedBySetAnswer(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    /// <summary>The members of a set that no other member dominates, with what each retains (sprint 3.1).</summary>
    public async Task<SetDominatorsAnswer> QuerySetDominatorsAsync(SnapshotHandle handle, ObjectSetQuery set, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(set);
        SetDominatorsResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseWithSetAsync(
                handle,
                set,
                (service, id, spec, innerCt) => service.QuerySetDominatorsAsync(new SetDominatorsRequest(id, spec!), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);
        return new SetDominatorsAnswer(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    /// <summary>
    /// Runs <paramref name="call"/> with the snapshot's id and the set's spec bound to the current ids (sprint 3.1): a set
    /// with an inspection baseline uses both snapshots, so either one is reopened after a worker restart.
    /// </summary>
    private Task<T> UseWithSetAsync<T>(SnapshotHandle handle, ObjectSetQuery? set, Func<IWorkerService, Guid, ObjectSetSpec?, CancellationToken, Task<T>> call, CancellationToken cancellationToken) =>
        set?.Baseline is { } baseline
            ? UseBothAsync(handle, baseline, (service, id, baselineId, ct) => call(service, id, set.Bind(baselineId), ct), cancellationToken)
            : UseAsync(handle, (service, id, ct) => call(service, id, set?.Spec, ct), cancellationToken);

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

    /// <summary>The key paths of a set's members (sprint 3.2).</summary>
    public Task<TypePathsResult> QuerySetPathsAsync(SnapshotHandle handle, ObjectSetQuery set, int maxPaths, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        return UseWithSetAsync(handle, set, (service, id, spec, ct) => service.QueryTypePathsAsync(new TypePathsRequest(id, string.Empty, null, maxPaths, spec), progress, ct), cancellationToken);
    }

    /// <summary>A set's members grouped by retention pattern (sprint 3.2, ADR-031).</summary>
    public Task<SimilarRetentionResult> QuerySimilarRetentionAsync(SnapshotHandle handle, ObjectSetQuery set, int maxGroups, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        return UseWithSetAsync(handle, set, (service, id, spec, ct) => service.QuerySimilarRetentionAsync(new SimilarRetentionRequest(id, spec!, maxGroups), progress, ct), cancellationToken);
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
    /// A trend across <paramref name="snapshots"/> in the order given (sprint 3.3). The rows re-run the query, reopening any
    /// snapshot after a worker restart, when the set is gone.
    /// </summary>
    public async Task<SnapshotTrend> QueryTypeTrendAsync(
        IReadOnlyList<SnapshotHandle> snapshots,
        TypeGrouping groupBy,
        string? filter,
        string? sortColumn,
        bool descending,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        TypeTrendResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseManyAsync(
                snapshots,
                (service, ids, innerCt) => service.QueryTypeTrendAsync(new TypeTrendRequest(ids, groupBy, filter, sortColumn, descending), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);
        return new SnapshotTrend(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    /// <summary>Like <see cref="UseBothAsync"/> for any number of snapshots (sprint 3.3, trends).</summary>
    private async Task<T> UseManyAsync<T>(IReadOnlyList<SnapshotHandle> handles, Func<IWorkerService, IReadOnlyList<Guid>, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        foreach (var handle in handles)
        {
            ThrowIfClosed(handle);
        }

        await worker.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        foreach (var handle in handles.Distinct())
        {
            if (handle.Generation != worker.Generation)
            {
                await ReopenAsync(handle, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            return await worker.InvokeAsync((service, ct) => call(service, [.. handles.Select(h => h.WorkerSnapshotId)], ct), cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerOperationException ex) when (ex.ErrorCode == HeapScopeErrorCode.SnapshotNotLoaded)
        {
            foreach (var handle in handles.Distinct())
            {
                ThrowIfClosed(handle);
                await ReopenAsync(handle, cancellationToken).ConfigureAwait(false);
            }

            return await worker.InvokeAsync((service, ct) => call(service, [.. handles.Select(h => h.WorkerSnapshotId)], ct), cancellationToken).ConfigureAwait(false);
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
        // A reopen after a restart never asks: nobody is there to answer. Reads that need an untrusted DAC refuse instead.
        var request = new OpenSnapshotRequest(handle.Path, Mode: handle.RequestedMode, RequireCache: true, TrustedDacs: TrustedDacs?.All(), CheckDac: false);
        LogTrustedDacs(handle.Path);
        var previous = (handle.WorkerSnapshotId, handle.Generation);
        _logger.LogInformation("Reopening {Path} after a worker restart (was worker snapshot {WorkerSnapshotId}, generation {Generation})", handle.Path, previous.WorkerSnapshotId, previous.Generation);
        (SnapshotDescriptor Descriptor, int Generation) reopened;
        try
        {
            reopened = await OpenInWorkerAsync(request, null, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerOperationException ex)
        {
            _logger.LogInformation("Reopening {Path} failed ({Code}): {Message}", handle.Path, ex.ErrorCode, ex.Message);
            throw;
        }

        var (descriptor, generation) = reopened;

        // A cache rebuilt meanwhile (another front end, a newer loader, --rebuild) is a different graph: the handle's rows,
        // totals and addresses would no longer match it (sprint 2.1 review M10).
        var opened = handle.Descriptor.CacheIdentity;
        if (opened != 0 && descriptor.CacheIdentity != 0 && descriptor.CacheIdentity != opened)
        {
            _logger.LogInformation(
                "Not reopening {Path}: its index cache was rebuilt since it was opened (identity {Opened:x16}, now {Now:x16})",
                handle.Path,
                opened,
                descriptor.CacheIdentity);
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

        _logger.LogInformation(
            "Snapshot {Path} reopened: worker snapshot {OldId} (generation {OldGeneration}) is now {NewId} (generation {NewGeneration})",
            handle.Path,
            previous.WorkerSnapshotId,
            previous.Generation,
            descriptor.SnapshotId,
            generation);
        if (closedMeanwhile)
        {
            // Closed while the reopen was running: do not leave an orphaned snapshot in the worker.
            await worker.InvokeAsync((service, ct) => service.CloseSnapshotAsync(descriptor.SnapshotId, ct), CancellationToken.None).ConfigureAwait(false);
            ThrowIfClosed(handle);
        }

        SafeEvents.Raise(SnapshotsChanged, this, _logger, nameof(SnapshotsChanged));
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
