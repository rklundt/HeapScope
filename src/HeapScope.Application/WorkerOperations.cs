using System.Diagnostics;
using System.Globalization;
using HeapScope.Application.Jobs;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.Application;

/// <summary>
/// Use cases that UI and CLI share (ADR-008): every worker operation goes through here, so both front ends behave
/// the same.
/// </summary>
public sealed class WorkerOperations(IWorkerClient worker, JobTracker jobs)
{
    public IWorkerClient Worker { get; } = worker ?? throw new ArgumentNullException(nameof(worker));

    public JobTracker Jobs { get; } = jobs ?? throw new ArgumentNullException(nameof(jobs));

    /// <summary>Handshakes with the worker (starting it if needed) and returns its current information.</summary>
    public Task<WorkerInfo> GetInfoAsync(CancellationToken cancellationToken) =>
        Worker.InvokeAsync((service, ct) => service.GetInfoAsync(WorkerProtocol.Version, ct), cancellationToken);

    /// <summary>Measures a round trip to the worker, starting it if needed.</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        await Worker.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        return stopwatch.Elapsed;
    }

    /// <summary>Starts the synthetic diagnostic job as a tracked job.</summary>
    public TrackedJob<SyntheticJobResult> StartSyntheticJob(SyntheticJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = string.Create(CultureInfo.InvariantCulture, $"Synthetic graph, {request.ObjectCount:N0} objects");
        return Jobs.Start(title, (progress, ct) =>
            Worker.InvokeAsync((service, innerCt) => service.RunSyntheticJobAsync(request, progress, innerCt), ct));
    }

    /// <summary>Fetches one page of a result set.</summary>
    public Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken) =>
        Worker.InvokeAsync((service, ct) => service.GetRowsAsync(resultSetId, offset, count, ct), cancellationToken);

    /// <summary>Frees a result set in the worker.</summary>
    public Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken) =>
        Worker.InvokeAsync((service, ct) => service.ReleaseResultSetAsync(resultSetId, ct), cancellationToken);
}
