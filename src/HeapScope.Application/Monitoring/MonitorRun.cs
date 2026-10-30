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
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HeapScope.Core;
using HeapScope.Application.Events;

namespace HeapScope.Application.Monitoring;

/// <summary>
/// One monitoring run. <see cref="SampleReceived"/> and <see cref="Captured"/> are raised on a background thread; front
/// ends marshal them. Timeline rows go to <see cref="TimelinePath"/> (one per sample, plus one per capture or event).
/// </summary>
public sealed class MonitorRun : IAsyncDisposable
{
    /// <summary>The timeline event of a forced collection that finished.</summary>
    public const string GcForcedEvent = "gc forced";

    /// <summary>The prefix of a capture's timeline event.</summary>
    public const string SnapshotEventPrefix = "snapshot: ";

    /// <summary>The label of a snapshot taken with Get snapshot.</summary>
    public const string ManualLabel = "manual";

    /// <summary>The run stops recording allocations when the disk has less free space than this (review H7 of 3.4).</summary>
    public const long TraceMinimumFreeBytes = 1L << 30;

    private readonly ICaptureService _capture;
    private readonly ILogger _logger;
    private readonly SnapshotRuleEngine _rules;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<MonitorCapture> _captures = [];
    private readonly HashSet<Task> _pending = [];
    private readonly AllocationRecorder _allocations;
    private readonly TimeProvider _time;
    private readonly List<DateTimeOffset> _forcedGcs = [];
    private readonly DateTimeOffset _started;
    private bool _maximumLogged;
    private bool _deferLogged;
    private bool _closing;
    private TimelineWriter _timeline;
    private RunRecord? _record;
    private Task? _capturing;
    private AllocationRecording? _recording;

    internal MonitorRun(int processId, MonitorOptions options, ICaptureService capture, ILogger logger, AllocationRecorder allocations, TimeProvider time)
    {
        ProcessId = processId;
        Options = options;
        _capture = capture;
        _logger = logger;
        _allocations = allocations;
        _time = time;
        _started = time.GetUtcNow();
        _rules = new SnapshotRuleEngine(options.Rules);
        _timeline = TimelineWriter.None(logger);
    }

    public event EventHandler<MonitorSample>? SampleReceived;

    public event EventHandler<MonitorCapture>? Captured;

    /// <summary>Raised when a forced collection ends (sprint 3.4), on a background thread.</summary>
    public event EventHandler<MonitorForcedGc>? GcForced;

    /// <summary>
    /// Raised when the allocation trace stops before the run (refused, failed, or stopped at its size or disk limit), with
    /// the reason in words, on a background thread (review of 3.4: shown at once, not at the end of the run).
    /// </summary>
    public event EventHandler<string>? TraceStopped;

    /// <summary>The run's id in its session's record (sprint 3.4).</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The session folder the run records into, when it has one.</summary>
    public string? SessionFolder => _record?.Folder;

    /// <summary>The allocation trace, once the run ended with one (sprint 3.4).</summary>
    public string? TracePath { get; private set; }

    /// <summary>Why the allocation trace was not recorded or stopped early, when asked for.</summary>
    public string? TraceError { get; private set; }

    public int ProcessId { get; }

    public MonitorOptions Options { get; }

    /// <summary>The timeline file, from the run's start (null when not written, or when writing it failed).</summary>
    public string? TimelinePath
    {
        get
        {
            lock (_gate)
            {
                return _timeline.Path;
            }
        }
    }

    /// <summary>
    /// Ends when the run stops: canceled, or the process exited. Faults with <see cref="CaptureFailedException"/> when the
    /// process cannot be monitored (gone, not .NET, elevated).
    /// </summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<MonitorCapture> Captures
    {
        get
        {
            lock (_gate)
            {
                return [.. _captures];
            }
        }
    }

    /// <summary>The rule capture in progress, or a completed task (tests and smoke runs await it; it never faults).</summary>
    public Task CaptureTask
    {
        get
        {
            lock (_gate)
            {
                return _capturing ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Takes a snapshot now (sprint 3.4, the Live page's Get snapshot): a manual capture of the run's process with the run's
    /// kind, recorded as the run's card and timeline marker. Refused (a failed card) while a capture of it runs, and once
    /// the run is stopping.
    /// </summary>
    public Task<MonitorCapture> CaptureNowAsync()
    {
        lock (_gate)
        {
            if (Stopping)
            {
                return Task.FromResult(new MonitorCapture(_time.GetUtcNow(), ManualLabel, null, "The run has stopped."));
            }

            return Track(Task.Run(() => CaptureAsync(null, ManualLabel, _time.GetUtcNow(), null)));
        }
    }

    /// <summary>
    /// Forces a full collection in the run's process (sprint 3.4), marked on the timeline and in the run's record when it
    /// finished. Refused once the run is stopping.
    /// </summary>
    public Task<MonitorForcedGc> ForceGcAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Stopping)
            {
                return Task.FromResult(new MonitorForcedGc(_time.GetUtcNow(), null, "The run has stopped."));
            }

            return Track(Task.Run(() => ForceGcCoreAsync(cancellationToken), CancellationToken.None));
        }
    }

    /// <summary>Stops sampling; a capture already running finishes and is recorded.</summary>
    public void Stop() => _stop.Cancel();

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CaptureFailedException or OperationCanceledException)
        {
            // Disposing ends the run; how it ended was reported through Completion.
        }

        _stop.Dispose();
        if (_recording is not null)
        {
            await _recording.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal void Begin(ICounterSource counters)
    {
        // The run's session is fixed now, and cannot be deleted while the run writes into it.
        _record = RunRecord.Open(_capture.Library, Options.RecordRun, _logger, Id);

        if (Options.WriteTimeline)
        {
            _timeline = TimelineWriter.Create(_record.Folder, ProcessId, _time.GetLocalNow(), _logger, Id);
        }

        if (Options.RecordAllocations)
        {
            _recording = AllocationRecording.Start(_record.Folder, Id, ProcessId, Options.TraceLimitBytes, _allocations, _time, _logger, PendingCaptures, TraceProblem);
        }

        Record(ended: null);
        Completion = RunAsync(counters);
    }

    /// <summary>Whether the run is stopping or stopped: nothing new starts then (review H6 of 3.4).</summary>
    private bool Stopping => _closing || _stop.IsCancellationRequested;

    /// <summary>Tracks a capture or forced collection, so the run's end waits for it (call under the gate).</summary>
    private Task<T> Track<T>(Task<T> task)
    {
        _pending.Add(task);
        task.ContinueWith(
            done =>
            {
                lock (_gate)
                {
                    _pending.Remove(done);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private async Task<MonitorForcedGc> ForceGcCoreAsync(CancellationToken cancellationToken)
    {
        var at = _time.GetUtcNow();
        MonitorForcedGc result;
        try
        {
            result = new MonitorForcedGc(at, await _capture.ForceGcAsync(ProcessId, cancellationToken).ConfigureAwait(false), null);
        }
        catch (CaptureFailedException ex)
        {
            result = new MonitorForcedGc(at, null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            result = new MonitorForcedGc(at, null, "Canceled.");
        }

        lock (_gate)
        {
            // Only a collection whose end was seen is marked as forced (review of 3.4): the marker backs the advice that
            // objects compared across it may have moved.
            if (result.Result is { Completed: true })
            {
                _forcedGcs.Add(at);
            }

            _timeline.WriteEvent(at, result.Result is { Completed: true } ? GcForcedEvent
                : result.Result is { } incomplete ? "gc not confirmed: " + incomplete.Outcome
                : "gc not forced: " + result.Error);
        }

        _logger.LogInformation("Monitoring run {RunId}: Force GC {Outcome}", Id, result.Result?.Outcome ?? "failed: " + result.Error);
        Raise(GcForced, result, nameof(GcForced));
        return result;
    }

    /// <summary>The captures and forced collections still running.</summary>
    private Task[] PendingCaptures()
    {
        lock (_gate)
        {
            return [.. _pending];
        }
    }

    /// <summary>
    /// Reports a problem with the allocation trace. Every problem is logged; the first is what the run shows and records,
    /// unless a later one says the trace was not kept after all (3.8 review: a stop that failed after an early stop).
    /// </summary>
    private void TraceProblem(string message, Exception? ex)
    {
        _logger.LogWarning(ex, "Monitoring run {RunId} (pid {Pid}): {Message}", Id, ProcessId, message);
        bool first;
        lock (_gate)
        {
            first = TraceError is null;
            if (first || ex is not null)
            {
                TraceError = first ? message : TraceError + " " + message;
            }
        }

        if (first)
        {
            Raise(TraceStopped, message, nameof(TraceStopped));
        }
    }

    /// <summary>The run's record in its session: at the start, and with its end, files and snapshots.</summary>
    private void Record(DateTimeOffset? ended)
    {
        if (_record is not { Folder: not null } record)
        {
            return;
        }

        List<Guid> snapshots;
        List<string> described;
        List<DateTimeOffset> forced;
        lock (_gate)
        {
            snapshots = [.. _captures.Select(c => c.Result?.SnapshotId).OfType<Guid>()];
            described = [.. _captures.Where(c => c.Result?.SnapshotId is not null).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Result!.SnapshotId} {Path.GetFileName(c.Result.Path)}"))];
            forced = [.. _forcedGcs];
        }

        // At the start the files it will write; at the end the trace only when it was kept.
        var trace = ended is null ? _recording?.Path : TracePath;
        record.Write(
            new SessionRun(
                Id,
                Options.ProcessName,
                ProcessId,
                Options.Interval.TotalSeconds,
                Options.Rules.Rules.Count == 0 ? "none" : string.Join("; ", Options.Rules.Rules.Select(r => r.Label)),
                Options.CaptureKind,
                _started,
                ended,
                record.Relative(TimelinePath),
                record.Relative(trace),
                snapshots,
                forced,
                TraceError),
            described);
    }

    private async Task RunAsync(ICounterSource counters)
    {
        try
        {
            await counters.RunAsync(ProcessId, Options.Interval, OnSample, _stop.Token).ConfigureAwait(false);
        }
        catch (ElevationRequiredException ex)
        {
            throw new CaptureFailedException(CaptureFailure.NeedsAdministrator, ex.Message, ex);
        }
        catch (TargetUnavailableException ex)
        {
            throw new CaptureFailedException(CaptureFailure.TargetExited, ex.Message, ex);
        }
        finally
        {
            // Teardown never throws: a capture's own failure is recorded by CaptureAsync, and the timeline is closed
            // whatever happened (review H3 of 1.7). Nothing new starts from here on, and every capture and forced
            // collection still running is awaited (review H6 of 3.4).
            Task[] pending;
            lock (_gate)
            {
                _closing = true;
                pending = [.. _pending];
            }

            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var taken = Captures.Count(c => c.Result is not null);
            _logger.LogInformation("Monitoring run {RunId} (pid {Pid}) ended: {Captures} snapshots taken, {Rules} by rules", Id, ProcessId, taken, _rules.Count);

            // The allocation trace stops after the captures: stopping a runtime session while a gcdump's is open can
            // induce another collection (review of 3.4). Keep what it wrote.
            if (_recording is not null && await _recording.StopAsync().ConfigureAwait(false) is { } written)
            {
                TracePath = written.Path;
            }

            StreamWriter? timeline;
            lock (_gate)
            {
                timeline = _timeline.Detach();
            }

            await _timeline.CloseAsync(timeline).ConfigureAwait(false);
            Record(_time.GetUtcNow());
            _record?.Dispose();
        }
    }

    private void OnSample(HeapSample sample)
    {
        SnapshotRule? fired;
        MonitorSample seen;
        lock (_gate)
        {
            // After Stop no rule fires: the user asked for no more dumps (review of 1.7).
            var stopped = Stopping;

            // A capture or forced collection of this process from anywhere (the Processes page, Get snapshot, Force GC)
            // counts as running (decision 3).
            var own = _pending.Any(t => !t.IsCompleted);
            var running = own || _capture.IsBusy(ProcessId);
            fired = stopped ? null : _rules.Evaluate(sample, captureRunning: running);
            seen = new MonitorSample(sample, _rules.HighGcPressure, _rules.Baseline);
            _timeline.WriteSample(sample);
            if (fired is not null)
            {
                // Started outside the lock: a capture logs and adds a job, which raises events.
                var decision = _rules.LastDecision;
                _capturing = Track(Task.Run(() => CaptureAsync(fired, fired.Label, sample.At, decision)));
            }
            else if (_rules.Deferred is { } deferred)
            {
                // Logged once per busy stretch (review H5 of 3.4): the rule fires at the first sample after it.
                if (!_deferLogged)
                {
                    _deferLogged = true;
                    _logger.LogInformation(
                        "Monitoring run {RunId} (pid {Pid}): {Rule} is due (metric {Metric} bytes) but {Holder} of the process is running; it fires at the next sample after that ends",
                        Id,
                        ProcessId,
                        deferred.Label,
                        _rules.LastMetric,
                        own ? "the run's own capture or forced collection" : "a capture or forced collection from elsewhere");
                }
            }
            else if (_rules.AtMaximum && !_maximumLogged)
            {
                _maximumLogged = true;
                _logger.LogInformation("Monitoring pid {Pid}: the maximum of {Max} snapshots is reached; rules no longer capture", ProcessId, _rules.Count);
            }

            if (!running)
            {
                _deferLogged = false;
            }
        }

        Raise(SampleReceived, seen, nameof(SampleReceived));
    }

    private async Task<MonitorCapture> CaptureAsync(SnapshotRule? rule, string label, DateTimeOffset at, RuleDecision? decision)
    {
        MonitorCapture result;
        var busy = false;
        try
        {
            _logger.LogInformation("Monitoring run {RunId} (pid {Pid}): {Rule} {How} at {At:O}, capturing: {Decision}", Id, ProcessId, label, rule is null ? "asked for" : "fired", at, decision?.ToString() ?? "no decision recorded");
            var captured = await _capture.StartCapture(ProcessId, label: label, source: rule is null ? SnapshotSource.Manual : SnapshotSource.Timed, kind: Options.CaptureKind).Result.ConfigureAwait(false);
            result = new MonitorCapture(at, label, captured, null);
            _logger.LogInformation("Monitoring run {RunId}: the {Rule} capture is snapshot {SnapshotId}", Id, label, captured.SnapshotId);
        }
        catch (CaptureFailedException ex)
        {
            busy = ex.Reason == CaptureFailure.Busy;
            _logger.LogWarning(ex, "Monitoring run {RunId} (pid {Pid}): the {Rule} capture failed", Id, ProcessId, label);
            result = new MonitorCapture(at, label, null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            // Canceled from the jobs panel, or when the app closes: recorded like a failure, the run goes on.
            _logger.LogInformation("Monitoring run {RunId} (pid {Pid}): the {Rule} capture was canceled", Id, ProcessId, label);
            result = new MonitorCapture(at, label, null, "Canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitoring run {RunId} (pid {Pid}): the {Rule} capture failed unexpectedly", Id, ProcessId, label);
            result = new MonitorCapture(at, label, null, "The capture failed. Details are in the log.");
        }

        lock (_gate)
        {
            // A manual snapshot does not count toward the rules' maximum.
            if (result.Result is not null && rule is not null)
            {
                _rules.Taken();
            }

            // Another capture of the process won the race between the busy check and the gate: the rule is not used up
            // (review H5 of 3.4).
            if (busy && rule is not null)
            {
                _rules.Missed(rule);
                _logger.LogInformation("Monitoring run {RunId}: {Rule} is re-armed; another capture of the process started first", Id, label);
            }

            _captures.Add(result);
            _timeline.WriteCapture(result);
        }

        Raise(Captured, result, nameof(Captured));
        return result;
    }

    /// <summary>Raises an event; a listener that throws is logged and never ends the run.</summary>
    private void Raise<T>(EventHandler<T>? handler, T value, string eventName) => SafeEvents.Raise(handler, this, value, _logger, "monitoring run " + eventName);

}
