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
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>
/// Three dumps of one LeakyApi process, leaking a little more before each (sprint 3.3), and one dump of another process.
/// </summary>
public sealed class ComparisonRuns : IAsyncLifetime
{
    public const int ItemsPerRequest = 50;

    private readonly TempFolder _folder = new();

    public string First => _folder.File("first.dmp");

    public string Second => _folder.File("second.dmp");

    public string Third => _folder.File("third.dmp");

    public string OtherProcess => _folder.File("other.dmp");

    public async ValueTask InitializeAsync()
    {
        string[] arguments = ["--Leaks:Enabled=all", $"--Leaks:ItemsPerRequest={ItemsPerRequest}", "--Leaks:PayloadBytes=32"];
        using (var api = await FixtureProcess.StartApiAsync(arguments))
        {
            await api.PostWorkAsync(5, CancellationToken.None);
            api.CaptureDump(First);
            await api.PostWorkAsync(10, CancellationToken.None);
            api.CaptureDump(Second);
            await api.PostWorkAsync(10, CancellationToken.None);
            api.CaptureDump(Third);
        }

        using var other = await FixtureProcess.StartApiAsync(arguments);
        await other.PostWorkAsync(1, CancellationToken.None);
        other.CaptureDump(OtherProcess);
    }

    public ValueTask DisposeAsync()
    {
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Sprint 3.3 (ADR-032): new, dead and survived objects by address, retained bytes in diffs, and trends.</summary>
[Trait("Category", "Integration")]
public sealed class ComparisonTests(ComparisonRuns runs) : IClassFixture<ComparisonRuns>, IAsyncDisposable
{
    private const string Module = "Leaks.dll";

    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private SnapshotSession? _session;

    private SnapshotSession Session => _session ??= new SnapshotSession(_host, new JobTracker(TimeProvider.System));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ItemType => KnownAnswer.Load("StaticCache").Types[0].Type;

    [Fact]
    public async Task New_and_dead_objects_match_clrmds_address_matches_and_open_as_sets_on_the_right_side()
    {
        var baseline = await Session.StartOpen(runs.First).Result;
        var current = await Session.StartOpen(runs.Second).Result;

        var diff = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, null, true, null, Ct, matchObjects: true);
        diff.Result.ObjectsMatched.ShouldBeTrue(diff.Result.IdentityNote);
        var row = (await RetainedSizesTests.AllRowsAsync(diff.Rows)).Single(r => r[TypeDiffColumns.TypeName] == ItemType);

        // ClrMD, independently: an item survived when the earlier dump had an item at its address.
        using var before = DumpAnalysis.Open(runs.First);
        using var after = DumpAnalysis.Open(runs.Second);
        var beforeAddresses = before.Instances(ItemType).Select(o => o.Address).ToHashSet();
        var afterAddresses = after.Instances(ItemType).Select(o => o.Address).ToHashSet();
        var expectedNew = afterAddresses.Count(a => !beforeAddresses.Contains(a));
        var expectedDead = beforeAddresses.Count(a => !afterAddresses.Contains(a));
        (Long(row[TypeDiffColumns.NewObjects]), Long(row[TypeDiffColumns.DeadObjects])).ShouldBe((expectedNew, expectedDead));
        Long(row[TypeDiffColumns.NewObjects]).ShouldBeGreaterThanOrEqualTo(10L * ComparisonRuns.ItemsPerRequest, "every item leaked since is new");

        // The sets: new and survived in the current snapshot, dead in the baseline.
        var type = ObjectSetSpec.OfType(ItemType, Module);
        async Task<long> CountAsync(SnapshotHandle side, ObjectChange change, SnapshotHandle other) =>
            (await Session.QueryTypeSummaryAsync(side, TypeGrouping.Type, null, null, true, false, null, Ct, new ObjectSetQuery(ObjectSetSpec.ComparedOf(type, change), other))).Result.Set!.Objects;
        (await CountAsync(current, ObjectChange.New, baseline)).ShouldBe(expectedNew);
        (await CountAsync(current, ObjectChange.Survived, baseline)).ShouldBe(afterAddresses.Count - expectedNew);
        (await CountAsync(baseline, ObjectChange.Dead, current)).ShouldBe(expectedDead);
    }

    [Fact]
    public async Task Dumps_of_different_processes_are_not_matched_and_say_why()
    {
        var baseline = await Session.StartOpen(runs.First).Result;
        var other = await Session.StartOpen(runs.OtherProcess).Result;

        var diff = await Session.QueryTypeDiffAsync(baseline, other, TypeGrouping.Type, null, null, true, null, Ct, matchObjects: true);

        diff.Result.ObjectsMatched.ShouldBeFalse();
        diff.Result.IdentityNote.ShouldNotBeNull().ShouldContain("different processes");
        diff.Rows.Info.Columns.ShouldNotContain(c => c.Name == TypeDiffColumns.NewObjects);
        (await Should.ThrowAsync<WorkerOperationException>(() => Session.QueryTypeSummaryAsync(other, TypeGrouping.Type, null, null, true, false, null, Ct, new ObjectSetQuery(ObjectSetSpec.ComparedOf(ObjectSetSpec.OfType(ItemType, Module), ObjectChange.New), baseline))))
            .Message.ShouldContain("different processes");
    }

    [Fact]
    public async Task Retained_bytes_and_heap_kinds_in_a_diff_agree_with_each_snapshots_summary()
    {
        var baseline = await Session.StartOpen(runs.First).Result;
        var current = await Session.StartOpen(runs.Second).Result;

        var diff = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, null, true, null, Ct, includeRetained: true);
        var row = (await RetainedSizesTests.AllRowsAsync(diff.Rows)).Single(r => r[TypeDiffColumns.TypeName] == ItemType);

        async Task<long> SummaryRetainedAsync(SnapshotHandle handle) =>
            Long((await RetainedSizesTests.AllRowsAsync((await Session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, includeRetained: true, null, Ct)).Rows))
                .Single(r => r[TypeSummaryColumns.TypeName] == ItemType)[TypeSummaryColumns.Retained]);
        var (before, after) = (await SummaryRetainedAsync(baseline), await SummaryRetainedAsync(current));
        (Long(row[TypeDiffColumns.BaselineRetained]), Long(row[TypeDiffColumns.CurrentRetained]), Long(row[TypeDiffColumns.RetainedDelta])).ShouldBe((before, after, after - before));
        diff.Result.BaselineDominators.ShouldNotBeNull();

        // Per-kind deltas add up to the row's bytes delta.
        var kinds = new[] { TypeDiffColumns.Gen0Delta, TypeDiffColumns.Gen1Delta, TypeDiffColumns.Gen2Delta, TypeDiffColumns.LohDelta, TypeDiffColumns.PohDelta };
        kinds.Sum(k => Long(row[k])).ShouldBe(Long(row[TypeDiffColumns.BytesDelta]));
        diff.Result.KindDeltas.ShouldNotBeNull().Sum(k => k.Bytes).ShouldBe(diff.Result.CurrentBytes - diff.Result.BaselineBytes);
    }

    [Fact]
    public async Task A_trend_of_three_dumps_shows_the_leak_growing_in_every_step()
    {
        var handles = new[] { await Session.StartOpen(runs.First).Result, await Session.StartOpen(runs.Second).Result, await Session.StartOpen(runs.Third).Result };

        var trend = await Session.QueryTypeTrendAsync(handles, TypeGrouping.Type, null, null, true, null, Ct);
        var row = (await RetainedSizesTests.AllRowsAsync(trend.Rows)).Single(r => r[TypeTrendColumns.TypeName] == ItemType);

        var counts = Enumerable.Range(1, 3).Select(i => Long(row[TypeTrendColumns.Objects(i)])).ToList();
        counts.ShouldBe([5L * ComparisonRuns.ItemsPerRequest, 15L * ComparisonRuns.ItemsPerRequest, 25L * ComparisonRuns.ItemsPerRequest]);
        (Long(row[TypeTrendColumns.GrowthSteps]), Long(row[TypeTrendColumns.ObjectsDelta])).ShouldBe((2L, 20L * ComparisonRuns.ItemsPerRequest));
        trend.Result.TotalBytes.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_cli_matches_objects_gates_per_row_and_per_kind_and_shows_a_trend()
    {
        using var diff = ChildProcess.Start(TestPaths.CliExe, "diff", runs.First, runs.Second, "--match-objects", "--filter", "StaticCache");
        (await diff.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(diff.Output);
        diff.Process.ExitCode.ShouldBe(0, diff.Output);
        diff.Output.ShouldContain("matched by address");
        diff.Output.ShouldContain("New");

        using var perRow = ChildProcess.Start(TestPaths.CliExe, "diff", runs.First, runs.Second, "--fail-on-growth", "1KB", "--fail-on-growth-per-row");
        (await perRow.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(perRow.Output);
        perRow.Process.ExitCode.ShouldBe(2, perRow.Output);
        perRow.Output.ShouldContain("rows grew by more than 1KB");

        using var kind = ChildProcess.Start(TestPaths.CliExe, "diff", runs.First, runs.Second, "--fail-on-growth", "100GB", "--growth-of", "gen2");
        (await kind.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(kind.Output);
        kind.Process.ExitCode.ShouldBe(0, kind.Output);

        using var usage = ChildProcess.Start(TestPaths.CliExe, "diff", runs.First, runs.Second, "--fail-on-growth", "10%", "--growth-of", "gen2");
        (await usage.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(usage.Output);
        usage.Process.ExitCode.ShouldBe(64, usage.Output);

        using var trend = ChildProcess.Start(TestPaths.CliExe, "trend", runs.First, runs.Second, runs.Third, "--filter", "StaticCache");
        (await trend.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(trend.Output);
        trend.Process.ExitCode.ShouldBe(0, trend.Output);
        trend.Output.ShouldContain("across 3 dumps");

        using var set = ChildProcess.Start(TestPaths.CliExe, "set", runs.Second, "--type", ItemType, "--module", Module, "--compared-with", runs.First, "--change", "new");
        (await set.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(set.Output);
        set.Process.ExitCode.ShouldBe(0, set.Output);
        set.Output.ShouldContain(ItemType);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static long Long(string? text) => long.Parse(text!, CultureInfo.InvariantCulture);
}
