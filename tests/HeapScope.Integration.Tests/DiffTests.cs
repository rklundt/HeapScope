using System.Globalization;
using Fixtures.Leaks;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>Two dumps of one live LeakyApi with every leak on, the second after 1,000 more items per leak.</summary>
public sealed class LeakyApiPair : IAsyncLifetime
{
    public const int ItemsPerRequest = 100;
    public const int FirstRequests = 5;
    public const int MoreRequests = 10;
    public const int Growth = MoreRequests * ItemsPerRequest;

    private readonly TempFolder _folder = new();

    public string Baseline { get; private set; } = null!;

    public string Current { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Baseline = _folder.File("baseline.dmp");
        Current = _folder.File("current.dmp");
        using var api = await FixtureProcess.StartApiAsync(
            "--Leaks:Enabled=all", $"--Leaks:ItemsPerRequest={ItemsPerRequest}", "--Leaks:PayloadBytes=32", "--Leaks:Publishers=64");
        await api.PostWorkAsync(FirstRequests, ct);
        api.CaptureDump(Baseline);
        await api.PostWorkAsync(MoreRequests, ct);
        api.CaptureDump(Current);
    }

    public ValueTask DisposeAsync()
    {
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Sprint 1.4: snapshot diffs through the worker, the session and the CLI, against the known answers.</summary>
[Trait("Category", "Integration")]
public sealed class DiffTests(LeakyApiPair pair) : IClassFixture<LeakyApiPair>, IAsyncDisposable
{
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());

    private SnapshotSession? _session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SnapshotSession Session => _session ??= new SnapshotSession(_host, new JobTracker(TimeProvider.System));

    [Fact]
    public async Task Every_leak_grows_by_its_known_answers_and_its_item_type_leads_its_fixture_types()
    {
        var (baseline, current) = await OpenBothAsync();

        var byCount = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, TypeDiffColumns.ObjectsDelta, true, null, Ct);
        var rows = (await AllRowsAsync(byCount.Rows)).ToDictionary(r => r[0]!, StringComparer.Ordinal);

        // Exact census types grow by items times perItem, summed over the leaks that share a type (as in LeakyApiGrowthTests).
        var census = LeakCatalog.All.SelectMany(l => KnownAnswer.Load(l.Name).Census!.Select(c => (Leak: l.Name, Entry: c))).ToList();
        var mismatches = new List<string>();
        foreach (var group in census.GroupBy(c => c.Entry.Type))
        {
            var expected = (long)LeakyApiPair.Growth * group.Sum(c => c.Entry.PerItem);
            var delta = rows.TryGetValue(group.Key, out var row) ? long.Parse(row[4]!, CultureInfo.InvariantCulture) : 0;
            var ok = group.All(c => c.Entry.Exact) ? delta == expected : delta >= expected;
            if (!ok)
            {
                mismatches.Add($"{group.Key}: delta {delta}, expected {expected}");
            }
        }

        mismatches.ShouldBeEmpty();

        // Each leak's item type (types[0]) has the largest count growth among its own fixture types.
        foreach (var leak in LeakCatalog.All)
        {
            var answer = KnownAnswer.Load(leak.Name);
            var fixtureDeltas = answer.Census!.Where(c => c.Type.StartsWith("Fixtures.", StringComparison.Ordinal))
                .Select(c => long.Parse(rows[c.Type][4]!, CultureInfo.InvariantCulture));
            long.Parse(rows[answer.Types[0].Type][4]!, CultureInfo.InvariantCulture).ShouldBe(fixtureDeltas.Max(), leak.Name);
        }

        // Rows sum to the snapshot totals, and the diff is ordered by count growth.
        var result = byCount.Result;
        rows.Values.Sum(r => long.Parse(r[4]!, CultureInfo.InvariantCulture)).ShouldBe(result.CurrentObjects - result.BaselineObjects);
        rows.Values.Sum(r => long.Parse(r[7]!, CultureInfo.InvariantCulture)).ShouldBe(result.CurrentBytes - result.BaselineBytes);
        result.MatchingBytesDelta.ShouldBe(result.CurrentBytes - result.BaselineBytes);
        result.BaselineObjects.ShouldBe(baseline.Descriptor.ObjectCount);
        result.CurrentObjects.ShouldBe(current.Descriptor.ObjectCount);

        // By bytes growth the payload arrays come first.
        var byBytes = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, null, true, null, Ct);
        (await byBytes.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0].ShouldBe("System.Byte[]");
    }

    [Fact]
    public async Task A_diff_filters_and_groups_and_refuses_an_unknown_sort_column()
    {
        var (baseline, current) = await OpenBothAsync();

        var leaks = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Namespace, "Fixtures.Leaks", TypeDiffColumns.Name, false, null, Ct);
        var rows = await AllRowsAsync(leaks.Rows);
        rows.Select(r => r[0]).ShouldContain("Fixtures.Leaks");
        rows.ShouldAllBe(r => NameFilter.IsMatch(r[0]!, "Fixtures.Leaks"));
        leaks.Result.MatchingBytesDelta.ShouldBe(rows.Sum(r => long.Parse(r[7]!, CultureInfo.InvariantCulture)));
        leaks.Result.MatchingBytesDelta.ShouldBeGreaterThan(0);

        var ex = await Should.ThrowAsync<WorkerOperationException>(
            () => Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, "Size", true, null, Ct));
        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task A_diff_survives_a_worker_restart_by_reopening_both_snapshots()
    {
        var (baseline, current) = await OpenBothAsync();
        var diff = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, null, true, null, Ct);
        var first = (await diff.Rows.GetRowsAsync(0, 5, Ct)).Rows.Select(r => r[0]).ToList();

        await _host.RestartAsync(Ct);

        (await diff.Rows.GetRowsAsync(0, 5, Ct)).Rows.Select(r => r[0]).ShouldBe(first);
        diff.Rows.Requeries.ShouldBe(1);
        baseline.Descriptor.FromCache.ShouldBeTrue();
        current.Descriptor.FromCache.ShouldBeTrue();
    }

    [Fact]
    public async Task A_snapshot_the_worker_lost_is_reopened_and_an_unknown_one_is_refused()
    {
        var (baseline, current) = await OpenBothAsync();

        // The worker forgets the current snapshot (same generation): the session reopens it and runs the diff again.
        await _host.InvokeAsync((service, ct) => service.CloseSnapshotAsync(current.Descriptor.SnapshotId, ct), Ct);
        var diff = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, "StaticCacheEntry", null, true, null, Ct);
        (await AllRowsAsync(diff.Rows)).Single(r => r[0] == "Fixtures.Leaks.StaticCacheEntry")[4]
            .ShouldBe(LeakyApiPair.Growth.ToString(CultureInfo.InvariantCulture));

        var unknown = await Should.ThrowAsync<WorkerOperationException>(() => _host.InvokeAsync(
            (service, ct) => service.QueryTypeDiffAsync(new TypeDiffRequest(baseline.Descriptor.SnapshotId, Guid.NewGuid()), null, ct), Ct));
        unknown.ErrorCode.ShouldBe(HeapScopeErrorCode.SnapshotNotLoaded);

        await Session.CloseAsync(current, Ct);
        await Should.ThrowAsync<Exception>(() => Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, null, true, null, Ct));
    }

    [Fact]
    public async Task Cli_diff_prints_formats_and_exits_by_the_growth_threshold()
    {
        await OpenBothAsync(); // writes the index caches, so the CLI runs reopen quickly

        var table = await RunCliAsync("diff", pair.Baseline, pair.Current, "--top", "5");
        table.Code.ShouldBe(0, table.Output);
        table.Output.ShouldContain("System.Byte[]");
        table.Output.ShouldContain("unreachable ones included");

        var json = await RunCliAsync("diff", pair.Baseline, pair.Current, "--format", "json", "--filter", "StaticCacheEntry", "--sort", "count-delta");
        json.Code.ShouldBe(0, json.Output);
        using (var document = System.Text.Json.JsonDocument.Parse(json.Output[json.Output.IndexOf('{', StringComparison.Ordinal)..(json.Output.LastIndexOf('}') + 1)]))
        {
            var row = document.RootElement.GetProperty("Rows")[0];
            row.GetProperty("Name").GetString().ShouldBe("Fixtures.Leaks.StaticCacheEntry");
            row.GetProperty("ObjectsDelta").GetInt64().ShouldBe(LeakyApiPair.Growth);
        }

        var csv = await RunCliAsync("diff", pair.Baseline, pair.Current, "--format", "csv", "--top", "2");
        csv.Code.ShouldBe(0, csv.Output);
        csv.Output.ShouldContain("Name,Change,BaselineObjects,CurrentObjects,ObjectsDelta,BaselineBytes,CurrentBytes,BytesDelta");

        (await RunCliAsync("diff", pair.Baseline, pair.Current, "--fail-on-growth", "1GB")).Code.ShouldBe(0);
        var grew = await RunCliAsync("diff", pair.Baseline, pair.Current, "--fail-on-growth", "1KB");
        grew.Code.ShouldBe(2, grew.Output);
        grew.Output.ShouldContain("more than 1KB");
        (await RunCliAsync("diff", pair.Baseline, pair.Current, "--filter", "StaticCacheEntry", "--fail-on-growth", "1000%")).Code.ShouldBe(0);
        (await RunCliAsync("diff", pair.Baseline, pair.Current, "--filter", "StaticCacheEntry", "--fail-on-growth", "10%")).Code.ShouldBe(2);

        // A gate whose filter matches nothing fails instead of passing forever.
        var nothing = await RunCliAsync("diff", pair.Baseline, pair.Current, "--filter", "StaticCacheEntryTypo", "--fail-on-growth", "1GB");
        nothing.Code.ShouldBe(1, nothing.Output);
        nothing.Output.ShouldContain("matches no rows");
    }

    [Fact]
    public async Task Cli_diff_reports_errors_and_usage_with_their_own_exit_codes()
    {
        var missing = await RunCliAsync("diff", pair.Baseline, pair.Baseline + ".missing.dmp");
        missing.Code.ShouldBe(1, missing.Output);
        missing.Output.ShouldContain("does not exist");

        (await RunCliAsync("diff", pair.Baseline, pair.Current, "--sort", "size")).Code.ShouldBe(64);
        (await RunCliAsync("diff", pair.Baseline, pair.Current, "--fail-on-growth", "lots")).Code.ShouldBe(64);
        (await RunCliAsync("diff", pair.Baseline, pair.Current, "--top", "0")).Code.ShouldBe(64);
        (await RunCliAsync("diff", pair.Baseline, pair.Current, "--top", "3", "--all")).Code.ShouldBe(64);
        (await RunCliAsync("diff", pair.Baseline)).Code.ShouldBe(64);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<(SnapshotHandle Baseline, SnapshotHandle Current)> OpenBothAsync() =>
        (await Session.StartOpen(pair.Baseline).Result, await Session.StartOpen(pair.Current).Result);

    private static async Task<(int Code, string Output)> RunCliAsync(params string[] arguments)
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, arguments);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(180))).ShouldBeTrue(cli.Output);
        return (cli.Process.ExitCode, cli.Output);
    }

    private static async Task<List<IReadOnlyList<string?>>> AllRowsAsync(ResultSetHandle rows)
    {
        var all = new List<IReadOnlyList<string?>>();
        while (all.Count < rows.Info.TotalCount)
        {
            all.AddRange((await rows.GetRowsAsync(all.Count, WorkerProtocol.MaxPageSize, Ct)).Rows);
        }

        return all;
    }
}
