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
        var top = await TreeAsync(handle, []);
        var misses = new List<string>();
        foreach (var name in LeakCatalog.All.Select(l => l.Name))
        {
            var answer = KnownAnswer.Load(name);
            var itemType = answer.Types[0].Type;
            var item = _shared.Dump.Instances(itemType).First();

            // The item's dominators from the top: the row a user starts from, then each row to expand.
            var focus = await _session.QueryDominatorTreeAsync(handle, [], DominatorGroupingDto.None, item.Address, 1, null, Ct);
            await focus.Rows.ReleaseAsync(Ct);
            var chain = focus.Result.Ancestors;
            var collection = chain.Select((a, i) => (a, i)).FirstOrDefault(x => x.a.Name == answer.GrowingCollection).i;
            var clicks = chain.Any(a => a.Name == answer.GrowingCollection) ? collection : chain.Count;
            if (chain.Count == 0 || !top.Any(r => r[DominatorTreeColumns.Key] == chain[0].Key))
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

        misses.ShouldBeEmpty();
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
        var (treeCode, tree) = await RunCliAsync("retainers", _dump, "--depth", "2", "--top", "5", "--format", "json");
        var (tableCode, table) = await RunCliAsync("retainers", _dump, "--group", "type", "--top", "5");
        var (setCode, set) = await RunCliAsync("retained-by", _dump, "Fixtures.Leaks.StaticCacheEntry", "--format", "json");
        var (setTableCode, setTable) = await RunCliAsync("retained-by", _dump, "Fixtures.Leaks.StaticCacheEntry", "--top", "3");
        var (badCode, bad) = await RunCliAsync("retainers", _dump, "--depth", "9");
        var (missingCode, missing) = await RunCliAsync("retained-by", _dump, "Fixtures.Leaks.NoSuchType", "--module", Module);

        treeCode.ShouldBe(0, tree);
        using (var json = Json(tree))
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
        using (var json = Json(set))
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
        var answer = await _session.QueryDominatorTreeAsync(handle, expanded, DominatorGroupingDto.None, 0, 100, null, Ct);
        var rows = await RetainedSizesTests.AllRowsAsync(answer.Rows);
        await answer.Rows.ReleaseAsync(Ct);
        return rows;
    }

    private static JsonDocument Json(string output) =>
        JsonDocument.Parse(output[output.IndexOf('{', StringComparison.Ordinal)..(output.LastIndexOf('}') + 1)]);

    private static async Task<(int Code, string Output)> RunCliAsync(params string[] arguments)
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, arguments);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(180))).ShouldBeTrue(cli.Output);
        return (cli.Process.ExitCode, cli.Output);
    }
}
