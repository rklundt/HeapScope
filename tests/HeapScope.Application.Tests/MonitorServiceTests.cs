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
using HeapScope.Application.Monitoring;
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

/// <summary>Monitoring runs with a fed sample stream and recorded captures (sprint 1.7).</summary>
public sealed class MonitorServiceTests : IDisposable
{
    private const long Mb = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FeedCounterSource _feed = new();
    private readonly RecordingCaptureService _capture;

    public MonitorServiceTests()
    {
        _capture = new RecordingCaptureService(_root, new JobTracker(TimeProvider.System));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rules_take_timed_snapshots_labeled_by_rule_and_the_timeline_records_samples_and_markers()
    {
        var service = new MonitorService(_capture, _feed);
        var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb)])));
        var samples = new List<MonitorSample>();
        run.SampleReceived += (_, s) => samples.Add(s);

        foreach (var heap in new long[] { 100, 600, 650, 440, 700 })
        {
            await _feed.SendAsync(heap * Mb);
            await run.CaptureTask;
        }

        run.Stop();
        await run.Completion;

        run.Captures.Select(c => c.Rule).ShouldBe(["above 500 MB", "above 500 MB"], "fires when crossed, re-arms under 500 MB, fires again");
        _capture.Taken.ShouldAllBe(t => t.Source == SnapshotSource.Timed && t.Label == "above 500 MB");
        samples.Count.ShouldBe(5);
        samples[0].Baseline.ShouldBe(100 * Mb);

        var rows = await File.ReadAllLinesAsync(run.TimelinePath.ShouldNotBeNull(), Ct);
        rows[0].ShouldStartWith("time,gc_heap_bytes,");
        rows.Count(r => r.EndsWith(',')).ShouldBe(5, "one row per sample");
        rows.Count(r => r.Contains("\"snapshot: above 500 MB: ", StringComparison.Ordinal)).ShouldBe(2);
        rows[1].Split(',')[1].ShouldBe((100 * Mb).ToString(CultureInfo.InvariantCulture));
        Path.GetDirectoryName(run.TimelinePath).ShouldBe(_capture.Library.Current.Folder);
    }

    [Fact]
    public async Task Rules_capture_the_kind_the_run_was_started_with()
    {
        var run = new MonitorService(_capture, _feed).Start(
            42,
            new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb)]), WriteTimeline: false, CaptureKind: SnapshotKindDto.GcDump));

        await _feed.SendAsync(600 * Mb);
        await run.CaptureTask;
        run.Stop();
        await run.Completion;

        _capture.Kinds.ShouldBe([SnapshotKindDto.GcDump]);
    }

    [Fact]
    public async Task A_failed_capture_is_recorded_and_does_not_count_toward_the_maximum()
    {
        _capture.Failure = new CaptureFailedException(CaptureFailure.TargetExited, "The process exited.", null);
        var run = new MonitorService(_capture, _feed).Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb, RearmMode.Step)], MaxSnapshots: 1)));

        await _feed.SendAsync(600 * Mb);
        await run.CaptureTask;
        _capture.Failure = null;
        await _feed.SendAsync(1100 * Mb);
        await run.CaptureTask;
        await _feed.SendAsync(1600 * Mb);
        await run.CaptureTask;
        run.Stop();
        await run.Completion;

        run.Captures.Select(c => (c.Result is not null, c.Error)).ShouldBe([(false, "The process exited."), (true, null)], "the maximum of one counts written snapshots");
    }

    [Fact]
    public async Task A_canceled_rule_capture_is_recorded_and_the_run_ends_normally_with_its_timeline_closed()
    {
        _capture.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new MonitorService(_capture, _feed).Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb)])));

        await _feed.SendAsync(600 * Mb);
        await WaitUntilAsync(() => _capture.Jobs.Count == 1);
        _capture.Jobs[0].Cancel();
        await WaitUntilAsync(() => run.Captures.Count == 1);
        run.Stop();
        await run.Completion;

        run.Captures.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Result.ShouldBeNull(), c => c.Error.ShouldBe("Canceled."));
        var rows = await File.ReadAllLinesAsync(run.TimelinePath.ShouldNotBeNull(), Ct);
        rows.ShouldContain(r => r.Contains("failed: Canceled.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Samples_while_a_capture_runs_take_no_second_snapshot_and_none_after_stop()
    {
        _capture.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new MonitorService(_capture, _feed).Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb, RearmMode.Step)])));

        await _feed.SendAsync(600 * Mb);
        await _feed.SendAsync(1_200 * Mb);
        await _feed.SendAsync(1_800 * Mb);
        _capture.Gate.SetResult();
        await WaitUntilAsync(() => run.Captures.Count == 1);
        run.Stop();
        await _feed.SendAsync(2_400 * Mb);
        await run.Completion;

        run.Captures.Count.ShouldBe(1, "the rules waited while the first capture ran, and none fires after Stop");
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_end_the_run()
    {
        var run = new MonitorService(_capture, _feed).Start(42, MonitorOptions.Default, r => r.SampleReceived += (_, _) => throw new InvalidOperationException("listener"));
        await _feed.SendAsync(100 * Mb);
        await _feed.SendAsync(200 * Mb);
        run.Stop();
        await run.Completion;
        run.Completion.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task A_process_that_cannot_be_monitored_faults_the_run()
    {
        _feed.Failure = new TargetUnavailableException("No running process has id 42.");
        var run = new MonitorService(_capture, _feed).Start(42, MonitorOptions.Default);

        var failed = await Should.ThrowAsync<CaptureFailedException>(() => run.Completion);
        (failed.Reason, failed.Message).ShouldBe((CaptureFailure.TargetExited, "No running process has id 42."));
        await run.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(10, Ct);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Samples sent by the test, one at a time, on the source's own thread like EventPipe's.</summary>
    private sealed class FeedCounterSource : ICounterSource
    {
        private readonly System.Threading.Channels.Channel<long> _heaps = System.Threading.Channels.Channel.CreateUnbounded<long>();
        private readonly SemaphoreSlim _handled = new(0);
        private DateTimeOffset _at = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        public Exception? Failure { get; set; }

        public async Task SendAsync(long heapBytes)
        {
            await _heaps.Writer.WriteAsync(heapBytes);
            await _handled.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task RunAsync(int processId, TimeSpan interval, Action<HeapSample> onSample, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            try
            {
                while (await _heaps.Reader.WaitToReadAsync(cancellationToken))
                {
                    var heap = await _heaps.Reader.ReadAsync(cancellationToken);
                    _at += interval;
                    onSample(new HeapSample(_at, heap, 0, 0, heap, 0, 0, 1, 0, 0, 0, 1, 0));
                    _handled.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped, like an EventPipe session ending.
            }
        }
    }

    /// <summary>Captures that write a small file and record their label and source.</summary>
    private sealed class RecordingCaptureService(string root, JobTracker jobs) : ICaptureService
    {
        private Task _last = Task.CompletedTask;

        public SessionLibrary Library { get; } = new(root);

        public bool IsHeapScopeElevated => false;

        public CaptureFailedException? Failure { get; set; }

        /// <summary>When set, captures wait for it (or for their cancellation).</summary>
        public TaskCompletionSource? Gate { get; set; }

        public List<TrackedJob<CaptureResult>> Jobs { get; } = [];

        public List<(string? Label, SnapshotSource Source)> Taken { get; } = [];

        public List<SnapshotKindDto> Kinds { get; } = [];


        public Task<IReadOnlyList<ProcessEntry>> ListProcessesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProcessEntry>>([]);

        public TrackedJob<CaptureResult> StartCapture(int processId, string? outputPath = null, string? label = null, SnapshotSource source = SnapshotSource.Manual, SnapshotKindDto kind = SnapshotKindDto.FullDump)
        {
            Kinds.Add(kind);
            var failure = Failure;
            var gate = Gate;
            var job = jobs.Start($"Capture pid {processId}", async (progress, ct) =>
            {
                if (gate is not null)
                {
                    await gate.Task.WaitAsync(ct);
                }

                if (failure is not null)
                {
                    throw failure;
                }

                var path = Path.Combine(Library.Current.Folder, $"app-{Taken.Count}.dmp");
                await File.WriteAllBytesAsync(path, new byte[16], ct);
                Taken.Add((label, source));
                var process = new ProcessEntry(processId, "app", null, "10.0.0", ProcessElevation.NotElevated, false, null, null);
                return new CaptureResult(path, 16, process, Library.AddToCurrent(new SessionSnapshot(Guid.NewGuid(), path, "app", processId, "10.0.0", DateTimeOffset.UtcNow, 16, Source: source, Label: label)));
            });
            _last = job.Result;
            Jobs.Add(job);
            return job;
        }
    }
}
