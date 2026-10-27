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
using HeapScope.Application.Sessions;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Application.Allocations;

/// <summary>An allocation trace open in this session (sprint 2.5).</summary>
public sealed class TraceHandle
{
    internal TraceHandle(TraceDescriptor descriptor, int generation)
    {
        Path = descriptor.Path;
        Update(descriptor, generation);
    }

    /// <summary>Stable id of this handle in the session.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public string Path { get; }

    /// <summary>What the worker reported when the trace was last read.</summary>
    public TraceDescriptor Descriptor { get; private set; } = null!;

    public bool IsClosed { get; internal set; }

    internal int Generation { get; private set; }

    internal Guid WorkerTraceId => Descriptor.TraceId;

    internal void Update(TraceDescriptor descriptor, int generation)
    {
        Descriptor = descriptor;
        Generation = generation;
    }
}

/// <summary>The types of an interval and the totals of the first run.</summary>
public sealed record AllocationTypesAnswer(ResultSetHandle Rows, AllocationTypesResult Result);

/// <summary>A call tree or back traces and the answer of the first run.</summary>
public sealed record AllocationTreeAnswer(ResultSetHandle Rows, AllocationTreeResult Result);

/// <summary>An interval of a trace and, optionally, one type: what the allocation queries are about.</summary>
/// <param name="ToSeconds">The end, or null for the end of the trace.</param>
public sealed record AllocationScope(double FromSeconds = 0, double? ToSeconds = null, string? TypeName = null)
{
    public static AllocationScope All { get; } = new();
}

/// <summary>
/// The allocation traces open in this session (sprint 2.5, ADR-028): reading them in the worker, recording new ones into
/// the session folder, and the queries over an interval. A trace is read again after a worker restart (seconds, from the
/// trace file; nothing is cached).
/// </summary>
public sealed class AllocationSession(IWorkerClient worker, JobTracker jobs, TraceService recorder, SessionLibrary library, TimeProvider? timeProvider = null, ILogger<AllocationSession>? logger = null)
{
    /// <summary>How long Record allocations records unless stopped earlier.</summary>
    public static readonly TimeSpan DefaultRecording = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly List<TraceHandle> _traces = [];

    /// <summary>Raised on any thread when a trace is opened or closed.</summary>
    public event EventHandler? TracesChanged;

    public IReadOnlyList<TraceHandle> Traces
    {
        get
        {
            lock (_gate)
            {
                return [.. _traces];
            }
        }
    }

    /// <summary>Reads a trace file as a tracked job.</summary>
    public TrackedJob<TraceHandle> StartOpen(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        return jobs.Start(string.Create(CultureInfo.InvariantCulture, $"Open {System.IO.Path.GetFileName(fullPath)}"), (progress, ct) => OpenAsync(fullPath, progress, ct));
    }

    /// <summary>
    /// Records sampled allocations of a process into the current session's folder, then reads the trace, as one tracked
    /// job. <paramref name="stop"/> ends the recording early and keeps it; canceling the job abandons it.
    /// </summary>
    public TrackedJob<TraceHandle> StartRecord(int processId, string processName, TimeSpan duration, CancellationToken stop)
    {
        ArgumentException.ThrowIfNullOrEmpty(processName);
        var title = string.Create(CultureInfo.InvariantCulture, $"Record allocations of {processName} ({processId})");
        return jobs.Start(title, async (progress, ct) =>
        {
            var folder = library.Current.Folder;
            var path = UniquePath(folder, processName, processId);
            progress.Report(new JobProgress("Recording allocations", 0, null, string.Create(CultureInfo.InvariantCulture, $"Up to {duration.TotalSeconds:N0} s; Stop keeps what was recorded")));
            _logger.LogInformation("Recording allocations of {Name} (pid {Pid}) for up to {Seconds} s to {Path}", processName, processId, duration.TotalSeconds, path);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
            var recorded = await recorder.RecordAsync(processId, path, TraceKind.Allocations, duration, either.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Recorded {Bytes} bytes of allocations in {Seconds:N1} s{Early}", recorded.Bytes, recorded.Duration.TotalSeconds, recorded.StoppedEarly ? " (stopped early)" : string.Empty);
            return await OpenAsync(recorded.Path, progress, ct).ConfigureAwait(false);
        });
    }

    /// <summary>Closes a trace in the worker (when it is still there) and forgets the handle.</summary>
    public async Task CloseAsync(TraceHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (!_traces.Remove(handle))
            {
                return;
            }

            handle.IsClosed = true;
        }

        _logger.LogInformation("Allocation trace {Path} (worker trace {WorkerTraceId}) closed", handle.Path, handle.WorkerTraceId);
        TracesChanged?.Invoke(this, EventArgs.Empty);
        if (handle.Generation == worker.Generation && worker.State == WorkerState.Running)
        {
            await worker.InvokeAsync((service, ct) => service.CloseTraceAsync(handle.WorkerTraceId, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<AllocationTimelineResult> QueryTimelineAsync(TraceHandle handle, double bucketSeconds, CancellationToken cancellationToken) =>
        UseAsync(handle, (service, id, ct) => service.QueryAllocationTimelineAsync(new AllocationTimelineRequest(id, bucketSeconds), ct), cancellationToken);

    public async Task<AllocationTypesAnswer> QueryTypesAsync(TraceHandle handle, AllocationScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        AllocationTypesResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(handle, (service, id, innerCt) => service.QueryAllocationTypesAsync(new AllocationTypesRequest(id, scope.FromSeconds, scope.ToSeconds), innerCt), ct).ConfigureAwait(false);
            return latest.Types;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);
        return new AllocationTypesAnswer(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    public Task<AllocationStacksResult> QueryStacksAsync(TraceHandle handle, AllocationScope scope, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return UseAsync(handle, (service, id, ct) => service.QueryAllocationStacksAsync(new AllocationStacksRequest(id, scope.FromSeconds, scope.ToSeconds, scope.TypeName, count), ct), cancellationToken);
    }

    public async Task<AllocationTreeAnswer> QueryTreeAsync(
        TraceHandle handle,
        AllocationScope scope,
        AllocationTreeKindDto kind,
        IReadOnlyCollection<string> expanded,
        string? revealKey,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(expanded);
        var keys = expanded.ToArray();
        AllocationTreeResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await UseAsync(
                handle,
                (service, id, innerCt) => service.QueryAllocationTreeAsync(new AllocationTreeRequest(id, kind, keys, scope.FromSeconds, scope.ToSeconds, scope.TypeName, RevealKey: revealKey), progress, innerCt),
                ct).ConfigureAwait(false);
            return latest.Rows;
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);
        return new AllocationTreeAnswer(new ResultSetHandle(worker, info, RunAsync, _logger), latest!);
    }

    /// <summary>
    /// Runs <paramref name="call"/> with the trace's current worker id, reading the trace again first when the worker
    /// restarted since, and once more when the worker reports it as not open.
    /// </summary>
    public async Task<T> UseAsync<T>(TraceHandle handle, Func<IWorkerService, Guid, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
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
            return await worker.InvokeAsync((service, ct) => call(service, handle.WorkerTraceId, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerOperationException ex) when (ex.ErrorCode == HeapScopeErrorCode.TraceNotLoaded)
        {
            ThrowIfClosed(handle);
            await ReopenAsync(handle, cancellationToken).ConfigureAwait(false);
            return await worker.InvokeAsync((service, ct) => call(service, handle.WorkerTraceId, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TraceHandle> OpenAsync(string fullPath, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await worker.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var generation = worker.Generation;
        var descriptor = await worker.InvokeAsync((service, ct) => service.OpenTraceAsync(new OpenTraceRequest(fullPath), progress, ct), cancellationToken).ConfigureAwait(false);
        var handle = new TraceHandle(descriptor, generation);
        lock (_gate)
        {
            _traces.Add(handle);
        }

        _logger.LogInformation(
            "Allocation trace {Path} opened as worker trace {WorkerTraceId} (worker generation {Generation}): {Samples} samples, {Bytes} sampled bytes, {Seconds:N1} s recorded",
            descriptor.Path,
            descriptor.TraceId,
            generation,
            descriptor.Samples,
            descriptor.SampledBytes,
            descriptor.DurationSeconds);
        TracesChanged?.Invoke(this, EventArgs.Empty);
        return handle;
    }

    private async Task ReopenAsync(TraceHandle handle, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reading {Path} again after a worker restart (was worker trace {WorkerTraceId}, generation {Generation})", handle.Path, handle.WorkerTraceId, handle.Generation);
        var generation = worker.Generation;
        var descriptor = await worker.InvokeAsync((service, ct) => service.OpenTraceAsync(new OpenTraceRequest(handle.Path), null, ct), cancellationToken).ConfigureAwait(false);
        handle.Update(descriptor, generation);
    }

    private static void ThrowIfClosed(TraceHandle handle)
    {
        if (handle.IsClosed)
        {
            throw new InvalidOperationException("The allocation trace was closed.");
        }
    }

    private string UniquePath(string folder, string processName, int processId)
    {
        var stamp = _time.GetUtcNow().ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = string.Create(CultureInfo.InvariantCulture, $"{processName}-{processId}-{stamp}-allocations");
        var path = System.IO.Path.Combine(folder, baseName + ".nettrace");
        for (var n = 2; File.Exists(path) || File.Exists(path + ".partial"); n++)
        {
            path = System.IO.Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{baseName}-{n}.nettrace"));
        }

        return path;
    }
}
