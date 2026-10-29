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

using HeapScope.Application.Jobs;
using HeapScope.Application.Monitoring;
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using HeapScope.Integration.Tests.Infrastructure;

namespace HeapScope.Integration.Tests;

/// <summary>Sprint 3.4: the Live workflow against a real process (Force GC, Get snapshot, one capture per target, runs, deletion).</summary>
[Trait("Category", "Integration")]
public sealed class LiveWorkflowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Force_gc_runs_a_full_collection_in_the_target()
    {
        // The runtime's gen-2-gc-count counter is a count per interval: a sample after the forced collection counts it.
        using var api = await FixtureProcess.StartApiAsync("--Leaks:Enabled=none");
        var gen2 = new System.Collections.Concurrent.ConcurrentQueue<long>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var monitor = CounterMonitor.RunAsync(api.ProcessId, TimeSpan.FromSeconds(1), s => gen2.Enqueue(s.Gen2Collections), stop.Token);
        await WaitUntilAsync(() => gen2.Count >= 2);
        var before = gen2.Count;

        var forced = await GcTrigger.CollectAsync(api.ProcessId, TimeSpan.FromSeconds(30), Ct);
        await WaitUntilAsync(() => gen2.Count >= before + 3);
        await stop.CancelAsync();
        try
        {
            await monitor;
        }
        catch (OperationCanceledException)
        {
        }

        forced.Completed.ShouldBeTrue();
        forced.GcNumber.ShouldBeGreaterThan(0);
        gen2.Skip(before).Sum().ShouldBeGreaterThan(0, "the induced collection is a Gen2 one");
    }

    [Fact]
    public async Task A_monitoring_run_takes_a_snapshot_forces_a_gc_refuses_a_second_capture_and_is_recorded()
    {
        using var folder = new TempFolder();
        using var api = await FixtureProcess.StartApiAsync("--Leaks:Enabled=none");
        var library = new SessionLibrary(folder.Path);
        var capture = new CaptureService(new ProcessDiscovery(), new DumpCapturer(), library, new JobTracker(TimeProvider.System));
        await using var run = new MonitorService(capture).Start(api.ProcessId, new MonitorOptions(TimeSpan.FromSeconds(1), MonitorRules.None, ProcessName: "LeakyApi"));
        var samples = 0;
        run.SampleReceived += (_, _) => Interlocked.Increment(ref samples);
        await WaitUntilAsync(() => Volatile.Read(ref samples) >= 1);

        // While one capture of the process runs, a second one and a forced collection are refused (decision 3).
        var first = run.CaptureNowAsync();
        await WaitUntilAsync(() => capture.IsBusy(api.ProcessId) || first.IsCompleted);
        if (!first.IsCompleted)
        {
            var second = await Should.ThrowAsync<CaptureFailedException>(() => capture.StartCapture(api.ProcessId).Result);
            second.Reason.ShouldBe(CaptureFailure.Busy);
            (await run.ForceGcAsync(Ct)).Error.ShouldNotBeNull().ShouldContain("is running");
        }

        var taken = await first;
        taken.Result.ShouldNotBeNull();
        (await run.ForceGcAsync(Ct)).Result.ShouldNotBeNull().Completed.ShouldBeTrue();
        await WaitUntilAsync(() => Volatile.Read(ref samples) >= 3);
        run.Stop();
        await run.Completion;

        var recorded = SessionLibrary.TryRead(run.SessionFolder!)!.Document.Runs.ShouldNotBeNull().ShouldHaveSingleItem();
        (recorded.SnapshotIds.ShouldHaveSingleItem(), recorded.ForcedGcs!.Count, recorded.EndedUtc is not null).ShouldBe((taken.Result.SnapshotId!.Value, 1, true));
        var timeline = await TimelineFile.ReadAsync(Path.Combine(run.SessionFolder!, recorded.Timeline!), Ct);
        timeline.Samples.Count.ShouldBeGreaterThanOrEqualTo(3);
        timeline.Events.Select(e => e.Text).ShouldContain("gc forced");

        // The snapshot is deleted with its index cache, written by opening it once.
        var dump = taken.Result.Path;
        await File.WriteAllBytesAsync(dump + ".hsidx", new byte[16], Ct);
        var deletion = library.DeleteSnapshot(run.SessionFolder!, taken.Result.SnapshotId!.Value);
        deletion.Files.ShouldContain(dump);
        deletion.Files.ShouldContain(dump + ".hsidx");
        File.Exists(dump).ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_records_an_allocation_trace_for_its_whole_length()
    {
        using var folder = new TempFolder();
        using var api = await FixtureProcess.StartApiAsync("--Leaks:Enabled=all", "--Leaks:ItemsPerRequest=500");
        var capture = new CaptureService(new ProcessDiscovery(), new DumpCapturer(), new SessionLibrary(folder.Path), new JobTracker(TimeProvider.System));
        await using var run = new MonitorService(capture).Start(api.ProcessId, new MonitorOptions(TimeSpan.FromSeconds(1), MonitorRules.None, RecordAllocations: true));
        await api.PostWorkAsync(20, Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        run.Stop();
        await run.Completion;

        run.TraceError.ShouldBeNull();
        var trace = run.TracePath.ShouldNotBeNull();
        new FileInfo(trace).Length.ShouldBeGreaterThan(0);
        SessionLibrary.TryRead(run.SessionFolder!)!.Document.Runs!.Single().Trace.ShouldBe(Path.GetFileName(trace));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(50, Ct);
        }
    }
}
