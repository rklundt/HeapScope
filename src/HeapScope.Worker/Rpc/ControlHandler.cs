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

using System.Diagnostics;
using System.Runtime;
using HeapScope.Contracts;
using HeapScope.Core.Synthetic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>The worker's own calls (<see cref="IWorkerControl"/>): the handshake, the synthetic job and shutdown.</summary>
internal sealed class ControlHandler(
    WorkerOptions options,
    ResultSetStore resultSets,
    IHostApplicationLifetime lifetime,
    ILogger<ControlHandler> logger) : RpcHandler(logger), IWorkerControl
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
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            Validate(request);

            // One synthetic job at a time: two large builds must not stack their peaks.
            await _syntheticGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Logger.LogInformation("Synthetic job started: {Objects} objects, {References} references per object", request.ObjectCount, request.AverageReferences);
                var result = await Task.Run(() => RunSynthetic(request, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
                Logger.LogInformation("Synthetic job finished in {Seconds:N2} s", result.ElapsedSeconds);
                return result;
            }
            finally
            {
                _syntheticGate.Release();
            }
        });

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Shutdown requested by the client");

        // Stop after the reply has been written.
        _ = Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None)
            .ContinueWith(_ => lifetime.StopApplication(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return Task.CompletedTask;
    }

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
}
