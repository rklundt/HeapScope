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

using System.Collections.Concurrent;
using HeapScope.Application.Jobs;
using HeapScope.Application.Monitoring;
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using HeapScope.Integration.Tests.Infrastructure;

namespace HeapScope.Integration.Tests;

/// <summary>Sprint 1.7: runtime counters of a real process over EventPipe.</summary>
[Trait("Category", "Integration")]
public sealed class CounterMonitorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Counters_of_LeakyApi_arrive_every_interval_and_the_heap_rises_while_it_leaks()
    {
        using var api = await FixtureProcess.StartApiAsync("--Leaks:Enabled=all", "--Leaks:ItemsPerRequest=2000", "--Leaks:PayloadBytes=512");
        var samples = new ConcurrentQueue<CounterSample>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var monitor = CounterMonitor.RunAsync(api.ProcessId, TimeSpan.FromSeconds(1), samples.Enqueue, stop.Token);

        await WaitUntilAsync(() => samples.Count >= 2);
        var before = samples.Last().GcHeapBytes;
        await api.PostWorkAsync(20, Ct);

        // The heap size counter follows collections; a server GC may not run on its own within the wait (peer review P11).
        await api.CollectGarbageAsync(Ct);
        var arrived = samples.Count;
        await WaitUntilAsync(() => samples.Count >= arrived + 3);
        await stop.CancelAsync();
        await monitor;

        samples.ShouldAllBe(s => s.GcHeapBytes > 0 && s.WorkingSetBytes > 0);
        samples.ShouldContain(s => s.Gen2Bytes > 0 && s.LargeObjectHeapBytes > 0, "the per-generation sizes the chart stacks are published (after the first GC)");
        samples.Max(s => s.GcHeapBytes).ShouldBeGreaterThan(before + (10L * 1024 * 1024), "20 requests of 2,000 items with 512-byte payloads, in each of the five leaks, leak well over 100 MB");
        samples.Sum(s => s.AllocatedBytes).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_growth_rule_captures_exactly_one_dump_of_LeakyApi_into_the_session_as_a_timed_snapshot()
    {
        using var folder = new TempFolder();
        using var api = await FixtureProcess.StartApiAsync("--Leaks:Enabled=all", "--Leaks:ItemsPerRequest=2000", "--Leaks:PayloadBytes=512");
        var capture = new CaptureService(new ProcessDiscovery(), new DumpCapturer(), new SessionLibrary(folder.Path), new JobTracker(TimeProvider.System));
        var rules = new MonitorRules([new GrowthRule(Bytes: 10L * 1024 * 1024, Rearm: RearmMode.Once)]);
        await using var run = new MonitorService(capture).Start(api.ProcessId, new MonitorOptions(TimeSpan.FromSeconds(1), rules));
        var samples = 0;
        run.SampleReceived += (_, _) => Interlocked.Increment(ref samples);

        await WaitUntilAsync(() => Volatile.Read(ref samples) >= 2);
        await api.PostWorkAsync(20, Ct);

        // The rule compares the heap after a GC; a server GC may not run on its own within the wait (peer review P11).
        await api.CollectGarbageAsync(Ct);
        await WaitUntilAsync(() => run.Captures.Count == 1);
        await api.PostWorkAsync(20, Ct);
        var seen = Volatile.Read(ref samples);
        await WaitUntilAsync(() => Volatile.Read(ref samples) >= seen + 3);
        run.Stop();
        await run.Completion;

        var taken = run.Captures.ShouldHaveSingleItem("once: the heap kept growing, no second snapshot");
        taken.Rule.ShouldBe("10 MB over baseline");
        var result = taken.Result.ShouldNotBeNull(taken.Error);
        File.Exists(result.Path).ShouldBeTrue();
        var recorded = capture.Library.Current.Document.Snapshots.ShouldHaveSingleItem();
        (recorded.Source, recorded.Label).ShouldBe((SnapshotSource.Timed, "10 MB over baseline"));
        var timeline = await File.ReadAllLinesAsync(run.TimelinePath.ShouldNotBeNull(), Ct);
        timeline.ShouldContain(r => r.Contains("snapshot: 10 MB over baseline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cli_monitor_prints_samples_captures_on_a_rule_and_reports_errors_and_usage()
    {
        using var api = await FixtureProcess.StartApiAsync("--Leaks:Enabled=all", "--Leaks:ItemsPerRequest=2000", "--Leaks:PayloadBytes=512");
        var pid = api.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var cli = ChildProcess.Start(TestPaths.CliExe, "monitor", "--pid", pid, "--duration", "12", "--growth", "5MB", "--rearm", "once");
        // Leak only after two samples: the baseline is the first sample's rule size (the heap after a GC once one ran, the heap now
        // before), measured once the session is up.
        await WaitUntilAsync(() => System.Text.RegularExpressions.Regex.Matches(cli.Output, @"^\d\d:\d\d:\d\d ", System.Text.RegularExpressions.RegexOptions.Multiline).Count >= 2);
        await api.PostWorkAsync(20, Ct);

        // The rule compares the heap after a GC; a server GC may not run on its own within the 12 s (peer review P11).
        await api.CollectGarbageAsync(Ct);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(cli.Output);

        cli.Process.ExitCode.ShouldBe(0, cli.Output);
        cli.Output.ShouldContain("rules: 5 MB over baseline");
        cli.Output.ShouldMatch(@"Snapshot \(5 MB over baseline\): .+\.dmp");
        cli.Output.ShouldContain("Timeline: ");
        System.Text.RegularExpressions.Regex.Matches(cli.Output, @"^\d\d:\d\d:\d\d ", System.Text.RegularExpressions.RegexOptions.Multiline).Count.ShouldBeGreaterThanOrEqualTo(5, "a line per second");

        using var csv = ChildProcess.Start(TestPaths.CliExe, "monitor", "--pid", pid, "--duration", "3", "--format", "csv");
        (await csv.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(csv.Output);
        csv.Process.ExitCode.ShouldBe(0, csv.Output);
        csv.Output.ShouldContain("time,gc_heap_bytes,");

        foreach (var (arguments, code) in new (string[] Arguments, int Code)[]
        {
            (["monitor", "--pid", "1", "--duration", "2"], 1),
            (["monitor", "--pid", pid, "--every", "soon"], 64),
            (["monitor", "--pid", pid, "--above", "50%"], 64),
            (["monitor", "--pid", pid, "--interval", "0"], 64),
            (["monitor", "--pid", pid, "--rearm", "twice"], 64),
            (["monitor", "--pid", pid, "--above", "0MB"], 64),
            (["monitor", "--pid", pid, "--max", "0"], 64),
            (["monitor"], 64),
        })
        {
            using var bad = ChildProcess.Start(TestPaths.CliExe, arguments);
            (await bad.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(bad.Output);
            bad.Process.ExitCode.ShouldBe(code, string.Join(" ", arguments) + ": " + bad.Output);
        }
    }

    [Fact]
    public async Task A_missing_process_is_a_plain_error()
    {
        var ex = await Should.ThrowAsync<TargetUnavailableException>(() => CounterMonitor.RunAsync(1, TimeSpan.FromSeconds(1), _ => { }, Ct));
        ex.Message.ShouldContain("1");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(100, Ct);
        }
    }
}
