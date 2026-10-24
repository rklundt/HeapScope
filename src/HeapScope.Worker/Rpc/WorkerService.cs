using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>The worker's implementation of <see cref="IWorkerService"/>; an RPC boundary (ARCHITECTURE.md 11).</summary>
internal sealed class WorkerService(
    WorkerOptions options,
    ResultSetStore resultSets,
    SnapshotStore snapshots,
    IHostApplicationLifetime lifetime,
    ILogger<WorkerService> logger) : IWorkerService
{
    private const int MaxSyntheticObjects = 100_000_000;

    // Peak bytes per object and per reference while building a synthetic graph: final columns (22 bytes per
    // object) plus chunked phase-one buffers, and the edge chunks and final edge array alive together (8 bytes per
    // reference) during the copy (gc-specialist review of 0.3).
    private const double PeakBytesPerObject = 44;
    private const double PeakBytesPerReference = 8;

    private readonly SemaphoreSlim _syntheticGate = new(1, 1);

    /// <summary>Changes with every worker process (ARCHITECTURE.md 4.3).</summary>
    public Guid InstanceId { get; } = Guid.NewGuid();

    public Task<WorkerInfo> GetInfoAsync(int clientProtocolVersion, CancellationToken cancellationToken)
    {
        if (clientProtocolVersion != WorkerProtocol.Version)
        {
            throw WorkerErrors.Create(
                HeapScopeErrorCode.ProtocolMismatch,
                $"The client speaks worker protocol {clientProtocolVersion} but this worker speaks {WorkerProtocol.Version}. Use a matching HeapScope build.");
        }

        using var process = Process.GetCurrentProcess();
        var info = new WorkerInfo(
            InstanceId,
            WorkerProtocol.Version,
            ProductInfo.Version,
            Environment.ProcessId,
            GCSettings.IsServerGC,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            options.EnableTestHooks);
        return Task.FromResult(info);
    }

    public Task<SyntheticJobResult> RunSyntheticJobAsync(SyntheticJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            Validate(request);

            // One synthetic job at a time: two large builds must not stack their peaks.
            await _syntheticGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                logger.LogInformation("Synthetic job started: {Objects} objects, {References} references per object", request.ObjectCount, request.AverageReferences);
                var result = await Task.Run(() => RunSynthetic(request, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Synthetic job finished in {Seconds:N2} s", result.ElapsedSeconds);
                return result;
            }
            finally
            {
                _syntheticGate.Release();
            }
        });

    public Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken) =>
        GuardAsync(() => Task.Run(() => snapshots.Probe(path), cancellationToken));

    public Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            logger.LogInformation("Opening snapshot (rebuild {Rebuild}, mode {Mode})", request.Rebuild, request.Mode);
            return snapshots.OpenAsync(request, progress, cancellationToken);
        });

    public Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        snapshots.Close(snapshotId);
        return Task.CompletedTask;
    }

    public async Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        (await QueryTypeSummaryAsync(new TypeSummaryRequest(snapshotId), null, cancellationToken).ConfigureAwait(false)).Rows;

    public Task<TypeSummaryResult> QueryTypeSummaryAsync(TypeSummaryRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();
            var snapshot = snapshots.TryGet(request.SnapshotId) ?? throw SnapshotNotLoaded(request.SnapshotId);

            // The lease keeps a mapped graph alive while the aggregation and the grouping read it; the result source
            // itself holds only names and totals. A snapshot closed between the lookup and here refuses the lease.
            SnapshotLease lease;
            try
            {
                lease = snapshot.AcquireLease();
            }
            catch (ObjectDisposedException)
            {
                throw SnapshotNotLoaded(request.SnapshotId);
            }

            using var _ = lease;
            var throttled = new ThrottledProgress<JobProgress>(progress);
            var total = snapshot.Graph.ObjectCount;
            var aggregation = await snapshots.GetAggregationAsync(
                request.SnapshotId,
                new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", done, total))),
                cancellationToken).ConfigureAwait(false) ?? throw SnapshotNotLoaded(request.SnapshotId);
            var generations = snapshot.Capabilities.HasFlag(SnapshotCapabilities.Generations);
            var source = await Task.Run(() => TypeSummarySource.Create(snapshot.Graph, aggregation, request, generations), cancellationToken).ConfigureAwait(false);
            throttled.Flush();
            var rows = resultSets.Add(source);
            // Unknown (generation not determined) is listed only when present, so the bar always adds up to the total.
            var kinds = generations
                ? TypeSummarySource.KindColumns.Prepend(Core.Graph.HeapKind.Unknown)
                    .Where(k => k != Core.Graph.HeapKind.Unknown || aggregation.KindBytes[(int)k] > 0)
                    .Select(k => new HeapKindTotal((HeapKindDto)(int)k, aggregation.KindObjects[(int)k], (long)aggregation.KindBytes[(int)k]))
                    .ToArray()
                : [];
            return new TypeSummaryResult(
                rows,
                aggregation.TotalObjects,
                (long)aggregation.TotalBytes,
                aggregation.Counts.Count(c => c > 0),
                generations,
                kinds,
                stopwatch.Elapsed.TotalSeconds,
                source.UnfilteredCount);
        });

    public Task<TypeDiffResult> QueryTypeDiffAsync(TypeDiffRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();

            // Both graphs stay leased while their aggregations and names are read; the source keeps only names and totals.
            var (baseline, baselineLease) = Lease(request.BaselineId);
            using var _ = baselineLease;
            var (current, currentLease) = Lease(request.CurrentId);
            using var __ = currentLease;
            var throttled = new ThrottledProgress<JobProgress>(progress);
            var total = baseline.Graph.ObjectCount + (long)current.Graph.ObjectCount;
            long baselineDone = 0;
            var baselineAggregation = await snapshots.GetAggregationAsync(
                request.BaselineId,
                new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", baselineDone = done, total))),
                cancellationToken).ConfigureAwait(false) ?? throw SnapshotNotLoaded(request.BaselineId);
            var currentAggregation = await snapshots.GetAggregationAsync(
                request.CurrentId,
                new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", baselineDone + done, total))),
                cancellationToken).ConfigureAwait(false) ?? throw SnapshotNotLoaded(request.CurrentId);
            var source = await Task.Run(
                () => TypeDiffSource.Create(baseline.Graph, baselineAggregation, current.Graph, currentAggregation, request),
                cancellationToken).ConfigureAwait(false);
            throttled.Flush();
            var rows = resultSets.Add(source);
            return new TypeDiffResult(
                rows,
                baselineAggregation.TotalObjects,
                (long)baselineAggregation.TotalBytes,
                currentAggregation.TotalObjects,
                (long)currentAggregation.TotalBytes,
                source.NewRows,
                source.GoneRows,
                source.UnfilteredCount,
                source.MatchingBaselineBytes,
                source.MatchingBytesDelta,
                stopwatch.Elapsed.TotalSeconds,
                baseline.Capabilities.HasFlag(SnapshotCapabilities.ExactCounts) && current.Capabilities.HasFlag(SnapshotCapabilities.ExactCounts));
        });

    public Task<InstancesResult> QueryInstancesAsync(InstancesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();

            // The set keeps the lease: its pages read the graph and the dump (string values) while it lives.
            var (snapshot, lease) = Lease(request.SnapshotId);
            try
            {
                snapshot.TryGetService<IObjectReader>(out var reader);
                var throttled = new ThrottledProgress<JobProgress>(progress);
                var source = await Task.Run(() => InstancesSource.Create(snapshot.Graph, reader, request, cancellationToken, throttled), cancellationToken).ConfigureAwait(false);
                var rows = resultSets.Add(source, lease);
                return new InstancesResult(rows, source.TotalObjects, source.TotalBytes, source.MatchedTypes, reader is not null, stopwatch.Elapsed.TotalSeconds);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });

    public Task<ObjectDetails> GetObjectAsync(ObjectRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            progress?.Report(new JobProgress("Reading the object", 0, null));
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            if (!graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            var type = graph.TypeIndices[index];
            IReadOnlyList<ObjectFieldDto>? fields = null;
            string? unavailable = null;
            string? text = null;
            if (snapshot.TryGetService<IObjectReader>(out var reader))
            {
                (fields, text) = await Task.Run(
                    () =>
                    {
                        var read = reader.ReadFields(request.Address, MaxInspectedElements, cancellationToken).Select(ToDto).ToArray();
                        var str = graph.Types.Kinds[type].HasFlag(Core.Graph.TypeKind.String) ? reader.ReadString(request.Address, MaxInspectedText) : null;
                        return ((IReadOnlyList<ObjectFieldDto>)read, str);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                unavailable = CapabilityDescriptions.ExplainMissing(SnapshotFeatures.FieldValues, (SnapshotKindDto)(int)snapshot.Info.Kind);
            }

            return new ObjectDetails(
                request.Address,
                graph.Types.GetName(type),
                (long)graph.GetSize(index),
                (HeapKindDto)(int)graph.HeapKinds[index],
                graph.GetReferences(index).Length,
                graph.Roots.IsRetained(index),
                fields,
                unavailable,
                text);
        });

    public Task<ResultSetInfo> QueryReferencesAsync(ReferencesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            progress?.Report(new JobProgress("Naming references", 0, null));
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            if (!snapshot.Graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            snapshot.TryGetService<IObjectReader>(out var reader);
            var source = await Task.Run(() => ReferencesSource.Create(snapshot.Graph, reader, index, cancellationToken), cancellationToken).ConfigureAwait(false);

            // The source formats pages from the graph, so the set keeps a lease of its own.
            var owner = snapshot.AcquireLease();
            return resultSets.Add(source, owner);
        });

    public Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken)
    {
        if (offset < 0 || count < 0)
        {
            throw WorkerErrors.Create(HeapScopeErrorCode.InvalidArgument, "Offset and count must not be negative.");
        }

        // Pages of instances read string values from the dump (sprint 1.5), so paging gets the error contract (codes,
        // the worker log). It stays on the request's thread: a further thread pool hop cost latency under load.
        return GuardAsync(() => Task.FromResult(
            resultSets.TryGetPage(resultSetId, offset, count)
                ?? throw WorkerErrors.Create(
                    HeapScopeErrorCode.ResultSetNotFound,
                    $"Result set {resultSetId} is not available: it was released, evicted or never existed. Run the query again.")));
    }

    public Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken)
    {
        resultSets.Release(resultSetId);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Shutdown requested by the client");

        // Stop after the reply has been written.
        _ = Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None)
            .ContinueWith(_ => lifetime.StopApplication(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public Task CrashForTestingAsync(CancellationToken cancellationToken)
    {
        EnsureTestHooks();
        logger.LogWarning("Crash requested by a test hook");
        using var self = Process.GetCurrentProcess();
        self.Kill();
        return Task.CompletedTask;
    }

    public async Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        EnsureTestHooks();
        progress?.Report(new JobProgress("Hanging", 0, null, "Waiting until canceled"));
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    private Task<T> GuardAsync<T>(Func<Task<T>> body) => WorkerErrors.GuardAsync(body, logger);

    /// <summary>Array elements listed when an object is inspected (more are summarized in one row).</summary>
    private const int MaxInspectedElements = 100;

    /// <summary>Characters of a string returned with its object.</summary>
    private const int MaxInspectedText = 10_000;

    private static ObjectFieldDto ToDto(ObjectField field) =>
        new(field.Name, field.TypeName, (ObjectFieldKindDto)(int)field.Kind, field.Value, field.Address, [.. field.Children.Select(ToDto)]);

    /// <summary>A loaded snapshot and a lease on it; a snapshot closed meanwhile refuses the lease.</summary>
    private (ISnapshot Snapshot, SnapshotLease Lease) Lease(Guid snapshotId)
    {
        var snapshot = snapshots.TryGet(snapshotId) ?? throw SnapshotNotLoaded(snapshotId);
        try
        {
            return (snapshot, snapshot.AcquireLease());
        }
        catch (ObjectDisposedException)
        {
            throw SnapshotNotLoaded(snapshotId);
        }
    }

    private static StreamJsonRpc.LocalRpcException SnapshotNotLoaded(Guid snapshotId) => WorkerErrors.Create(
        HeapScopeErrorCode.SnapshotNotLoaded,
        $"Snapshot {snapshotId} is not loaded in the analysis worker: it was closed or the worker restarted. Open it again.");

    private static void Validate(SyntheticJobRequest request)
    {
        if (request.ObjectCount is < 0 or > MaxSyntheticObjects)
        {
            throw WorkerErrors.Create(HeapScopeErrorCode.InvalidArgument, $"Object count must be between 0 and {MaxSyntheticObjects:N0}.");
        }

        if (request.AverageReferences is < 0 or > 16)
        {
            throw WorkerErrors.Create(HeapScopeErrorCode.InvalidArgument, "Average references must be between 0 and 16.");
        }

        if (request.TypeCount is < 1 or > 1_000_000)
        {
            throw WorkerErrors.Create(HeapScopeErrorCode.InvalidArgument, "Type count must be between 1 and 1,000,000.");
        }

        // Projected peak against the worker's memory budget.
        var projected = (request.ObjectCount * PeakBytesPerObject) + (request.ObjectCount * request.AverageReferences * PeakBytesPerReference);
        // The same budget snapshots use (ARCHITECTURE.md 6.5): live physical memory, not the GC's last reading.
        var budget = (double)HeapScope.Storage.MemoryBudget.Current().BudgetBytes;
        if (projected > budget)
        {
            throw WorkerErrors.Create(
                HeapScopeErrorCode.InsufficientMemory,
                $"A synthetic graph of {request.ObjectCount:N0} objects needs about {projected / (1024 * 1024 * 1024):N1} GB, more than the worker's budget of {budget / (1024 * 1024 * 1024):N1} GB. Use fewer objects or references.");
        }
    }

    private void EnsureTestHooks()
    {
        if (!options.EnableTestHooks)
        {
            throw WorkerErrors.Create(HeapScopeErrorCode.TestHooksDisabled, "Test hooks are disabled; start the worker with --enable-test-hooks.");
        }
    }

    private SyntheticJobResult RunSynthetic(SyntheticJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            return BuildAndSummarize(request, progress, cancellationToken);
        }
        finally
        {
            // The graph and the builder's buffers are garbage now (completed, failed or canceled). An idle worker
            // allocates nothing, so without this collection gigabytes would stay committed (ARCHITECTURE.md 9).
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    private SyntheticJobResult BuildAndSummarize(SyntheticJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var throttled = new ThrottledProgress<JobProgress>(progress);
        var total = 2L * request.ObjectCount;
        var buildProgress = new CallbackProgress(done => throttled.Report(new JobProgress(
            done < request.ObjectCount ? "Adding objects" : "Adding references", done, total)));

        using var graph = SyntheticGraphs.Random(
            new RandomGraphOptions(request.ObjectCount, request.AverageReferences, request.TypeCount, Seed: (ulong)request.Seed),
            buildProgress,
            cancellationToken);

        throttled.Report(new JobProgress("Summarizing types", total, total));
        throttled.Flush();
        var summary = resultSets.Add(TypeSummarySource.Create(graph, cancellationToken));
        stopwatch.Stop();

        return new SyntheticJobResult(graph.ObjectCount, graph.EdgeCount, graph.TotalSize, graph.ColumnBytes, stopwatch.Elapsed.TotalSeconds, summary);
    }

    private sealed class CallbackProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
