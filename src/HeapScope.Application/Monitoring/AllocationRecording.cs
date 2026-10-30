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
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Monitoring;

/// <summary>
/// A monitoring run's allocation trace (sprint 3.4, split from the run in 3.8, peer review P11): recorded into the run's
/// session folder, watched so it stops at its size limit or when the disk runs low, and stopped after the run's
/// captures. Its watch is awaited before anything it uses is disposed. A refusal or failure is reported through
/// <c>problem</c> once and the run goes on.
/// </summary>
internal sealed class AllocationRecording : IAsyncDisposable
{
    private static readonly TimeSpan s_checkInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private readonly Guid _runId;
    private readonly int _processId;
    private readonly long _limitBytes;
    private readonly TimeProvider _time;
    private readonly Func<Task[]> _pendingCaptures;
    private readonly Action<string, Exception?> _problem;
    private Task<TraceResult>? _trace;
    private Task _watch = Task.CompletedTask;

    private AllocationRecording(Guid runId, int processId, long limitBytes, TimeProvider time, ILogger logger, Func<Task[]> pendingCaptures, Action<string, Exception?> problem)
    {
        _runId = runId;
        _processId = processId;
        _limitBytes = limitBytes;
        _time = time;
        _logger = logger;
        _pendingCaptures = pendingCaptures;
        _problem = problem;
    }

    /// <summary>The trace file being written, once started.</summary>
    public string? Path { get; private set; }

    /// <summary>
    /// Starts recording into <paramref name="folder"/>; a refusal (no folder, the recorder failing to start) is reported
    /// at once and the recording does nothing more.
    /// </summary>
    public static AllocationRecording Start(
        string? folder,
        Guid runId,
        int processId,
        long limitBytes,
        AllocationRecorder recorder,
        TimeProvider time,
        ILogger logger,
        Func<Task[]> pendingCaptures,
        Action<string, Exception?> problem)
    {
        var recording = new AllocationRecording(runId, processId, limitBytes, time, logger, pendingCaptures, problem);
        try
        {
            var target = folder ?? throw new IOException("The run has no session folder to record the allocation trace into.");
            recording.Path = System.IO.Path.Combine(target, string.Create(CultureInfo.InvariantCulture, $"allocations-{processId}-{time.GetLocalNow():yyyyMMdd-HHmmss}.nettrace"));
            logger.LogInformation("Monitoring run {RunId} (pid {Pid}): starting an allocation trace to {Path}, limit {Limit} bytes", runId, processId, recording.Path, limitBytes);
            recording._trace = recorder(processId, recording.Path, recording._stop.Token);
            _ = recording._trace.ContinueWith(recording.OnEnded, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            recording._watch = recording.WatchAsync(recording.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CaptureFailedException)
        {
            problem($"The allocation trace could not start: {ex.Message}", ex);
        }

        return recording;
    }

    /// <summary>
    /// Stops the trace (after the run's captures, which the caller awaited: stopping a runtime session while a gcdump's
    /// is open can induce another collection) and returns what it kept, or null with the failure reported.
    /// </summary>
    public async Task<TraceResult?> StopAsync()
    {
        if (_trace is not { } trace)
        {
            return null;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            var written = await trace.ConfigureAwait(false);
            _logger.LogInformation("Monitoring run {RunId} (pid {Pid}): allocation trace {Path}, {Bytes} bytes over {Seconds:N0} s", _runId, _processId, written.Path, written.Bytes, written.Duration.TotalSeconds);
            return written;
        }
        catch (Exception ex)
        {
            // A boundary: whatever the recorder threw, the run still closes its timeline and records its end.
            _problem($"The allocation trace failed: {ex.Message}", ex);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _watch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    /// <summary>A trace that ends before the run (refused, failed) is reported at once.</summary>
    private void OnEnded(Task<TraceResult> trace)
    {
        if (trace.IsFaulted && !_stop.IsCancellationRequested)
        {
            var error = trace.Exception!.InnerException ?? trace.Exception;
            _problem($"The allocation trace stopped: {error.Message} The run goes on without it.", error);
        }
    }

    /// <summary>Stops the trace at its size limit or when the disk has less than <see cref="MonitorRun.TraceMinimumFreeBytes"/> free.</summary>
    private async Task WatchAsync(string path)
    {
        try
        {
            while (!_stop.IsCancellationRequested && _trace is { IsCompleted: false })
            {
                await Task.Delay(s_checkInterval, _time, _stop.Token).ConfigureAwait(false);
                var file = new FileInfo(path + ".partial") is { Exists: true } partial ? partial : new FileInfo(path);
                var size = file.Exists ? file.Length : 0;
                var free = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path))!).AvailableFreeSpace;
                string? why = size >= _limitBytes
                    ? string.Create(CultureInfo.InvariantCulture, $"it reached its limit of {_limitBytes / (1024 * 1024)} MB")
                    : free < MonitorRun.TraceMinimumFreeBytes ? string.Create(CultureInfo.InvariantCulture, $"the disk has {free / (1024 * 1024)} MB free") : null;
                if (why is not null)
                {
                    _problem($"The allocation trace stopped early because {why}; what it recorded is kept and the run goes on.", null);

                    // Not while a capture of the process runs: stopping a runtime session while a gcdump's is open can
                    // induce another collection.
                    await Task.WhenAll(_pendingCaptures()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await _stop.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Monitoring run {RunId}: the allocation trace's size could not be checked", _runId);
        }
    }
}
