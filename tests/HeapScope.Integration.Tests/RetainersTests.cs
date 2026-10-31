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
using System.Text.Json;
using Fixtures.Leaks;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>Sprint 2.4: the retention explorer through the real worker, on the dump with every leak.</summary>
[Trait("Category", "Integration")]
public sealed class RetainersTests : IClassFixture<AllLeaksDump>, IAsyncDisposable
{
    private const string Module = "Leaks.dll";

    /// <summary>The acceptance bound: expansions from the biggest retainers to a leak's growing collection or its items.</summary>
    private const int MaxClicks = 3;

    private readonly AllLeaksDump _shared;
    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public RetainersTests(AllLeaksDump shared)
    {
        _shared = shared;
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("leaks.dmp");
        File.Copy(shared.DumpPath, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_leak_is_three_expansions_or_fewer_from_the_biggest_retainers()
    {
        var handle = await _session.StartOpen(_dump).Result;

        (await LeakMissesAsync(handle, _shared.Dump)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_LeakyApi_leak_is_three_expansions_or_fewer_from_the_biggest_retainers()
    {
        // The acceptance criterion names LeakyApi (the 2.4 review): an ASP.NET heap, with its container and statics, has
        // many more top-level retainers and deeper chains than the console fixture.
        using var api = await FixtureProcess.StartApiAsync("--Leaks:Enabled=all", "--Leaks:ItemsPerRequest=100", "--Leaks:PayloadBytes=32", "--Leaks:Publishers=64");
        await api.PostWorkAsync(10, Ct);
        var path = _folder.File("api.dmp");
        api.CaptureDump(path);
        using var dump = DumpAnalysis.Open(path);
        var handle = await _session.StartOpen(path).Result;

        (await LeakMissesAsync(handle, dump)).ShouldBeEmpty();
    }

    /// <summary>
    /// For each leak of the catalog: its first item's dominators from the top, the expansions from the biggest retainers to
    /// its growing collection or its items (at most <see cref="MaxClicks"/>), and the rows those expansions show. Returns
    /// what fails.
    /// </summary>
    private async Task<List<string>> LeakMissesAsync(SnapshotHandle handle, DumpAnalysis dump)
    {
        var top = await TreeAsync(handle, []);
        var misses = new List<string>();
        foreach (var name in LeakCatalog.All.Select(l => l.Name))
        {
            var answer = KnownAnswer.Load(name);
            var itemType = answer.Types[0].Type;
            var item = dump.Instances(itemType).First();
            var itemKey = DominatorTreeKeys.Object(item.Address);

            // The item's dominators from the top: the row a user starts from, then each row to expand.
            var focus = await _session.QueryDominatorTreeAsync(handle, [], DominatorGroupingDto.None, item.Address, 1, null, null, Ct);
            await focus.Rows.ReleaseAsync(Ct);
            var chain = focus.Result.Ancestors;
            if (focus.Result.AncestorDepth > chain.Count)
            {
                misses.Add($"{name}: {focus.Result.AncestorDepth} dominators deep");
                continue;
            }

            var collection = chain.Select((a, i) => (a, i)).FirstOrDefault(x => x.a.Name == answer.GrowingCollection).i;
            var clicks = chain.Any(a => a.Name == answer.GrowingCollection) ? collection : chain.Count;
            var start = chain.Count > 0 ? chain[0].Key : itemKey;
            if (!top.Any(r => r[DominatorTreeColumns.Key] == start))
            {
                misses.Add($"{name}: its top dominator is not among the biggest retainers shown");
                continue;
            }

            if (clicks > MaxClicks)
            {
                misses.Add($"{name}: {clicks} expansions ({string.Join(" > ", chain.Select(a => a.Name))})");
                continue;
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{name}: {clicks} expansions: {string.Join(" > ", chain.Select(a => a.Name))} > {itemType}");

            // Expanding those rows, as a user would, shows the collection or the items.
            var rows = await TreeAsync(handle, [.. chain.Take(clicks).Select(a => a.Key)]);
            if (!rows.Any(r => r[DominatorTreeColumns.Depth] == clicks.ToString(CultureInfo.InvariantCulture)
                && (r[DominatorTreeColumns.Name] == answer.GrowingCollection || r[DominatorTreeColumns.Name] == itemType)))
            {
                misses.Add($"{name}: after {clicks} expansions neither {answer.GrowingCollection} nor {itemType} is shown");
            }
        }

        return misses;
    }

    [Fact]
    public async Task The_icicle_of_the_heap_starts_with_the_biggest_retainers_and_its_bars_nest()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var top = await TreeAsync(handle, []);

        var icicle = await _session.QueryIcicleAsync(handle, 0, null, Ct);

        icicle.Total.ShouldBe(icicle.Segments[0].Width);
        var first = icicle.Segments.Where(s => s.Level == 1).OrderBy(s => s.Offset).First();
        first.Key.ShouldBe(top[0][DominatorTreeColumns.Key]);
        first.Width.ShouldBe(long.Parse(top[0][DominatorTreeColumns.Retained]!, CultureInfo.InvariantCulture));
        icicle.Segments.Where(s => s.Level == 1).Sum(s => s.Width).ShouldBeLessThanOrEqualTo(icicle.Total);
        icicle.Segments.ShouldAllBe(s => s.Level == 0 || s.Width >= icicle.Total / 1000);
    }

    [Fact]
    public async Task The_cli_prints_the_biggest_retainers_expanded_and_what_a_type_retains()
    {
        var (treeCode, tree, treeJson) = await RunCliAsync("retainers", _dump, "--depth", "2", "--top", "5", "--format", "json");
        var (tableCode, table, _) = await RunCliAsync("retainers", _dump, "--group", "type", "--top", "5");
        var (setCode, set, setJson) = await RunCliAsync("retained-by", _dump, "Fixtures.Leaks.StaticCacheEntry", "--format", "json");
        var (setTableCode, setTable, _) = await RunCliAsync("retained-by", _dump, "Fixtures.Leaks.StaticCacheEntry", "--top", "3");
        var (badCode, bad, _) = await RunCliAsync("retainers", _dump, "--depth", "9");
        var (missingCode, missing, _) = await RunCliAsync("retained-by", _dump, "Fixtures.Leaks.NoSuchType", "--module", Module);

        treeCode.ShouldBe(0, tree);
        using (var json = Json(treeJson))
        {
            json.RootElement.GetProperty("TotalRetained").GetInt64().ShouldBeGreaterThan(0);
            var depths = json.RootElement.GetProperty("Rows").EnumerateArray().Select(r => r.GetProperty("Depth").GetInt64()).ToList();
            depths.ShouldContain(0);
            depths.ShouldContain(2, "two levels were expanded");
        }

        tableCode.ShouldBe(0, table);
        table.ShouldContain("Biggest retainers");
        table.ShouldContain("Retained per row: exact");

        setCode.ShouldBe(0, set);
        using (var json = Json(setJson))
        {
            var root = json.RootElement;
            root.GetProperty("SetObjects").GetInt64().ShouldBe(AllLeaksDump.Items);
            root.GetProperty("RetainedBytes").GetInt64().ShouldBeGreaterThanOrEqualTo(root.GetProperty("LowerBoundBytes").GetInt64());
        }

        setTableCode.ShouldBe(0, setTable);
        setTable.ShouldContain("exactly; the summary's lower bound is");
        badCode.ShouldBe(64, bad);
        missingCode.ShouldBe(1, missing);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    private async Task<List<Dictionary<string, string?>>> TreeAsync(SnapshotHandle handle, IReadOnlyList<string> expanded)
    {
        var answer = await _session.QueryDominatorTreeAsync(handle, expanded, DominatorGroupingDto.None, 0, 100, null, null, Ct);
        var rows = await RetainedSizesTests.AllRowsAsync(answer.Rows);
        await answer.Rows.ReleaseAsync(Ct);
        return rows;
    }

    private static JsonDocument Json(string output) =>
        JsonDocument.Parse(output[output.IndexOf('{', StringComparison.Ordinal)..(output.LastIndexOf('}') + 1)]);

    private static async Task<(int Code, string Output, string StandardOutput)> RunCliAsync(params string[] arguments)
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, arguments);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(180))).ShouldBeTrue(cli.Output);
        return (cli.Process.ExitCode, cli.Output, cli.StandardOutput);
    }
}
