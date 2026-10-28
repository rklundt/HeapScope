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
using Fixtures.Leaks;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>
/// Two LeakyApi runs for the leak detectors (sprint 2.6): every leak (the five and the detector leaks) and the clean
/// workload alone, each dumped after a warm-up (the baseline) and after more load (the current dump).
/// </summary>
public sealed class DetectorRuns : IAsyncLifetime
{
    public const int ItemsPerRequest = 100;
    public const int WarmUpRequests = 10;
    public const int MoreRequests = 50;

    private readonly TempFolder _folder = new();

    public string LeakyBaseline => _folder.File("leaky-baseline.dmp");

    public string LeakyCurrent => _folder.File("leaky-current.dmp");

    public string CleanBaseline => _folder.File("clean-baseline.dmp");

    public string CleanCurrent => _folder.File("clean-current.dmp");

    /// <summary>Items each leak grew by between the two dumps.</summary>
    public static int GrownItems => MoreRequests * ItemsPerRequest;

    public async ValueTask InitializeAsync()
    {
        await RunAsync(LeakyBaseline, LeakyCurrent, "--Leaks:Enabled=all", "--Leaks:Detected=all", "--Leaks:Publishers=64");
        await RunAsync(CleanBaseline, CleanCurrent, "--Leaks:Enabled=none", "--Clean:Enabled=true");
    }

    public ValueTask DisposeAsync()
    {
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task RunAsync(string baseline, string current, params string[] arguments)
    {
        using var api = await FixtureProcess.StartApiAsync([.. arguments, $"--Leaks:ItemsPerRequest={ItemsPerRequest}", "--Leaks:PayloadBytes=32"]);
        await api.PostWorkAsync(WarmUpRequests, CancellationToken.None);
        api.CaptureDump(baseline);
        await api.PostWorkAsync(MoreRequests, CancellationToken.None);
        api.CaptureDump(current);
    }
}

/// <summary>Sprint 2.6: the leak detectors through the real worker, against the known answers.</summary>
[Trait("Category", "Integration")]
public sealed class DetectorTests : IClassFixture<DetectorRuns>, IAsyncDisposable
{
    private static readonly Dictionary<string, string> s_countColumn = new(StringComparer.Ordinal)
    {
        ["growing-static-collections"] = "Growth",
        ["event-handler-leaks"] = "Subscribers",
        ["timer-leaks"] = "Timers",
        ["disposed-but-referenced"] = "Objects",
    };

    private readonly DetectorRuns _runs;
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;

    public DetectorTests(DetectorRuns runs)
    {
        _runs = runs;
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_detector_fixtures_hold_their_known_items()
    {
        // Raw ClrMD, independent of HeapScope: the fixtures themselves are right.
        using var dump = DumpAnalysis.Open(_runs.LeakyCurrent);
        var items = (DetectorRuns.WarmUpRequests + DetectorRuns.MoreRequests) * DetectorRuns.ItemsPerRequest;
        var reachable = dump.ReachableFromRoots();
        dump.Instances("Fixtures.Leaks.TimerTarget").Count(o => reachable.Contains(o.Address)).ShouldBe(items);
        var disposed = dump.Instances("Fixtures.Leaks.DisposedResource").Where(o => reachable.Contains(o.Address)).ToList();
        disposed.Count.ShouldBe(items);
        disposed.ShouldAllBe(o => o.ReadField<bool>("_disposedValue"));
        dump.Instances("Fixtures.Leaks.UndisposedResource").ShouldAllBe(o => !o.ReadField<bool>("<IsDisposed>k__BackingField"));
    }

    [Fact]
    public async Task Acceptance_every_LeakyApi_leak_is_detected_with_its_known_answer()
    {
        var current = await _session.StartOpen(_runs.LeakyCurrent).Result;
        var baseline = await _session.StartOpen(_runs.LeakyBaseline).Result;
        var answers = new Dictionary<string, List<Dictionary<string, string?>>>(StringComparer.Ordinal);
        var misses = new List<string>();
        foreach (var leak in LeakCatalog.All.Concat(LeakCatalog.Detected))
        {
            foreach (var detection in KnownAnswer.Load(leak.Name).Detections!)
            {
                if (!answers.TryGetValue(detection.Detector, out var rows))
                {
                    rows = await RowsAsync(current, detection.Detector, baseline);
                    answers[detection.Detector] = rows;
                }

                var row = rows.FirstOrDefault(r => r.Values.First() == detection.Key);
                var minimum = (long)(detection.PerItem * DetectorRuns.GrownItems);

                // Never more than every item leaked since the start (review M13): double counting or merged events fail.
                var maximum = (long)(detection.PerItem * (DetectorRuns.WarmUpRequests + DetectorRuns.MoreRequests) * DetectorRuns.ItemsPerRequest);
                if (row is null)
                {
                    misses.Add($"{leak.Name}: {detection.Detector} has no row {detection.Key} (rows: {string.Join(", ", rows.Select(r => r.Values.First()))})");
                }
                else if (long.Parse(row[s_countColumn[detection.Detector]]!, CultureInfo.InvariantCulture) is var count && (count < minimum || count > maximum))
                {
                    misses.Add($"{leak.Name}: {detection.Detector} counts {count} for {detection.Key}, outside {minimum} to {maximum}");
                }
            }
        }

        misses.ShouldBeEmpty();
    }

    [Fact]
    public async Task Acceptance_no_detector_fires_on_a_clean_LeakyApi_run_with_or_without_a_baseline()
    {
        KnownAnswer.Load("Clean").Detections.ShouldBeEmpty();
        var current = await _session.StartOpen(_runs.CleanCurrent).Result;
        var baseline = await _session.StartOpen(_runs.CleanBaseline).Result;
        var fired = new List<string>();
        foreach (var detector in (await _session.ListInspectionsAsync(current, Ct)).Where(i => i.Kind == InspectionKinds.Leak))
        {
            detector.Unavailable.ShouldBeNull(detector.Name);
            foreach (var withBaseline in new SnapshotHandle?[] { baseline, null })
            {
                var rows = await RowsAsync(current, detector.Name, withBaseline);
                fired.AddRange(rows.Select(r => $"{detector.Name}{(withBaseline is null ? string.Empty : " (baseline)")}: {string.Join(" | ", r.Values)}"));
            }
        }

        fired.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cli_inspect_with_a_baseline_names_the_growing_collections()
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, "inspect", _runs.LeakyCurrent, "growing-static-collections", "--baseline", _runs.LeakyBaseline, "--all");
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(0, cli.Output);
        cli.Output.ShouldContain("Fixtures.Leaks.StaticCacheLeak.s_cache");
        cli.Output.ShouldContain("Growth");

        using var summary = ChildProcess.Start(TestPaths.CliExe, "inspect", _runs.LeakyCurrent);
        (await summary.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(summary.Output);
        summary.Process.ExitCode.ShouldBe(0, summary.Output);
        summary.Output.ShouldContain("timer-leaks");
        summary.Output.ShouldContain("event-handler-leaks");
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_detector_row_opens_exactly_the_objects_it_counted_as_a_set()
    {
        // Sprint 3.1 (2.6 review M24): the disposed-but-referenced row is its disposed objects, not every reachable one.
        var current = await _session.StartOpen(_runs.LeakyCurrent).Result;
        var rows = await RowsAsync(current, "disposed-but-referenced", null);
        var index = rows.FindIndex(r => r["Type"] == "Fixtures.Leaks.DisposedResource");
        index.ShouldBeGreaterThanOrEqualTo(0);
        var counted = long.Parse(rows[index]["Objects"]!, CultureInfo.InvariantCulture);

        var set = await _session.QueryInstancesAsync(current, new InstancesQuery(string.Empty, null, Set: new ObjectSetQuery(ObjectSetSpec.OfInspectionRow("disposed-but-referenced", index))), null, Ct);

        set.Result.TotalObjects.ShouldBe(counted);
        set.Result.MatchedTypes.ShouldBe(1);

        // An event row opens its subscriber delegates and their targets (the 3.1 review H1): the row's bytes, not the
        // shared multicast delegates.
        var events = await RowsAsync(current, "event-handler-leaks", null);
        var eventRow = events.FindIndex(r => long.Parse(r["Subscribers"]!, CultureInfo.InvariantCulture) > 0);
        eventRow.ShouldBeGreaterThanOrEqualTo(0);
        var eventSet = await _session.QueryInstancesAsync(current, new InstancesQuery(string.Empty, null, Set: new ObjectSetQuery(ObjectSetSpec.OfInspectionRow("event-handler-leaks", eventRow))), null, Ct);
        eventSet.Result.TotalObjects.ShouldBeGreaterThanOrEqualTo(long.Parse(events[eventRow]["Subscribers"]!, CultureInfo.InvariantCulture), "at least one delegate per subscriber");
        eventSet.Result.TotalBytes.ShouldBeLessThanOrEqualTo(long.Parse(events[eventRow]["Bytes"]!, CultureInfo.InvariantCulture), "targets shared by several subscribers count once in the set");
    }

    private async Task<List<Dictionary<string, string?>>> RowsAsync(SnapshotHandle snapshot, string detector, SnapshotHandle? baseline)
    {
        var run = await _session.RunInspectionAsync(snapshot, detector, null, Ct, baseline);
        var rows = await RetainedSizesTests.AllRowsAsync(run.Rows);
        await run.Rows.ReleaseAsync(Ct);
        return rows;
    }
}
