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
    private MonitorRun? _run;

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

    public LiveViewModel(MonitorService monitor, ICaptureService capture, IUiDispatcher dispatcher, ILogger<LiveViewModel>? logger = null, IUserInteraction? interaction = null)
    {
        _interaction = interaction;
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

    /// <summary>The run in progress, if any (tests and smoke runs await its completion).</summary>
    public MonitorRun? Run => _run;

    /// <summary>Whether runs write their timeline into the session folder (off for smoke runs, which leave nothing behind).</summary>
    public bool WriteTimeline { get; set; } = true;

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

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
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
        Snapshots.Clear();
        TimelinePath = null;
        HighGcPressure = false;
        Latest = null;
        ChartVersion++;
        var options = new MonitorOptions(
            TimeSpan.FromSeconds(Math.Max(1, IntervalSeconds)),
            rules,
            WriteTimeline,
            CaptureGcDumps ? SnapshotKindDto.GcDump : SnapshotKindDto.FullDump);
        _run = _monitor.Start(process.ProcessId, options, attached =>
        {
            _run = attached;
            attached.SampleReceived += (_, sample) => _dispatcher.Post(() => OnSample(attached, sample));
            attached.Captured += (_, capture) => _dispatcher.Post(() => OnCaptured(attached, capture));
        });
        var run = _run;
        IsRunning = true;
        Status = string.Create(CultureInfo.CurrentCulture, $"Monitoring {process.Name} (pid {process.ProcessId}){(rules.Rules.Count > 0 ? "; rules: " + string.Join(", ", rules.Rules.Select(r => r.Label)) : string.Empty)}.");
        _ = WatchAsync(run);
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _run?.Stop();

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
            _logger.LogInformation("Timeline deleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The type only: the message holds the path, which names the session and the process.
            _logger.LogWarning("Deleting the timeline failed: {Error}", ex.GetType().Name);
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
            IsRunning = false;
        });
    }
}
