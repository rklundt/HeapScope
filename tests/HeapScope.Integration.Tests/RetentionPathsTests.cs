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

/// <summary>
/// Sprint 2.2 acceptance: each rooted known answer's item is alive along its path shape from its root kind; a type's key
/// paths name the leak's root; late garbage is unreachable; dependent handles are labeled.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RetentionPathsTests : IClassFixture<AllLeaksDump>, IAsyncDisposable
{
    private const string Module = "Leaks.dll";
    private const string StaticPrefix = "Static field ";

    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public RetentionPathsTests(AllLeaksDump shared)
    {
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("leaks.dmp");
        File.Copy(shared.DumpPath, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<string> Rooted => LeakCatalog.All.Select(l => l.Name).Concat(["ConditionalWeakTable", "EmbeddedStruct"]);

    [Fact]
    public async Task Every_rooted_known_answer_item_is_alive_along_its_path_shape_from_its_root()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var mismatches = new List<string>();
        foreach (var name in Rooted)
        {
            var answer = KnownAnswer.Load(name);
            var root = answer.PrimaryRoot;
            var item = root.ItemType ?? answer.Types[0].Type;
            var address = await FirstAddressAsync(handle, item);

            var paths = await _session.QueryObjectPathsAsync(handle, address, 10, null, Ct);

            var shapes = paths.RootNodeIds.Select(id => (Root: paths.Nodes[id].Root!, Path: PathTypes(paths, id))).ToList();
            var matched = shapes.Any(s => s.Root.Kind.ToString() == root.Kind
                && s.Root.Description.StartsWith(StaticPrefix, StringComparison.Ordinal)
                && KnownAnswer.MatchesShape(answer.PathShape!, s.Root.Description[StaticPrefix.Length..], s.Path));
            if (!matched)
            {
                mismatches.Add($"{name}: none of {string.Join(" | ", shapes.Select(s => s.Root.Description + ": " + string.Join(" > ", s.Path)))} matches {string.Join(" > ", answer.PathShape!)}");
            }
        }

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_leaks_key_paths_name_its_root_for_every_item_and_late_garbage_is_unreachable()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var mismatches = new List<string>();
        foreach (var name in LeakCatalog.All.Select(l => l.Name))
        {
            var answer = KnownAnswer.Load(name);
            var field = $"{StaticPrefix}{answer.PrimaryRoot.DeclaringType}.{answer.PrimaryRoot.Field}";
            var paths = await _session.QueryTypePathsAsync(handle, answer.Types[0].Type, Module, 100, null, Ct);
            var viaRoot = paths.Paths.Where(p => p.Root.Description == field).Sum(p => p.Objects);
            if (viaRoot != AllLeaksDump.Items || paths.Unreachable != 0)
            {
                mismatches.Add($"{name}: {viaRoot} of {paths.Objects} items along paths from {field}, {paths.Unreachable} unreachable");
            }
        }

        foreach (var type in KnownAnswer.Load("LateGarbage").PresentButUnreachable!)
        {
            var paths = await _session.QueryTypePathsAsync(handle, type, Module, 20, null, Ct);
            (paths.Objects, paths.Unreachable, paths.Paths.Count).ShouldBe((AllLeaksDump.LateGarbageItems, (long)AllLeaksDump.LateGarbageItems, 0), type);
        }

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_conditional_weak_table_value_is_held_through_its_key_by_a_dependent_handle()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var address = await FirstAddressAsync(handle, "Fixtures.Leaks.CwtValue");

        var referrers = await _session.QueryReferrersAsync(handle, address, null, Ct);
        var rows = await ReferrersTests.AllRowsAsync(referrers.Rows);
        var paths = await _session.QueryObjectPathsAsync(handle, address, 10, null, Ct);

        rows.ShouldContain(r => r[ReferrerColumns.Type] == "Fixtures.Leaks.CwtKey" && r[ReferrerColumns.Field] == "(dependent handle)");
        paths.Nodes.ShouldContain(n => n.TypeName == "Fixtures.Leaks.CwtKey" && n.ViaDependentHandle && n.ChildId == 0);
    }

    [Fact]
    public async Task Thread_stacks_interior_pointers_and_pinned_handles_are_named_as_roots()
    {
        // Review M6: the RuntimeKinds fixture's roots, end to end.
        var handle = await _session.StartOpen(_dump).Result;
        var expected = new Dictionary<string, RootKindDto>
        {
            ["Fixtures.Leaks.StackRootedObject"] = RootKindDto.ThreadStack,
            ["Fixtures.Leaks.InteriorElement[]"] = RootKindDto.ThreadStack,
            ["Fixtures.Leaks.PinnedPoint[]"] = RootKindDto.PinnedHandle,
        };

        foreach (var (type, kind) in expected)
        {
            var at = await FirstAddressAsync(handle, type);
            var paths = await _session.QueryObjectPathsAsync(handle, at, 10, null, Ct);
            var details = await _session.GetObjectAsync(handle, at, null, Ct);

            var target = paths.Nodes[0];
            target.Root.ShouldNotBeNull($"{type} rooted={details.IsRooted} reachable={paths.Reachable} visited={paths.Visited}: {string.Join(" | ", paths.Nodes.Select(x => $"{x.Id}<-{x.ChildId} {x.TypeName} {x.Root?.Kind} {x.Root?.Description}"))}");
            target.Root.Kind.ShouldBe(kind, type);
        }
    }

    [Fact]
    public async Task Bad_requests_are_refused_and_an_unknown_type_has_no_paths()
    {
        var handle = await _session.StartOpen(_dump).Result;

        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryObjectPathsAsync(handle, 0x10, 10, null, Ct))).ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
        var address = await FirstAddressAsync(handle, "Fixtures.Leaks.StaticCacheEntry");
        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryObjectPathsAsync(handle, address, 0, null, Ct))).ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryTypePathsAsync(handle, "Fixtures.Leaks.StaticCacheEntry", Module, 101, null, Ct))).ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);

        var none = await _session.QueryTypePathsAsync(handle, "Fixtures.Leaks.Nothing", Module, 20, null, Ct);
        (none.MatchedTypes, none.Objects, none.Paths.Count, none.TreeSeconds).ShouldBe((0, 0L, 0, 0d));
    }

    [Fact]
    public async Task The_cli_explains_an_object_and_a_type()
    {
        var (code, output) = await RunCliAsync("paths", _dump, "--type", "Fixtures.Leaks.StaticCacheEntry", "--format", "json");
        code.ShouldBe(0, output);
        using (var json = JsonDocument.Parse(output[output.IndexOf('{', StringComparison.Ordinal)..(output.LastIndexOf('}') + 1)]))
        {
            json.RootElement.GetProperty("Objects").GetInt64().ShouldBe(AllLeaksDump.Items);
            json.RootElement.GetProperty("Paths")[0].GetProperty("Root").GetProperty("Description").GetString().ShouldBe("Static field Fixtures.Leaks.StaticCacheLeak.s_cache");
        }

        var handle = await _session.StartOpen(_dump).Result;
        var entry = await FirstAddressAsync(handle, "Fixtures.Leaks.StaticCacheEntry");
        var address = string.Create(CultureInfo.InvariantCulture, $"0x{entry:x}");
        await _session.CloseAsync(handle, Ct);
        var (objectCode, text) = await RunCliAsync("paths", _dump, "--address", address);

        objectCode.ShouldBe(0, text);
        text.ShouldContain("ROOT: Static field Fixtures.Leaks.StaticCacheLeak.s_cache");
        text.ShouldContain("framework");
        (await RunCliAsync("paths", _dump, "--type", "Fixtures.Leaks.Nothing", "--module", Module)).Code.ShouldBe(1);
        (await RunCliAsync("paths", _dump)).Code.ShouldBe(64);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    /// <summary>A root's path as type names from its object to the item, with "(dependent handle)" where a handle holds the next.</summary>
    private static List<string> PathTypes(ObjectPathsResult paths, int rootNode)
    {
        var names = new List<string>();
        for (var node = paths.Nodes[rootNode]; ; node = paths.Nodes[node.ChildId])
        {
            names.Add(node.TypeName);
            if (node.ChildId < 0)
            {
                return names;
            }

            if (node.ViaDependentHandle)
            {
                names.Add("(dependent handle)");
            }
        }
    }

    private async Task<ulong> FirstAddressAsync(SnapshotHandle handle, string type)
    {
        var instances = await _session.QueryInstancesAsync(handle, type, Module, null, null, true, null, Ct);
        var cell = (await instances.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0]!;
        await instances.Rows.ReleaseAsync(Ct);
        return ulong.Parse(cell[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static async Task<(int Code, string Output)> RunCliAsync(params string[] arguments)
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, arguments);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(180))).ShouldBeTrue(cli.Output);
        return (cli.Process.ExitCode, cli.Output);
    }
}
