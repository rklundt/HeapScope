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

using System.IO;
using System.Threading.Channels;
using HeapScope.Application.Jobs;
using HeapScope.Application.Monitoring;
using HeapScope.Application.Sessions;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The Live page (sprint 1.7): form validation, samples to the chart, rule snapshots as cards, stopping.</summary>
public sealed class LiveViewModelTests : IAsyncDisposable
{
    private const long Mb = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FeedSource _feed = new();
    private readonly FakeCaptureService _capture;
    private readonly FakeUserInteraction _interaction = new();
    private readonly LiveViewModel _live;

    public LiveViewModelTests()
    {
        _capture = new FakeCaptureService(_root, new JobTracker(TimeProvider.System));
        _capture.Processes.Add(new ProcessEntry(42, "app", null, "10.0.0", ProcessElevation.NotElevated, false, null, null));
        _live = new LiveViewModel(new MonitorService(_capture, _feed), _capture, new ImmediateUiDispatcher(), interaction: _interaction);
    }

    [Fact]
    public async Task Start_needs_a_process_and_valid_rules()
    {
        _live.StartCommand.Execute(null);
        _live.Status.ShouldBe("Pick a process to monitor.");

        await _live.RefreshCommand.ExecuteAsync(null);
        _live.SelectedProcess = _live.Processes.Single();
        foreach (var (field, text, message) in new (string Field, string Text, string Message)[]
        {
            ("above", "lots", "Above: enter a size above zero, such as 500MB or 2GB."),
            ("above", "50%", "Above: enter a size above zero, such as 500MB or 2GB."),
            ("above", "0MB", "Above: enter a size above zero, such as 500MB or 2GB."),
            ("growth", "much", "Growth: enter a size or a percent above zero, such as 100MB or 50%."),
            ("growth", "0%", "Growth: enter a size or a percent above zero, such as 100MB or 50%."),
            ("every", "0", "Every: enter minutes, such as 5 or 0.5."),
            ("max", "0", "Max snapshots: enter a whole number of 1 or more."),
        })
        {
            (_live.Above, _live.Growth, _live.Every, _live.MaxSnapshots) = (null, null, null, null);
            switch (field)
            {
                case "above": _live.Above = text; break;
                case "growth": _live.Growth = text; break;
                case "every": _live.Every = text; break;
                default: _live.MaxSnapshots = text; break;
            }

            _live.StartCommand.Execute(null);
            (_live.Status, _live.IsRunning).ShouldBe((message, false), $"{field} = {text}");
        }
    }

    [Fact]
    public async Task Get_snapshot_and_force_gc_mark_the_run_and_a_past_run_reopens_read_only()
    {
        // Sprint 3.4 (decisions 2, 1 and 5).
        await _live.RefreshCommand.ExecuteAsync(null);
        _live.SelectedProcess = _live.Processes.Single();
        _live.GetSnapshotCommand.CanExecute(null).ShouldBeFalse("only while monitoring");

        _live.StartCommand.Execute(null);
        await _feed.SendAsync(100 * Mb, timeInGc: 2);
        await _live.GetSnapshotCommand.ExecuteAsync(null);
        _live.Status.ShouldBe("Snapshot taken.");
        await _live.ForceGcCommand.ExecuteAsync(null);
        _live.Status!.ShouldStartWith("A full garbage collection ran");
        await _feed.SendAsync(120 * Mb, timeInGc: 2);
        _capture.Busy.Add(42);
        await _live.ForceGcCommand.ExecuteAsync(null);
        _live.Status!.ShouldContain("Wait for it to finish");
        _capture.Busy.Clear();

        (_live.Snapshots.ShouldHaveSingleItem().Rule, _live.Markers.Count, _live.GcMarkers.Count, _capture.ForcedGcs.Count).ShouldBe(("manual", 1, 1, 1));
        _live.StopCommand.Execute(null);
        await _live.Run!.Completion;
        await WaitUntilAsync(() => !_live.IsRunning && _live.PastRuns.Count == 1);

        var past = _live.PastRuns.ShouldHaveSingleItem();
        past.Title.ShouldContain("app 42, 1 snapshots");
        _live.StartCommand.Execute(null);
        _live.OpenPastRunCommand.CanExecute(past).ShouldBeFalse("not while monitoring");
        _live.StopCommand.Execute(null);
        await _live.Run!.Completion;
        await WaitUntilAsync(() => !_live.IsRunning);

        await _live.OpenPastRunCommand.ExecuteAsync(past);

        _live.Samples.Count.ShouldBe(2);
        (_live.Markers.Count, _live.GcMarkers.Count, _live.Snapshots.Count).ShouldBe((1, 1, 1));
        _live.PastRunShown.ShouldBe(past.Title);
        _live.Status!.ShouldStartWith("Showing a past run");
    }

    [Fact]
    public async Task Rules_capture_gcdumps_when_asked()
    {
        await _live.RefreshCommand.ExecuteAsync(null);
        _live.SelectedProcess = _live.Processes.Single();
        _live.Above = "500MB";
        _live.CaptureGcDumps = true;

        _live.StartCommand.Execute(null);
        await _feed.SendAsync(600 * Mb, timeInGc: 2);
        await WaitUntilAsync(() => _live.Snapshots.Count == 1);
        _live.StopCommand.Execute(null);

        _capture.Kinds.ShouldBe([SnapshotKindDto.GcDump]);
    }

    [Fact]
    public async Task Samples_reach_the_chart_rules_become_cards_and_markers_and_stop_ends_the_run()
    {
        await _live.RefreshCommand.ExecuteAsync(null);
        _live.SelectedProcess = _live.Processes.Single();
        _live.Above = "500MB";
        _live.Rearm = "once";

        _live.StartCommand.Execute(null);
        _live.IsRunning.ShouldBeTrue();
        _live.Status!.ShouldContain("rules: above 500 MB");
        await _feed.SendAsync(100 * Mb, timeInGc: 2);
        await _feed.SendAsync(600 * Mb, timeInGc: 30);
        await WaitUntilAsync(() => _live.Snapshots.Count == 1);
        await _feed.SendAsync(700 * Mb, timeInGc: 30);

        _live.Samples.Select(s => s.HeapAfterGcBytes).ShouldBe([100 * Mb, 600 * Mb, 700 * Mb]);
        _live.ChartVersion.ShouldBeGreaterThanOrEqualTo(4, "a redraw per sample and per snapshot");
        _live.Latest!.ShouldContain("after the last GC 700");
        _live.HighGcPressure.ShouldBeTrue("(2 + 30 + 30) / 3 is over 10 percent");
        var card = _live.Snapshots.ShouldHaveSingleItem("once: one snapshot");
        (card.Rule, card.Error).ShouldBe(("above 500 MB", null));
        _live.Markers.ShouldHaveSingleItem().ShouldBe(_live.Samples[1].At);

        _live.StopCommand.Execute(null);
        await _live.Run!.Completion;
        await WaitUntilAsync(() => !_live.IsRunning);
        _live.Status!.ShouldStartWith("Stopped after 3 samples and 1 snapshots.");
        _live.StartCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task The_stopped_runs_timeline_opens_shows_in_its_folder_and_deletes_after_asking()
    {
        await _live.RefreshCommand.ExecuteAsync(null);
        _live.SelectedProcess = _live.Processes.Single();
        _live.StartCommand.Execute(null);
        _live.HasTimeline.ShouldBeFalse("not while running");
        await _feed.SendAsync(100 * Mb, timeInGc: 2);
        await WaitUntilAsync(() => _live.Samples.Count == 1);
        _live.StopCommand.Execute(null);
        await _live.Run!.Completion;
        await WaitUntilAsync(() => !_live.IsRunning);

        var path = _live.TimelinePath.ShouldNotBeNull();
        File.Exists(path).ShouldBeTrue();
        _live.Status!.ShouldNotContain(path, Case.Insensitive, "the path has its own row");
        await _live.OpenTimelineCommand.ExecuteAsync(null);
        await _live.ShowTimelineCommand.ExecuteAsync(null);
        (_interaction.Opened.Single(), _interaction.Shown.Single()).ShouldBe((path, path));

        _interaction.Answer = false;
        await _live.DeleteTimelineCommand.ExecuteAsync(null);
        File.Exists(path).ShouldBeTrue("declined");
        _interaction.Questions.Single().ShouldContain(Path.GetFileName(path));

        _interaction.Answer = true;
        await _live.DeleteTimelineCommand.ExecuteAsync(null);
        File.Exists(path).ShouldBeFalse();
        (_live.TimelinePath, _live.HasTimeline, _live.Status).ShouldBe((null, false, "Timeline deleted."));
        _live.OpenTimelineCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_snapshot_card_shows_its_file_in_its_folder()
    {
        await _live.RefreshCommand.ExecuteAsync(null);
        _live.SelectedProcess = _live.Processes.Single();
        _live.Above = "500MB";
        _live.StartCommand.Execute(null);
        await _feed.SendAsync(600 * Mb, timeInGc: 2);
        await WaitUntilAsync(() => _live.Snapshots.Count == 1);
        _live.StopCommand.Execute(null);
        await _live.Run!.Completion;

        var card = _live.Snapshots.Single();
        var path = card.Path.ShouldNotBeNull();
        await _live.ShowSnapshotCommand.ExecuteAsync(card);
        _interaction.Shown.ShouldBe([path]);

        File.Delete(path);
        await _live.ShowSnapshotCommand.ExecuteAsync(card);
        _interaction.Shown.Count.ShouldBe(1);
        _live.Status.ShouldBe("That snapshot file is no longer there.");
    }

    [Fact]
    public async Task A_timeline_removed_outside_HeapScope_is_said_to_be_gone()
    {
        await _live.RefreshCommand.ExecuteAsync(null);
        _live.SelectedProcess = _live.Processes.Single();
        _live.StartCommand.Execute(null);
        await _feed.SendAsync(100 * Mb, timeInGc: 2);
        await WaitUntilAsync(() => _live.Samples.Count == 1);
        _live.StopCommand.Execute(null);
        await _live.Run!.Completion;
        await WaitUntilAsync(() => !_live.IsRunning);
        File.Delete(_live.TimelinePath.ShouldNotBeNull());

        await _live.OpenTimelineCommand.ExecuteAsync(null);

        _interaction.Opened.ShouldBeEmpty();
        (_live.HasTimeline, _live.Status).ShouldBe((false, "The timeline file is no longer there."));
    }

    [Fact]
    public void The_chart_stacks_heap_kinds_over_the_samples_time_span()
    {
        var at = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        HeapSample[] samples =
        [
            new(at, 40, 10, 10, 20, 0, 0, 0, 0, 0, 0, 0, 0),
            new(at.AddSeconds(10), 100, 10, 10, 50, 10, 10, 0, 0, 0, 0, 0, 0),
        ];

        var layers = TimelineLayout.Layers(samples, width: 200, height: 100, out var max);

        max.ShouldBe(100);
        layers.Count.ShouldBe(6);
        layers[5][1].Y.ShouldBe(0.0, "allocated since the last GC (10 bytes) tops the stack at the heap now");
        layers[4][1].Y.ShouldBe(10.0);
        layers[0].Select(p => (p.X, p.Y)).ShouldBe([(0.0, 90.0), (200.0, 90.0)], "gen0 at the bottom");
        layers[2].Select(p => p.Y).ShouldBe([60.0, 30.0], "gen2 stacked on gen0 and gen1");
        TimelineLayout.XOf(samples, at.AddSeconds(5), 200).ShouldBe(100.0);
        TimelineLayout.XOf(samples, at.AddSeconds(20), 200).ShouldBeNull();
    }

    public async ValueTask DisposeAsync()
    {
        await _live.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Samples sent by the test, handed out on the source's own thread like EventPipe's.</summary>
    private sealed class FeedSource : ICounterSource
    {
        private readonly Channel<(long Heap, double TimeInGc)> _samples = Channel.CreateUnbounded<(long, double)>();
        private readonly SemaphoreSlim _handled = new(0);
        private DateTimeOffset _at = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        public async Task SendAsync(long heapBytes, double timeInGc)
        {
            await _samples.Writer.WriteAsync((heapBytes, timeInGc));
            await _handled.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task RunAsync(int processId, TimeSpan interval, Action<HeapSample> onSample, CancellationToken cancellationToken)
        {
            try
            {
                while (await _samples.Reader.WaitToReadAsync(cancellationToken))
                {
                    var (heap, timeInGc) = await _samples.Reader.ReadAsync(cancellationToken);
                    _at += interval;
                    onSample(new HeapSample(_at, heap + (10 * Mb), 0, heap / 10, heap * 7 / 10, heap / 5, 0, 1, 0, 0, Mb, timeInGc, heap * 2));
                    _handled.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }
    }
}
