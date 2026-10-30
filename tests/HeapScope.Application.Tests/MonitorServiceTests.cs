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
    public async Task A_run_is_recorded_in_its_session_with_its_snapshots_forced_collections_and_allocation_trace()
    {
        // Sprint 3.4 (decisions 2, 4, 5): Get snapshot, Force GC and Record allocations within one run.
        string? tracedTo = null;
        var traceStopped = new TaskCompletionSource();
        async Task<TraceResult> Recorder(int pid, string path, CancellationToken ct)
        {
            tracedTo = path;
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            await File.WriteAllBytesAsync(path, new byte[64], CancellationToken.None);
            traceStopped.SetResult();
            return new TraceResult(path, 64, TimeSpan.FromSeconds(1), StoppedEarly: true);
        }

        var service = new MonitorService(_capture, _feed, allocations: Recorder);
        var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb)]), RecordAllocations: true, ProcessName: "app"));
        var folder = run.SessionFolder.ShouldNotBeNull();
        SessionLibrary.TryRead(folder)!.Document.Runs.ShouldNotBeNull().ShouldHaveSingleItem().EndedUtc.ShouldBeNull("recorded when it starts");
        await _feed.SendAsync(100 * Mb);

        var manual = await run.CaptureNowAsync();
        var forced = await run.ForceGcAsync(Ct);
        await _feed.SendAsync(600 * Mb);
        await run.CaptureTask;
        run.Stop();
        await run.Completion;
        await traceStopped.Task;

        (manual.Rule, manual.Result.ShouldNotBeNull().SnapshotId).ShouldBe(("manual", _capture.Library.Current.Document.Snapshots[0].Id));
        forced.Result.ShouldNotBeNull().Completed.ShouldBeTrue();
        _capture.ForcedGcs.ShouldBe([42]);
        _capture.Taken.Select(t => t.Source).ShouldBe([SnapshotSource.Manual, SnapshotSource.Timed]);
        var recorded = SessionLibrary.TryRead(folder)!.Document.Runs.ShouldNotBeNull().ShouldHaveSingleItem();
        (recorded.Id, recorded.ProcessName, recorded.ProcessId, recorded.Rules).ShouldBe((run.Id, "app", 42, "above 500 MB"));
        recorded.EndedUtc.ShouldNotBeNull();
        recorded.SnapshotIds.Count.ShouldBe(2);
        recorded.ForcedGcs.ShouldNotBeNull().Count.ShouldBe(1);
        (recorded.Timeline, recorded.Trace).ShouldBe((Path.GetFileName(run.TimelinePath), Path.GetFileName(tracedTo)));
        run.TracePath.ShouldBe(tracedTo);

        // The timeline reads back with its samples and its events (snapshots and the forced collection).
        var timeline = await TimelineFile.ReadAsync(run.TimelinePath!, Ct);
        timeline.Samples.Count.ShouldBe(2);
        timeline.Events.Select(e => e.Text).ShouldBe(["snapshot: manual: app-0.dmp", "gc forced", "snapshot: above 500 MB: app-1.dmp"]);
        timeline.SkippedLines.ShouldBe(0);
    }

    [Fact]
    public async Task A_busy_target_takes_no_rule_snapshot_and_refuses_a_forced_collection()
    {
        // Sprint 3.4 (decision 3): a capture of the process from elsewhere counts as running.
        var service = new MonitorService(_capture, _feed);
        var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb)])));
        _capture.Busy.Add(42);

        await _feed.SendAsync(600 * Mb);
        var forced = await run.ForceGcAsync(Ct);
        _capture.Busy.Clear();
        await _feed.SendAsync(100 * Mb);
        await _feed.SendAsync(700 * Mb);
        await run.CaptureTask;
        run.Stop();
        await run.Completion;

        forced.Error.ShouldNotBeNull().ShouldContain("running");
        run.Captures.ShouldHaveSingleItem("only once the target was free");
        (await File.ReadAllTextAsync(run.TimelinePath!, Ct)).ShouldContain("gc not forced:");
    }

    [Fact]
    public async Task Stop_waits_for_every_capture_refuses_new_ones_and_writes_nothing_after_the_run_closed()
    {
        // Review H6 of 3.4: a rule capture and a Get snapshot in flight together are both awaited and recorded.
        _capture.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MonitorService(_capture, _feed);
        var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb)])));
        var folder = run.SessionFolder.ShouldNotBeNull();
        _capture.Library.InUseBy(folder).ShouldBe("a monitoring run");
        await _feed.SendAsync(600 * Mb);
        var manual = run.CaptureNowAsync();
        run.Stop();
        await Task.Delay(200, Ct);

        run.Completion.IsCompleted.ShouldBeFalse("the run's end waits for both captures");
        (await run.CaptureNowAsync()).Error.ShouldBe("The run has stopped.");
        (await run.ForceGcAsync(Ct)).Error.ShouldBe("The run has stopped.");
        _capture.Gate.SetResult();
        await run.Completion;

        (await manual).Result.ShouldNotBeNull();
        SessionLibrary.TryRead(folder)!.Document.Runs!.Single().SnapshotIds.Count.ShouldBe(2);
        Directory.GetFiles(folder, "counters-*.csv").ShouldHaveSingleItem("no second timeline after the run closed it");
        _capture.Library.InUseBy(folder).ShouldBeNull();
        _capture.ForcedGcs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rule_that_lost_the_race_for_the_process_is_rearmed_and_a_deferred_rule_is_logged_once()
    {
        // Review H5 of 3.4.
        var logger = new RecordingLogger<MonitorService>();
        var service = new MonitorService(_capture, _feed, logger);
        var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), new MonitorRules([new AboveRule(500 * Mb, RearmMode.Once)])));

        _capture.FailureOnce = new CaptureFailedException(CaptureFailure.Busy, "A capture of process 42 is running.", null);
        await _feed.SendAsync(600 * Mb);
        await run.CaptureTask;
        _capture.Busy.Add(42);
        await _feed.SendAsync(650 * Mb);
        await _feed.SendAsync(660 * Mb);
        _capture.Busy.Clear();
        await _feed.SendAsync(670 * Mb);
        await run.CaptureTask;
        await _feed.SendAsync(680 * Mb);
        run.Stop();
        await run.Completion;

        run.Captures.Select(c => c.Result is not null).ShouldBe([false, true], "the once rule fired again after losing the race, then retired");
        logger.Lines.Count(l => l.Contains("is re-armed", StringComparison.Ordinal)).ShouldBe(1);
        logger.Lines.Count(l => l.Contains("above 500 MB is due", StringComparison.Ordinal) && l.Contains("from elsewhere", StringComparison.Ordinal)).ShouldBe(1, "once per busy stretch");
    }

    [Fact]
    public async Task The_allocation_trace_stops_at_its_limit_and_the_run_goes_on()
    {
        // Review H7 of 3.4.
        static async Task<TraceResult> Recorder(int pid, string path, CancellationToken ct)
        {
            // The real recorder's naming (3.8 review H1): the watch must find what TraceRecorder writes.
            var partial = HeapScope.Capture.TraceRecorder.PartialName(path);
            await File.WriteAllBytesAsync(partial, new byte[4096], CancellationToken.None);
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            File.Move(partial, path);
            return new TraceResult(path, 4096, TimeSpan.FromSeconds(1), StoppedEarly: true);
        }

        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MonitorService(_capture, _feed, allocations: Recorder);
        var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), MonitorRules.None, RecordAllocations: true, TraceLimitBytes: 1024), r => r.TraceStopped += (_, m) => stopped.TrySetResult(m));

        (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldContain("limit of 0 MB");
        await _feed.SendAsync(100 * Mb);
        run.Stop();
        await run.Completion;

        run.TracePath.ShouldNotBeNull("what it recorded is kept");
        var recorded = SessionLibrary.TryRead(run.SessionFolder!)!.Document.Runs!.Single();
        (recorded.Trace, recorded.TraceNote).ShouldBe((Path.GetFileName(run.TracePath), run.TraceError));
    }

    [Fact]
    public async Task A_run_disposed_while_its_trace_watch_waits_on_a_capture_ends_cleanly()
    {
        // Peer review P11: the watch is awaited before its cancellation source is disposed, so stopping at the limit while
        // a capture holds the trace's stop never touches a disposed source.
        static async Task<TraceResult> Recorder(int pid, string path, CancellationToken ct)
        {
            // The real recorder's naming (3.8 review H1): the watch must find what TraceRecorder writes.
            var partial = HeapScope.Capture.TraceRecorder.PartialName(path);
            await File.WriteAllBytesAsync(partial, new byte[4096], CancellationToken.None);
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            File.Move(partial, path);
            return new TraceResult(path, 4096, TimeSpan.FromSeconds(1), StoppedEarly: true);
        }

        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            _capture.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new MonitorService(_capture, _feed, allocations: Recorder);
            var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), MonitorRules.None, RecordAllocations: true, TraceLimitBytes: 1024), r => r.TraceStopped += (_, m) => stopped.TrySetResult(m));
            var capture = run.CaptureNowAsync();
            (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldContain("limit");

            var disposing = run.DisposeAsync().AsTask();
            await Task.Delay(100, Ct);
            _capture.Gate.SetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await capture;

            run.TracePath.ShouldNotBeNull("what it recorded is kept");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            unobserved.ShouldBe(0);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task A_refused_trace_is_reported_at_once_and_an_unconfirmed_collection_is_not_marked()
    {
        // Reviews of 3.4: the refusal arrives as a faulted task; a collection whose end was not seen is no marker.
        var stopped = new List<string>();
        var service = new MonitorService(_capture, _feed, allocations: (_, _, _) => Task.FromException<TraceResult>(new CaptureFailedException(CaptureFailure.NeedsAdministrator, "Run HeapScope as administrator.", null)));
        var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), MonitorRules.None, WriteTimeline: false, RecordAllocations: true), r => r.TraceStopped += (_, m) => stopped.Add(m));

        stopped.ShouldHaveSingleItem().ShouldContain("administrator", Case.Insensitive);
        _capture.ForcedGcIncomplete = true;
        var forced = await run.ForceGcAsync(Ct);
        run.Stop();
        await run.Completion;

        forced.Result.ShouldNotBeNull().Outcome.ShouldBe("no induced collection was seen");
        var recorded = SessionLibrary.TryRead(run.SessionFolder.ShouldNotBeNull("a run is recorded without a timeline too"))!.Document.Runs!.Single();
        (recorded.Timeline, recorded.Trace, recorded.ForcedGcs!.Count).ShouldBe((null, null, 0));
        recorded.TraceNote.ShouldNotBeNull().ShouldContain("administrator", Case.Insensitive);
    }

    [Fact]
    public async Task A_run_that_records_nothing_leaves_the_session_library_alone()
    {
        // Sprint 3.4 re-review: smoke runs never write into the user's sessions.
        var service = new MonitorService(_capture, _feed);
        var run = service.Start(42, new MonitorOptions(TimeSpan.FromSeconds(1), MonitorRules.None, WriteTimeline: false, RecordRun: false));
        await _feed.SendAsync(100 * Mb);
        run.Stop();
        await run.Completion;

        (run.SessionFolder, run.TimelinePath).ShouldBe((null, null));
        _capture.Library.HasCurrent.ShouldBeFalse("no session was created");
        Directory.Exists(_capture.Library.Root).ShouldBeFalse();
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
        private int _started;

        public SessionLibrary Library { get; } = new(root);

        public string? UnavailableReason => null;

        public bool IsHeapScopeElevated => false;

        public CaptureFailedException? Failure { get; set; }

        /// <summary>A failure for the next capture only (sprint 3.4 review: a race lost to another capture).</summary>
        public CaptureFailedException? FailureOnce { get; set; }

        /// <summary>Forced collections whose start is never seen (sprint 3.4 review).</summary>
        public bool ForcedGcIncomplete { get; set; }

        /// <summary>When set, captures wait for it (or for their cancellation).</summary>
        public TaskCompletionSource? Gate { get; set; }

        public List<TrackedJob<CaptureResult>> Jobs { get; } = [];

        public List<(string? Label, SnapshotSource Source)> Taken { get; } = [];

        public List<SnapshotKindDto> Kinds { get; } = [];

        /// <summary>Processes the fake reports busy (sprint 3.4).</summary>
        public HashSet<int> Busy { get; } = [];

        /// <summary>Forced collections asked for (sprint 3.4).</summary>
        public List<int> ForcedGcs { get; } = [];

        public bool IsBusy(int processId) => Busy.Contains(processId) || Jobs.Any(j => !j.Result.IsCompleted);

        public Task<ForcedGcResult> ForceGcAsync(int processId, CancellationToken cancellationToken)
        {
            if (IsBusy(processId))
            {
                throw new CaptureFailedException(CaptureFailure.Busy, "A capture of this process is running.", null);
            }

            ForcedGcs.Add(processId);
            return Task.FromResult(ForcedGcIncomplete ? new ForcedGcResult(processId, false, TimeSpan.FromSeconds(30)) : new ForcedGcResult(processId, true, TimeSpan.FromMilliseconds(5), 7));
        }

        public Task<IReadOnlyList<ProcessEntry>> ListProcessesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProcessEntry>>([]);

        public TrackedJob<CaptureResult> StartCapture(int processId, string? outputPath = null, string? label = null, SnapshotSource source = SnapshotSource.Manual, SnapshotKindDto kind = SnapshotKindDto.FullDump)
        {
            Kinds.Add(kind);
            var failure = FailureOnce ?? Failure;
            FailureOnce = null;
            var number = Interlocked.Increment(ref _started) - 1;
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

                var path = Path.Combine(Library.Current.Folder, $"app-{number}.dmp");
                await File.WriteAllBytesAsync(path, new byte[16], ct);
                lock (Taken)
                {
                    Taken.Add((label, source));
                }
                var process = new ProcessEntry(processId, "app", null, "10.0.0", ProcessElevation.NotElevated, false, null, null);
                var id = Guid.NewGuid();
                return new CaptureResult(path, 16, process, Library.AddToCurrent(new SessionSnapshot(id, path, "app", processId, "10.0.0", DateTimeOffset.UtcNow, 16, Source: source, Label: label)), SnapshotId: id);
            });
            _last = job.Result;
            Jobs.Add(job);
            return job;
        }
    }
}
