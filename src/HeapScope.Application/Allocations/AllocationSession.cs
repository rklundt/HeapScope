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
        Descriptor = descriptor;
        Generation = generation;
    }

    /// <summary>Stable id of this handle in the session.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public string Path { get; }

    /// <summary>What the worker reported when the trace was last read.</summary>
    public TraceDescriptor Descriptor { get; private set; }

    public bool IsClosed { get; internal set; }

    internal int Generation { get; private set; }

    internal Guid WorkerTraceId => Descriptor.TraceId;

    /// <summary>Set when a re-read found the file changed: later queries refuse at once instead of reading it again.</summary>
    internal bool FileChanged { get; set; }

    /// <summary>One re-read at a time: queries that find the trace gone wait for the same read (the 2.5 review).</summary>
    internal SemaphoreSlim Reopening { get; } = new(1, 1);

    internal void Update(TraceDescriptor descriptor, int generation)
    {
        Descriptor = descriptor;
        Generation = generation;
    }
}

/// <summary>
/// A recording was written but could not be read (sprint 2.5 review): the trace is kept at <see cref="Path"/> and can be
/// opened later from the Allocations page.
/// </summary>
public sealed class TraceRecordedButNotReadException(string path, Exception innerException)
    : Exception($"Recorded {path}, but it could not be read: {innerException?.Message} Open it later from Allocations, Open trace.", innerException)
{
    public string Path { get; } = path;
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
    /// job. <paramref name="stop"/> ends the recording early and keeps it; canceling the job deletes what was recorded.
    /// <paramref name="recorded"/> runs when recording ends and reading starts. A trace that was written but cannot be read
    /// fails with <see cref="TraceRecordedButNotReadException"/>, which says where it is.
    /// </summary>
    public TrackedJob<TraceHandle> StartRecord(int processId, string processName, TimeSpan duration, CancellationToken stop, Action? recorded = null)
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
            var trace = await recorder.RecordAsync(processId, path, TraceKind.Allocations, duration, either.Token).ConfigureAwait(false);
            var why = ct.IsCancellationRequested ? "the job was canceled" : stop.IsCancellationRequested ? "Stop was pressed" : trace.StoppedEarly ? "the process exited or stopped answering" : "the duration passed";
            _logger.LogInformation(
                "Recorded {Path}: {Bytes} bytes of allocations in {Seconds:N1} s, ended because {Why}",
                trace.Path,
                trace.Bytes,
                trace.Duration.TotalSeconds,
                why);
            if (ct.IsCancellationRequested)
            {
                // A canceled recording is not kept: it would hold method and type names nobody asked to keep.
                TryDelete(trace.Path);
                _logger.LogInformation("Deleted the canceled recording {Path}", trace.Path);
                ct.ThrowIfCancellationRequested();
            }

            recorded?.Invoke();
            try
            {
                return await OpenAsync(trace.Path, progress, ct).ConfigureAwait(false);
            }
            catch (WorkerException ex)
            {
                _logger.LogInformation("Recorded {Path} could not be read: {Message}", trace.Path, ex.Message);
                throw new TraceRecordedButNotReadException(trace.Path, ex);
            }
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
        // RunAsync has just set latest: the first run returned without throwing.
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
        // RunAsync has just set latest: the first run returned without throwing.
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
            await ReopenAsync(handle, handle.WorkerTraceId, cancellationToken).ConfigureAwait(false);
        }

        var asked = handle.WorkerTraceId;
        try
        {
            return await worker.InvokeAsync((service, ct) => call(service, asked, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerOperationException ex) when (ex.ErrorCode == HeapScopeErrorCode.TraceNotLoaded)
        {
            ThrowIfClosed(handle);
            await ReopenAsync(handle, asked, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Reads the trace again in the worker, once for all the queries that found <paramref name="missing"/> gone: the
    /// others wait and use the new read. The file must be the one first read (size and write time); a changed file is
    /// refused with <see cref="HeapScopeErrorCode.TraceChanged"/>, since its rows and open keys would no longer match.
    /// </summary>
    private async Task ReopenAsync(TraceHandle handle, Guid missing, CancellationToken cancellationToken)
    {
        await handle.Reopening.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle.FileChanged)
            {
                throw Changed(handle);
            }

            if (handle.WorkerTraceId != missing && handle.Generation == worker.Generation)
            {
                return;
            }

            var previous = handle.Descriptor;
            _logger.LogInformation(
                "Reading {Path} again (worker trace {WorkerTraceId} of generation {Generation} is gone; the worker is now generation {Now})",
                handle.Path,
                previous.TraceId,
                handle.Generation,
                worker.Generation);
            var generation = worker.Generation;
            var descriptor = await worker.InvokeAsync((service, ct) => service.OpenTraceAsync(new OpenTraceRequest(handle.Path), null, ct), cancellationToken).ConfigureAwait(false);
            if (!descriptor.SameFileAs(previous) || handle.IsClosed)
            {
                await worker.InvokeAsync((service, ct) => service.CloseTraceAsync(descriptor.TraceId, ct), CancellationToken.None).ConfigureAwait(false);
                ThrowIfClosed(handle);
                _logger.LogInformation(
                    "Not using {Path}: it changed since it was opened ({OldBytes} bytes written {OldWritten:O}, now {NewBytes} bytes written {NewWritten:O})",
                    handle.Path,
                    previous.FileBytes,
                    previous.FileWriteUtc,
                    descriptor.FileBytes,
                    descriptor.FileWriteUtc);
                handle.FileChanged = true;
                throw Changed(handle);
            }

            handle.Update(descriptor, generation);
            _logger.LogInformation("Read {Path} again as worker trace {WorkerTraceId} (generation {Generation}): {Samples} samples, the same file", handle.Path, descriptor.TraceId, generation, descriptor.Samples);
        }
        finally
        {
            handle.Reopening.Release();
        }
    }

    private static WorkerOperationException Changed(TraceHandle handle) => new(
        HeapScopeErrorCode.TraceChanged,
        $"{System.IO.Path.GetFileName(handle.Path)} changed since it was opened, so its rows would no longer match. Close this trace and open it again.",
        null);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The session sweep removes it later.
        }
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
