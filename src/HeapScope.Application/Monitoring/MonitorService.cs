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

namespace HeapScope.Application.Monitoring;

/// <summary>
/// One interval of a process's GC heap (sprint 1.7), in bytes. <see cref="GcHeapBytes"/> is the heap now, garbage
/// allocated since the last GC included; the generation sizes are as of the last GC, so <see cref="HeapAfterGcBytes"/>
/// is what survived it (the rule metric, review of 1.7). Front ends see this, not Capture's counter type.
/// </summary>
public sealed record HeapSample(
    DateTimeOffset At,
    long GcHeapBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LargeObjectHeapBytes,
    long PinnedObjectHeapBytes,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections,
    long AllocatedBytes,
    double TimeInGcPercent,
    long WorkingSetBytes)
{
    /// <summary>The heap as of the last GC: every generation, LOH and POH.</summary>
    public long HeapAfterGcBytes => Gen0Bytes + Gen1Bytes + Gen2Bytes + LargeObjectHeapBytes + PinnedObjectHeapBytes;

    /// <summary>
    /// What size rules compare: the heap after the last GC, or, before the process had any GC (all generation sizes still
    /// zero), the heap now, which is all there is to measure then.
    /// </summary>
    public long RuleBytes => HeapAfterGcBytes > 0 ? HeapAfterGcBytes : GcHeapBytes;

    /// <summary>Allocated since the last GC (some of it garbage): the heap now above the after-GC size.</summary>
    public long AllocatedSinceGcBytes => Math.Max(0, GcHeapBytes - HeapAfterGcBytes);

    /// <summary>Whether a GC ran during the interval (only then do the after-GC sizes and the time in GC change).</summary>
    public bool HadGc => Gen0Collections > 0;
}

/// <summary>Where samples come from: a process's runtime counters, or a test feed.</summary>
public interface ICounterSource
{
    /// <summary>Hands a sample to <paramref name="onSample"/> every <paramref name="interval"/> until canceled or the process exits.</summary>
    Task RunAsync(int processId, TimeSpan interval, Action<HeapSample> onSample, CancellationToken cancellationToken);
}

/// <summary>The runtime's EventCounters over EventPipe (ADR-021).</summary>
public sealed class EventPipeCounterSource : ICounterSource
{
    public Task RunAsync(int processId, TimeSpan interval, Action<HeapSample> onSample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        return CounterMonitor.RunAsync(
            processId,
            interval,
            c => onSample(new HeapSample(
                c.At, c.GcHeapBytes, c.Gen0Bytes, c.Gen1Bytes, c.Gen2Bytes, c.LargeObjectHeapBytes, c.PinnedObjectHeapBytes,
                c.Gen0Collections, c.Gen1Collections, c.Gen2Collections, c.AllocatedBytes, c.TimeInGcPercent, c.WorkingSetBytes)),
            cancellationToken);
    }
}

/// <summary>
/// Samples made up without a process (diagnostics and the UI smoke run, like the synthetic job): a heap that grows and
/// is collected back in a saw tooth, <paramref name="count"/> samples one interval apart in sample time, handed out at
/// <paramref name="perSecond"/> a second.
/// </summary>
public sealed class SyntheticCounterSource(int count, int perSecond) : ICounterSource
{
    public async Task RunAsync(int processId, TimeSpan interval, Action<HeapSample> onSample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        const long Mb = 1024 * 1024;
        var at = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromSeconds(1.0 / Math.Max(1, perSecond));
        for (var i = 0; i < count && !cancellationToken.IsCancellationRequested; i++)
        {
            var tooth = i % 600;
            var gen2 = (200 + tooth) * Mb;
            onSample(new HeapSample(at, gen2 + ((70 + (i % 10 * 8)) * Mb), 0, 10 * Mb, gen2, 50 * Mb, 10 * Mb, 3, 1, tooth == 0 ? 1 : 0, 64 * Mb, tooth % 100 < 20 ? 15 : 2, gen2 + (300 * Mb)));
            at += interval;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// How to monitor: the sample interval, the snapshot rules, whether to write the timeline file, and what rules capture
/// (sprint 1.8: full dumps, or gcdumps, which are smaller but force a full GC in the target that shows in its counters).
/// </summary>
public sealed record MonitorOptions(TimeSpan Interval, MonitorRules Rules, bool WriteTimeline = true, SnapshotKindDto CaptureKind = SnapshotKindDto.FullDump)
{
    public static MonitorOptions Default { get; } = new(TimeSpan.FromSeconds(1), MonitorRules.None);
}

/// <summary>A snapshot a rule took: when, which rule, and the capture or why it failed.</summary>
public sealed record MonitorCapture(DateTimeOffset At, string Rule, CaptureResult? Result, string? Error);

/// <summary>A sample as the run saw it: the counters, and whether GC pressure is high.</summary>
public sealed record MonitorSample(HeapSample Counters, bool HighGcPressure, long? Baseline);

/// <summary>
/// Starts monitoring runs (sprint 1.7): counters every interval, snapshot rules turned into captures in the current
/// session (<see cref="SnapshotSource.Timed"/>, labeled with the rule), and a timeline file in the session folder.
/// </summary>
public sealed class MonitorService(ICaptureService capture, ICounterSource? counters = null, ILogger<MonitorService>? logger = null)
{
    private readonly ICaptureService _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly ICounterSource _counters = counters ?? new EventPipeCounterSource();
    private readonly ILogger _logger = logger ?? NullLogger<MonitorService>.Instance;

    /// <summary>
    /// Starts monitoring <paramref name="processId"/>; the run ends when stopped or when the process exits.
    /// <paramref name="attach"/> subscribes to the run before the first sample can arrive.
    /// </summary>
    public MonitorRun Start(int processId, MonitorOptions options, Action<MonitorRun>? attach = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var run = new MonitorRun(processId, options, _capture, _logger);
        attach?.Invoke(run);
        run.Begin(_counters);
        return run;
    }
}

/// <summary>
/// One monitoring run. <see cref="SampleReceived"/> and <see cref="Captured"/> are raised on a background thread; front
/// ends marshal them. Timeline rows go to <see cref="TimelinePath"/> (one per sample, plus one per capture).
/// </summary>
public sealed class MonitorRun : IAsyncDisposable
{
    private readonly ICaptureService _capture;
    private readonly ILogger _logger;
    private readonly SnapshotRuleEngine _rules;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<MonitorCapture> _captures = [];
    private StreamWriter? _timeline;
    private bool _timelineFailed;
    private string? _folder;
    private Task? _capturing;

    internal MonitorRun(int processId, MonitorOptions options, ICaptureService capture, ILogger logger)
    {
        ProcessId = processId;
        Options = options;
        _capture = capture;
        _logger = logger;
        _rules = new SnapshotRuleEngine(options.Rules);
    }

    public event EventHandler<MonitorSample>? SampleReceived;

    public event EventHandler<MonitorCapture>? Captured;

    public int ProcessId { get; }

    public MonitorOptions Options { get; }

    /// <summary>The timeline file, once the first sample arrived (null when not written, or when writing it failed).</summary>
    public string? TimelinePath
    {
        get
        {
            lock (_gate)
            {
                return _timelineFailed ? null : _timelinePath;
            }
        }
    }

    private string? _timelinePath;

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
    }

    internal void Begin(ICounterSource counters)
    {
        // The run's session is fixed now: opening another session meanwhile does not split the timeline from it.
        if (Options.WriteTimeline)
        {
            try
            {
                _folder = _capture.Library.Current.Folder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The monitoring timeline has no session folder");
                _timelineFailed = true;
            }
        }

        Completion = RunAsync(counters);
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
            // whatever happened (review H3 of 1.7).
            Task? pending;
            lock (_gate)
            {
                pending = _capturing;
            }

            if (pending is not null)
            {
                await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            StreamWriter? timeline;
            lock (_gate)
            {
                timeline = _timeline;
                _timeline = null;
            }

            if (timeline is not null)
            {
                try
                {
                    await timeline.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "The monitoring timeline could not be closed");
                }
            }
        }
    }

    private void OnSample(HeapSample sample)
    {
        SnapshotRule? fired;
        MonitorSample seen;
        lock (_gate)
        {
            // After Stop no rule fires: the user asked for no more dumps (review of 1.7).
            var stopped = _stop.IsCancellationRequested;
            fired = stopped ? null : _rules.Evaluate(sample, captureRunning: _capturing is { IsCompleted: false });
            seen = new MonitorSample(sample, _rules.HighGcPressure, _rules.Baseline);
            WriteRow(sample, null);
            if (fired is not null)
            {
                // Started outside the lock: a capture logs and adds a job, which raises events.
                _capturing = Task.Run(() => CaptureAsync(fired, sample.At));
            }
        }

        Raise(SampleReceived, seen);
    }

    private async Task CaptureAsync(SnapshotRule rule, DateTimeOffset at)
    {
        MonitorCapture result;
        try
        {
            _logger.LogInformation("Monitoring pid {Pid}: {Rule} fired, capturing", ProcessId, rule.Label);
            var captured = await _capture.StartCapture(ProcessId, label: rule.Label, source: SnapshotSource.Timed, kind: Options.CaptureKind).Result.ConfigureAwait(false);
            result = new MonitorCapture(at, rule.Label, captured, null);
        }
        catch (CaptureFailedException ex)
        {
            _logger.LogWarning(ex, "Monitoring pid {Pid}: the {Rule} capture failed", ProcessId, rule.Label);
            result = new MonitorCapture(at, rule.Label, null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            // Canceled from the jobs panel, or when the app closes: recorded like a failure, the run goes on.
            _logger.LogInformation("Monitoring pid {Pid}: the {Rule} capture was canceled", ProcessId, rule.Label);
            result = new MonitorCapture(at, rule.Label, null, "Canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitoring pid {Pid}: the {Rule} capture failed unexpectedly", ProcessId, rule.Label);
            result = new MonitorCapture(at, rule.Label, null, "The capture failed. Details are in the log.");
        }

        lock (_gate)
        {
            if (result.Result is not null)
            {
                _rules.Taken();
            }

            _captures.Add(result);
            WriteRow(null, result);
        }

        Raise(Captured, result);
    }

    /// <summary>Raises an event; a listener that throws is logged and never ends the run (review of 1.7).</summary>
    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var listener in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                listener(this, value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A monitoring listener failed");
            }
        }
    }

    /// <summary>A capture marker's event cell, quoted for CSV.</summary>
    private static string Quoted(MonitorCapture capture)
    {
        var what = capture.Result is { } result ? Path.GetFileName(result.Path) : "failed: " + capture.Error;
        return "\"" + $"snapshot: {capture.Rule}: {what}".Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// One timeline row: a sample, or a capture marker (columns empty but the time and the event). The file is created
    /// new (a numbered suffix when the name is taken) in the run's session folder; after the first failure the run
    /// stops writing it and warns once.
    /// </summary>
    private void WriteRow(HeapSample? sample, MonitorCapture? capture)
    {
        if (!Options.WriteTimeline || _timelineFailed || _folder is null)
        {
            return;
        }

        try
        {
            if (_timeline is null)
            {
                var stem = string.Create(CultureInfo.InvariantCulture, $"counters-{ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}");
                for (var n = 1; _timeline is null; n++)
                {
                    var path = Path.Combine(_folder, n == 1 ? stem + ".csv" : string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}.csv"));
                    try
                    {
                        _timeline = new StreamWriter(PrivateFiles.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                        _timelinePath = path;
                    }
                    catch (IOException) when (File.Exists(path) && n < 100)
                    {
                        // Taken (two runs on one process within a second): try the next number.
                    }
                }

                _timeline.WriteLine("time,gc_heap_bytes,gen0_bytes,gen1_bytes,gen2_bytes,loh_bytes,poh_bytes,gen0_gcs,gen1_gcs,gen2_gcs,allocated_bytes,time_in_gc_percent,working_set_bytes,event");
            }

            _timeline.WriteLine(sample is { } s
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{s.At:O},{s.GcHeapBytes},{s.Gen0Bytes},{s.Gen1Bytes},{s.Gen2Bytes},{s.LargeObjectHeapBytes},{s.PinnedObjectHeapBytes},{s.Gen0Collections},{s.Gen1Collections},{s.Gen2Collections},{s.AllocatedBytes},{s.TimeInGcPercent:0.##},{s.WorkingSetBytes},")
                : string.Create(CultureInfo.InvariantCulture, $"{capture!.At:O},,,,,,,,,,,,,{Quoted(capture)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The timeline is a convenience: a full disk must not stop monitoring, and must not flood the log.
            _logger.LogWarning(ex, "The monitoring timeline could not be written; the run goes on without it");
            _timelineFailed = true;
            _timeline?.Dispose();
            _timeline = null;
        }
    }
}
