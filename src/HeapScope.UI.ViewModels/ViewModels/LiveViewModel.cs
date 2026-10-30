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

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Application.Monitoring;
using HeapScope.Application.Sessions;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.UI.ViewModels;

/// <summary>A monitoring run of the current session, listed to reopen (sprint 3.4).</summary>
public sealed record PastRunItem(SessionRun Run, string Folder)
{
    public string Title => string.Create(CultureInfo.CurrentCulture, $"{Run.StartedUtc.ToLocalTime():g}: {Run.ProcessName ?? "process"} {Run.ProcessId}, {Run.SnapshotIds.Count} snapshots{(Run.EndedUtc is { } end ? $", {(end - Run.StartedUtc).TotalMinutes:N0} min" : ", not ended")}{(Run.Trace is null ? string.Empty : ", allocations")}");
}

/// <summary>A snapshot a rule took during the run, as a card: when, which rule, how big, or why it failed.</summary>
public sealed record SnapshotCard(DateTimeOffset At, string Rule, string? Path, long SizeBytes, string? Error)
{
    public string Time => At.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    public string Summary => Error is null ? $"{Format.Bytes(SizeBytes)}, {System.IO.Path.GetFileName(Path)}" : "failed: " + Error;
}

/// <summary>
/// The Live page (sprint 1.7, ADR-021): monitor a process's GC heap per heap kind every interval, take snapshots by
/// rules, see the run as a stacked chart with snapshot markers and cards, and a badge when GC pressure is high. Samples
/// arrive on a background thread and are marshaled here; the chart keeps the last <see cref="WindowSamples"/>.
/// </summary>
public sealed partial class LiveViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Samples the chart keeps (30 minutes at 1 s).</summary>
    public const int WindowSamples = 1800;

    private readonly MonitorService _monitor;
    private readonly ICaptureService _capture;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserInteraction? _interaction;
    private readonly ILogger _logger;
    private readonly List<HeapSample> _samples = [];
    private readonly List<DateTimeOffset> _markers = [];
    private readonly List<DateTimeOffset> _gcMarkers = [];
    private readonly AllocationsViewModel? _allocations;
    private MonitorRun? _run;

    /// <summary>Records an allocation trace for the whole run (sprint 3.4).</summary>
    [ObservableProperty]
    private bool _recordAllocations;

    /// <summary>The last run's allocation trace, once it ended with one.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenTraceCommand))]
    private string? _tracePath;

    /// <summary>A Get snapshot or Force GC in progress.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GetSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceGcCommand))]
    private bool _acting;

    /// <summary>What the chart shows when it is a past run's (sprint 3.4), read only.</summary>
    [ObservableProperty]
    private string? _pastRunShown;

    [ObservableProperty]
    private ProcessRow? _selectedProcess;

    [ObservableProperty]
    private int _intervalSeconds = 1;

    [ObservableProperty]
    private string? _every;

    [ObservableProperty]
    private string? _above;

    [ObservableProperty]
    private string? _growth;

    [ObservableProperty]
    private string _rearm = "below";

    [ObservableProperty]
    private string? _maxSnapshots;

    /// <summary>Rules capture gcdumps instead of full dumps (sprint 1.8). Each forces a full GC, which the chart shows.</summary>
    [ObservableProperty]
    private bool _captureGcDumps;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(GetSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceGcCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenPastRunCommand))]
    private bool _isRunning;

    /// <summary>The last run's timeline file once the run stopped, while it is there; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline))]
    [NotifyCanExecuteChangedFor(nameof(OpenTimelineCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowTimelineCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTimelineCommand))]
    private string? _timelinePath;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _latest;

    [ObservableProperty]
    private bool _highGcPressure;

    /// <summary>Bumped once per sample: the chart redraws when it changes.</summary>
    [ObservableProperty]
    private int _chartVersion;

    public LiveViewModel(MonitorService monitor, ICaptureService capture, IUiDispatcher dispatcher, ILogger<LiveViewModel>? logger = null, IUserInteraction? interaction = null, AllocationsViewModel? allocations = null)
    {
        _interaction = interaction;
        _allocations = allocations;
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger<LiveViewModel>.Instance;
    }

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    public ObservableCollection<SnapshotCard> Snapshots { get; } = [];

    public IReadOnlyList<string> RearmModes { get; } = ["below", "once", "step"];

    /// <summary>Whether the timeline row (its path, Open, Show in folder, Delete) is shown.</summary>
    public bool HasTimeline => TimelinePath is not null;

    /// <summary>The chart's samples, oldest first (read on the UI thread).</summary>
    public IReadOnlyList<HeapSample> Samples => _samples;

    /// <summary>When rules took snapshots, for the chart's markers.</summary>
    public IReadOnlyList<DateTimeOffset> Markers => _markers;

    /// <summary>When the run forced a collection (sprint 3.4).</summary>
    public IReadOnlyList<DateTimeOffset> GcMarkers => _gcMarkers;

    /// <summary>The current session's monitoring runs, newest first (sprint 3.4).</summary>
    public ObservableCollection<PastRunItem> PastRuns { get; } = [];

    /// <summary>The run in progress, if any (tests and smoke runs await its completion).</summary>
    public MonitorRun? Run => _run;

    /// <summary>Whether runs write their timeline into the session folder.</summary>
    public bool WriteTimeline { get; set; } = true;

    /// <summary>
    /// Whether runs use the session library at all: their record, timeline and trace (off for smoke runs, which leave
    /// nothing behind in the user's sessions).
    /// </summary>
    public bool RecordRuns { get; set; } = true;

    /// <summary>Why monitoring a process is not available on this system (sprint 2.3.6), or null.</summary>
    public string? Unavailable => _capture.UnavailableReason;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Unavailable is { } reason)
        {
            Status = reason;
            return;
        }

        try
        {
            var processes = await _capture.ListProcessesAsync(CancellationToken.None);
            var selected = SelectedProcess?.ProcessId;
            Processes.Clear();
            foreach (var process in processes)
            {
                Processes.Add(ProcessRow.From(process));
            }

            SelectedProcess = Processes.FirstOrDefault(p => p.ProcessId == selected);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Status = "The process list could not be read: " + ex.Message;
        }
    }

    private bool CanStart() => !IsRunning;

    /// <summary>
    /// Starts monitoring. The run's start reads and records its session on disk under the session's lock, which can wait,
    /// so it runs off the UI thread (review H3 of 3.4).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedProcess is not { } process)
        {
            Status = "Pick a process to monitor.";
            return;
        }

        if (ParseRules() is not { } rules)
        {
            return;
        }

        _samples.Clear();
        _markers.Clear();
        _gcMarkers.Clear();
        Snapshots.Clear();
        TimelinePath = null;
        TracePath = null;
        PastRunShown = null;
        HighGcPressure = false;
        Latest = null;
        ChartVersion++;
        var options = new MonitorOptions(
            TimeSpan.FromSeconds(Math.Max(1, IntervalSeconds)),
            rules,
            WriteTimeline,
            CaptureGcDumps ? SnapshotKindDto.GcDump : SnapshotKindDto.FullDump,
            RecordAllocations,
            process.Name,
            RecordRun: RecordRuns);
        IsRunning = true;
        _stopAsked = false;
        Status = string.Create(CultureInfo.CurrentCulture, $"Starting to monitor {process.Name} (pid {process.ProcessId})...");
        MonitorRun run;
        try
        {
            run = await Task.Run(() => _monitor.Start(process.ProcessId, options, attached =>
            {
                // Before the first sample can arrive: every event of the run finds it current.
                _run = attached;
                attached.SampleReceived += (_, sample) => _dispatcher.Post(() => OnSample(attached, sample));
                attached.Captured += (_, capture) => _dispatcher.Post(() => OnCaptured(attached, capture));
                attached.GcForced += (_, forced) => _dispatcher.Post(() => OnGcForced(attached, forced));
                attached.TraceStopped += (_, message) => _dispatcher.Post(() => OnTraceStopped(attached, message));
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Monitoring pid {Pid} could not start", process.ProcessId);
            Status = "Monitoring could not start: " + ex.Message;
            IsRunning = false;
            return;
        }

        if (_stopAsked)
        {
            run.Stop();
        }

        if (ReferenceEquals(run, _run) && IsRunning && run.TraceError is null)
        {
            Status = string.Create(CultureInfo.CurrentCulture, $"Monitoring {process.Name} (pid {process.ProcessId}){(rules.Rules.Count > 0 ? "; rules: " + string.Join(", ", rules.Rules.Select(r => r.Label)) : string.Empty)}.");
        }

        _ = WatchAsync(run);
    }

    /// <summary>What a forced collection does to the target, asked before each one (review of 3.4; the ask-first rule).</summary>
    public const string ForceGcQuestion =
        "Force a full, blocking garbage collection in {0}? Every managed thread in it pauses while the collection runs, for "
        + "seconds on a large heap. Finalizers do not run, so objects waiting for finalization, and what they hold, stay "
        + "until a later collection. An app inside a no-GC region leaves it, and its EndNoGCRegion call then fails. Objects "
        + "the collection moves count as dead and new in a comparison by address.";

    private bool CanAct() => IsRunning && !Acting;

    /// <summary>Takes a snapshot of the monitored process now (sprint 3.4), as the run's card.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task GetSnapshotAsync()
    {
        if (_run is not { } run)
        {
            return;
        }

        Acting = true;
        try
        {
            Status = "Taking a snapshot...";
            var taken = await run.CaptureNowAsync();
            Status = taken.Result is not null ? "Snapshot taken." : "The snapshot failed: " + taken.Error;
        }
        finally
        {
            Acting = false;
        }
    }

    /// <summary>Forces a full garbage collection in the monitored process (sprint 3.4), marked on the chart.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ForceGcAsync()
    {
        if (_run is not { } run)
        {
            return;
        }

        var target = run.Options.ProcessName ?? string.Create(CultureInfo.InvariantCulture, $"pid {run.ProcessId}");
        if (_interaction is not null)
        {
            var yes = await _interaction.ConfirmAsync("Force a garbage collection", string.Format(CultureInfo.CurrentCulture, ForceGcQuestion, target));
            _logger.LogInformation("Force GC of pid {Pid} (run {RunId}) asked; the user answered {Answer}", run.ProcessId, run.Id, yes ? "yes" : "no");
            if (!yes)
            {
                return;
            }
        }

        Acting = true;
        try
        {
            Status = "Forcing a garbage collection...";
            var forced = await run.ForceGcAsync(CancellationToken.None);
            Status = forced.Result is { Completed: true } ? "A full garbage collection ran in the process. Objects it moved count as dead and new in a comparison by address."
                : forced.Result is { } incomplete ? "The collection is not confirmed: " + incomplete.Outcome + ". It is not marked on the chart."
                : forced.Error;
        }
        finally
        {
            Acting = false;
        }
    }

    private bool CanOpenTrace() => TracePath is not null && _allocations is not null;

    /// <summary>Opens the last run's allocation trace on the Allocations page (sprint 3.4).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTrace))]
    private async Task OpenTraceAsync()
    {
        if (TracePath is { } path && _allocations is { } allocations)
        {
            await allocations.OpenPathAsync(path);
        }
    }

    /// <summary>Lists the current session's monitoring runs (sprint 3.4).</summary>
    [RelayCommand]
    private async Task RefreshRunsAsync()
    {
        // Read from disk, off the UI thread (a read waits up to a second for a program that holds the document, sprint 3.6
        // review): runs are recorded there as they start and end (never creating a session to list nothing).
        var folder = _capture.Library.CurrentFolder;
        var session = folder is null ? null : await Task.Run(() => SessionLibrary.TryRead(folder));
        PastRuns.Clear();
        if (session is null)
        {
            return;
        }

        foreach (var run in (session.Document.Runs ?? []).OrderByDescending(r => r.StartedUtc))
        {
            PastRuns.Add(new PastRunItem(run, session.Folder));
        }
    }

    private bool CanOpenPastRun(PastRunItem? item) => !IsRunning;

    /// <summary>
    /// Shows a past run read only (sprint 3.4, decision 5): its timeline's counters, its snapshots and forced collections as
    /// markers, and its snapshots as cards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenPastRun))]
    private async Task OpenPastRunAsync(PastRunItem? item)
    {
        if (item is null)
        {
            return;
        }

        var run = item.Run;
        // Only a timeline inside its session folder: Delete timeline acts on this path (3.4 re-review).
        if (run.Timeline is not { } timeline || Path.GetFullPath(Path.Combine(item.Folder, timeline)) is var path
            && (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)))
        {
            _logger.LogInformation("Monitoring run {RunId} not reopened: its timeline {Timeline} is not there", run.Id, run.Timeline ?? "(none recorded)");
            Status = "This run's timeline file is not there any more.";
            return;
        }

        try
        {
            var read = await TimelineFile.ReadAsync(path, CancellationToken.None);
            _samples.Clear();
            _samples.AddRange(read.Samples.TakeLast(WindowSamples));
            _markers.Clear();
            _markers.AddRange(read.Events.Where(e => e.Text.StartsWith(MonitorRun.SnapshotEventPrefix, StringComparison.Ordinal)).Select(e => e.At));
            _gcMarkers.Clear();
            _gcMarkers.AddRange(read.Events.Where(e => e.Text == MonitorRun.GcForcedEvent).Select(e => e.At));
            Snapshots.Clear();
            var session = await Task.Run(() => SessionLibrary.TryRead(item.Folder));
            foreach (var snapshot in (session?.Document.Snapshots ?? []).Where(s => run.SnapshotIds.Contains(s.Id)).OrderByDescending(s => s.CapturedUtc))
            {
                var file = session!.PathOf(snapshot);
                Snapshots.Add(new SnapshotCard(snapshot.CapturedUtc, snapshot.Label ?? snapshot.Source.ToString(), file, snapshot.SizeBytes, File.Exists(file) ? null : "deleted"));
            }

            TimelinePath = path;
            TracePath = run.Trace is { } trace ? Path.Combine(item.Folder, trace) : null;
            PastRunShown = item.Title + (read.SkippedLines > 0 ? string.Create(CultureInfo.CurrentCulture, $" ({read.SkippedLines} unreadable lines left out)") : string.Empty);
            Status = "Showing a past run, read only. Start monitoring to see live data again.";
            ChartVersion++;
            _logger.LogInformation(
                "Reopened monitoring run {RunId} of pid {Pid} from {Timeline}: {Samples} samples ({Shown} shown, {Skipped} unreadable lines), {Events} events, {Snapshots} snapshots",
                run.Id,
                run.ProcessId,
                path,
                read.Samples.Count,
                _samples.Count,
                read.SkippedLines,
                read.Events.Count,
                Snapshots.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Monitoring run {RunId}: its timeline could not be read", run.Id);
            Status = "The run's timeline could not be read: " + ex.Message;
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        // Pressed while the run is still starting: it stops as soon as it has started (3.4 re-review).
        _stopAsked = true;
        _run?.Stop();
    }

    private bool _stopAsked;

    private bool CanUseTimeline() => TimelinePath is not null && _interaction is not null;

    [RelayCommand(CanExecute = nameof(CanUseTimeline))]
    private async Task OpenTimelineAsync()
    {
        if (TimelineOrGone() is { } path)
        {
            var opened = await _interaction!.OpenFileAsync(path);
            _logger.LogInformation("Timeline open asked: {Outcome}", opened ? "opened" : "no app");
            if (!opened)
            {
                Status = "No app opened the timeline. It is a CSV file; Show in folder finds it.";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseTimeline))]
    private async Task ShowTimelineAsync()
    {
        if (TimelineOrGone() is { } path)
        {
            var shown = await _interaction!.ShowInFolderAsync(path);
            _logger.LogInformation("Timeline show in folder asked: {Outcome}", shown ? "shown" : "failed");
            if (!shown)
            {
                Status = "The folder could not be opened.";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseTimeline))]
    private async Task DeleteTimelineAsync()
    {
        if (TimelineOrGone() is not { } path
            || !await _interaction!.ConfirmAsync("Delete the timeline", $"Delete {Path.GetFileName(path)}? This cannot be undone. The run's snapshots are kept."))
        {
            return;
        }

        try
        {
            File.Delete(path);
            TimelinePath = null;
            Status = "Timeline deleted.";

            // The path of the user's own file, never its contents (ARCHITECTURE 11): a past run reopens from it.
            _logger.LogInformation("Timeline {Path} deleted after the user confirmed", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Deleting the timeline {Path} failed: {Error}", path, ex.GetType().Name);
            Status = "The timeline could not be deleted: " + ex.Message;
        }
    }

    /// <summary>Shows a rule's snapshot file in its folder (a failed capture's card has none).</summary>
    [RelayCommand]
    private async Task ShowSnapshotAsync(SnapshotCard? card)
    {
        if (card?.Path is not { } path || _interaction is null)
        {
            return;
        }

        if (!File.Exists(path))
        {
            Status = "That snapshot file is no longer there.";
            return;
        }

        var shown = await _interaction.ShowInFolderAsync(path);
        _logger.LogInformation("Snapshot show in folder asked: {Outcome}", shown ? "shown" : "failed");
        if (!shown)
        {
            Status = "The folder could not be opened.";
        }
    }

    /// <summary>The timeline's path, or null after saying it is gone (deleted or moved outside HeapScope).</summary>
    private string? TimelineOrGone()
    {
        if (TimelinePath is { } path && File.Exists(path))
        {
            return path;
        }

        TimelinePath = null;
        Status = "The timeline file is no longer there.";
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_run is { } run)
        {
            await run.DisposeAsync();
        }
    }

    /// <summary>The rules from the form, or null after saying what is wrong.</summary>
    private MonitorRules? ParseRules()
    {
        var mode = Rearm switch { "once" => RearmMode.Once, "step" => RearmMode.Step, _ => RearmMode.Below };
        var rules = new List<SnapshotRule>();
        if (!string.IsNullOrWhiteSpace(Every))
        {
            if (!TryParseMinutes(Every, out var interval))
            {
                Status = "Every: enter minutes, such as 5 or 0.5.";
                return null;
            }

            rules.Add(new EveryRule(interval));
        }

        if (!string.IsNullOrWhiteSpace(Above))
        {
            if (!GrowthThreshold.TryParse(Above, out var size) || size.Bytes is not { } bytes || bytes == 0)
            {
                Status = "Above: enter a size above zero, such as 500MB or 2GB.";
                return null;
            }

            rules.Add(new AboveRule(bytes, mode));
        }

        if (!string.IsNullOrWhiteSpace(Growth))
        {
            if (!GrowthThreshold.TryParse(Growth, out var growth) || growth.Bytes is 0 || growth.Percent is 0)
            {
                Status = "Growth: enter a size or a percent above zero, such as 100MB or 50%.";
                return null;
            }

            rules.Add(new GrowthRule(growth.Bytes, growth.Percent, mode));
        }

        int? max = null;
        if (!string.IsNullOrWhiteSpace(MaxSnapshots))
        {
            if (!int.TryParse(MaxSnapshots, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) || count < 1)
            {
                Status = "Max snapshots: enter a whole number of 1 or more.";
                return null;
            }

            max = count;
        }

        return new MonitorRules(rules, max);
    }

    private static bool TryParseMinutes(string text, out TimeSpan interval)
    {
        interval = default;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var minutes) || !double.IsFinite(minutes) || minutes * 60 < 1)
        {
            return false;
        }

        interval = TimeSpan.FromMinutes(minutes);
        return true;
    }

    private void OnSample(MonitorRun run, MonitorSample sample)
    {
        if (!ReferenceEquals(run, _run))
        {
            return;
        }

        _samples.Add(sample.Counters);
        if (_samples.Count > WindowSamples)
        {
            _samples.RemoveRange(0, _samples.Count - WindowSamples);
        }

        var s = sample.Counters;
        HighGcPressure = sample.HighGcPressure;
        Latest = string.Create(CultureInfo.CurrentCulture,
            $"GC heap {Format.Bytes(s.GcHeapBytes)}, after the last GC {Format.Bytes(s.HeapAfterGcBytes)} (gen1 {Format.Bytes(s.Gen1Bytes)}, gen2 {Format.Bytes(s.Gen2Bytes)}, LOH {Format.Bytes(s.LargeObjectHeapBytes)}, POH {Format.Bytes(s.PinnedObjectHeapBytes)}); allocated {Format.Bytes(s.AllocatedBytes)}; GCs {s.Gen0Collections}/{s.Gen1Collections}/{s.Gen2Collections}; last GC {s.TimeInGcPercent:0.#}% of the time");
        ChartVersion++;
    }

    private void OnGcForced(MonitorRun run, MonitorForcedGc forced)
    {
        // Only a collection whose end was seen is marked (review of 3.4).
        if (!ReferenceEquals(run, _run) || forced.Result is not { Completed: true })
        {
            return;
        }

        _gcMarkers.Add(forced.At);
        ChartVersion++;
    }

    private void OnTraceStopped(MonitorRun run, string message)
    {
        if (ReferenceEquals(run, _run))
        {
            Status = message;
        }
    }

    private void OnCaptured(MonitorRun run, MonitorCapture capture)
    {
        if (!ReferenceEquals(run, _run))
        {
            return;
        }

        _markers.Add(capture.At);
        Snapshots.Insert(0, new SnapshotCard(capture.At, capture.Rule, capture.Result?.Path, capture.Result?.SizeBytes ?? 0, capture.Error));
        ChartVersion++;
    }

    private async Task WatchAsync(MonitorRun run)
    {
        string? ended = null;
        try
        {
            await run.Completion;
        }
        catch (CaptureFailedException ex)
        {
            ended = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Stopped before the session connected: a normal stop.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monitoring pid {Pid} failed", run.ProcessId);
            ended = "Monitoring failed. Details are in the log.";
        }

        _dispatcher.Post(() =>
        {
            if (!ReferenceEquals(run, _run))
            {
                return;
            }

            // The final status first: whoever sees IsRunning turn false (a test on another thread) then reads it.
            Status = ended ?? string.Create(CultureInfo.CurrentCulture,
                $"Stopped after {_samples.Count:N0} samples and {run.Captures.Count(c => c.Result is not null)} snapshots.");
            TimelinePath = run.TimelinePath;
            TracePath = run.TracePath;
            if (run.TraceError is { } traceError)
            {
                Status += " " + traceError;
            }

            IsRunning = false;
            _ = RefreshRunsCommand.ExecuteAsync(null);
        });
    }
}
