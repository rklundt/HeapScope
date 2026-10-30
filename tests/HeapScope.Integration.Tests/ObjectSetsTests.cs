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
/// Sprint 3.1 (ADR-030): object sets through the real worker. Each kind of set on the leaks fixture, its views agreeing with
/// the same numbers taken from the whole-snapshot queries and from ClrMD.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ObjectSetsTests : IClassFixture<AllLeaksDump>, IAsyncDisposable
{
    private const string Module = "Leaks.dll";

    private readonly AllLeaksDump _shared;
    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public ObjectSetsTests(AllLeaksDump shared)
    {
        _shared = shared;
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("sets.dmp");
        File.Copy(shared.DumpPath, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] LeakTypes => [.. LeakCatalog.All.Take(2).Select(l => KnownAnswer.Load(l.Name).Types[0].Type)];

    [Fact]
    public async Task A_set_of_two_types_has_their_objects_in_every_view()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var set = new ObjectSetQuery(ObjectSetSpec.OfTypes(LeakTypes.Select(t => new TypeKeyDto(t, Module))));

        var whole = await RowsAsync((await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, null, Ct)).Rows);
        var expected = whole.Where(r => LeakTypes.Contains(r[TypeSummaryColumns.TypeName]) && r[TypeSummaryColumns.Module] == Module).ToList();
        var objects = expected.Sum(r => Long(r[TypeSummaryColumns.Objects]));
        var bytes = expected.Sum(r => Long(r[TypeSummaryColumns.Bytes]));
        objects.ShouldBe(2L * AllLeaksDump.Items);

        // Types and Generations: the set's own totals and heap kinds.
        var types = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, includeRetained: false, null, Ct, set);
        (types.Result.TotalObjects, types.Result.TotalBytes, types.Result.Rows.TotalCount).ShouldBe((objects, bytes, 2));
        types.Result.Set.ShouldBe(new ObjectSetInfo(objects, bytes, 2));
        types.Result.HeapKinds.Sum(k => k.Bytes).ShouldBe(bytes);
        (await RowsAsync(types.Rows)).Select(r => Long(r[TypeSummaryColumns.Objects])).ShouldAllBe(n => n == AllLeaksDump.Items);

        // Instances: every member, and one type's members.
        var instances = await _session.QueryInstancesAsync(handle, new InstancesQuery(string.Empty, null, Set: set), null, Ct);
        (instances.Result.TotalObjects, instances.Result.TotalBytes, instances.Result.MatchedTypes).ShouldBe((objects, bytes, 2));
        var one = await _session.QueryInstancesAsync(handle, new InstancesQuery(LeakTypes[0], Module, Set: set), null, Ct);
        one.Result.TotalObjects.ShouldBe(AllLeaksDump.Items);

        // Retained: the same as the two types' exact retention computed independently by ClrMD.
        var retained = await _session.QueryRetainedBySetAsync(handle, set, null, Ct);
        var reachable = _shared.Dump.ReachableFromRoots();
        retained.Result.RetainedBytes.ShouldBe((long)_shared.Dump.SetRetainedBytes(LeakTypes, reachable));
        retained.Result.SetObjects.ShouldBe(objects);
    }

    [Fact]
    public async Task A_namespace_and_an_assembly_are_sets_of_their_rows_objects()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var namespaces = await RowsAsync((await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Namespace, null, null, true, null, Ct)).Rows);
        var assemblies = await RowsAsync((await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Assembly, null, null, true, null, Ct)).Rows);
        var leaks = namespaces.Single(r => r[TypeSummaryColumns.Name] == "Fixtures.Leaks");
        var module = assemblies.Single(r => r[TypeSummaryColumns.Name] == Module);

        var inNamespace = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, false, null, Ct, new ObjectSetQuery(ObjectSetSpec.OfNamespace("Fixtures.Leaks")));
        var inAssembly = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, false, null, Ct, new ObjectSetQuery(ObjectSetSpec.OfAssembly("leaks.DLL")));

        (inNamespace.Result.TotalObjects, inNamespace.Result.TotalBytes).ShouldBe((Long(leaks[TypeSummaryColumns.Objects]), Long(leaks[TypeSummaryColumns.Bytes])));
        (inAssembly.Result.TotalObjects, inAssembly.Result.TotalBytes).ShouldBe((Long(module[TypeSummaryColumns.Objects]), Long(module[TypeSummaryColumns.Bytes])));
        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, false, null, Ct, new ObjectSetQuery(ObjectSetSpec.OfNamespace("No.Such")))))
            .ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Objects_by_address_form_a_set_and_unknown_addresses_are_left_out()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var items = _shared.Dump.Instances(LeakTypes[0]).Take(3).ToList();
        var set = new ObjectSetQuery(ObjectSetSpec.OfObjects([.. items.Select(o => o.Address), 8UL]));

        var instances = await _session.QueryInstancesAsync(handle, new InstancesQuery(string.Empty, null, Set: set), null, Ct);

        instances.Result.TotalObjects.ShouldBe(3);
        (await RowsAsync(instances.Rows)).Select(r => r[InstanceColumns.Address]).Order().ShouldBe(items.Select(o => $"0x{o.Address:x}").Order());
    }

    [Fact]
    public async Task What_a_set_retains_is_a_set_and_its_dominators_divide_the_retention()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var type = new ObjectSetQuery(ObjectSetSpec.OfType(LeakTypes[0], Module));
        var retained = await _session.QueryRetainedBySetAsync(handle, type, null, Ct);

        var asSet = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, false, null, Ct, new ObjectSetQuery(ObjectSetSpec.RetainedByOf(type.Spec)));
        (asSet.Result.TotalObjects, asSet.Result.TotalBytes).ShouldBe((retained.Result.RetainedObjects, retained.Result.RetainedBytes));

        var dominators = await _session.QuerySetDominatorsAsync(handle, type, null, Ct);
        var rows = await RowsAsync(dominators.Rows);
        var result = dominators.Result;
        (result.TopMembers + rows.Sum(r => Long(r[SetDominatorColumns.Members])) + result.UnreachableMembers).ShouldBe(AllLeaksDump.Items, "every member is a top member, below one, or unreachable");
        result.TopRetained.ShouldBe(rows.Sum(r => Long(r[SetDominatorColumns.Retained])));
        result.TopRetained.ShouldBeLessThanOrEqualTo(retained.Result.RetainedBytes, "the tops retain apart what the set retains together, at most");
        rows.Select(r => Long(r[SetDominatorColumns.Retained])).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task An_inspection_row_is_a_set_of_the_objects_it_opens_and_survives_a_worker_restart()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var run = await _session.RunInspectionAsync(handle, "duplicate-strings", null, Ct);
        var rows = await RowsAsync(run.Rows);
        var index = rows.FindIndex(r => r["Value"] == "HeapScope waste duplicate B");
        var set = new ObjectSetQuery(ObjectSetSpec.OfInspectionRow("duplicate-strings", index));

        var instances = await _session.QueryInstancesAsync(handle, new InstancesQuery(string.Empty, null, Set: set), null, Ct);
        instances.Result.TotalObjects.ShouldBe(30, "the 30 copies, as the row's instances list them");

        // A restart loses the cached inspection: the set runs it again.
        await _host.RestartAsync(Ct);
        var again = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, false, null, Ct, set);
        again.Result.TotalObjects.ShouldBe(30);
        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryInstancesAsync(handle, new InstancesQuery(string.Empty, null, Set: new ObjectSetQuery(ObjectSetSpec.OfInspectionRow("duplicate-strings", 100_000))), null, Ct)))
            .ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Sets_over_their_limits_or_selecting_nothing_are_refused_before_any_work()
    {
        var handle = await _session.StartOpen(_dump).Result;
        async Task<HeapScopeErrorCode> RefusalAsync(ObjectSetSpec spec) =>
            (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryInstancesAsync(handle, new InstancesQuery(string.Empty, null, Set: new ObjectSetQuery(spec)), null, Ct))).ErrorCode;

        (await RefusalAsync(ObjectSetSpec.OfObjects(Enumerable.Range(1, ObjectSetLimits.MaxAddresses + 1).Select(i => (ulong)i)))).ShouldBe(HeapScopeErrorCode.InvalidArgument);
        (await RefusalAsync(ObjectSetSpec.OfTypes([]))).ShouldBe(HeapScopeErrorCode.InvalidArgument);
        (await RefusalAsync(ObjectSetSpec.OfType("Fixtures.Leaks.NoSuchType", Module))).ShouldBe(HeapScopeErrorCode.InvalidArgument);
        var deep = ObjectSetSpec.OfType(LeakTypes[0], Module);
        for (var i = 0; i <= ObjectSetLimits.MaxDepth + 1; i++)
        {
            deep = ObjectSetSpec.RetainedByOf(deep);
        }

        (await RefusalAsync(deep)).ShouldBe(HeapScopeErrorCode.InvalidArgument);
        var none = await _session.QueryInstancesAsync(handle, new InstancesQuery(string.Empty, null, Set: new ObjectSetQuery(ObjectSetSpec.OfObjects([8UL]))), null, Ct);
        none.Result.TotalObjects.ShouldBe(0, "an address with no object selects nothing");
    }

    [Fact]
    public async Task The_cli_set_command_shows_the_views_the_app_shows()
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, "set", _dump, "--type", LeakTypes[0], "--type", LeakTypes[1], "--module", Module, "--view", "generations");
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(0, cli.Output);
        cli.Output.ShouldContain("Gen");

        using var instances = ChildProcess.Start(TestPaths.CliExe, "set", _dump, "--namespace", "Fixtures.Leaks", "--view", "types", "--format", "csv", "--all");
        (await instances.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(instances.Output);
        instances.Process.ExitCode.ShouldBe(0, instances.Output);
        instances.Output.ShouldContain(LeakTypes[0]);

        using var dominators = ChildProcess.Start(TestPaths.CliExe, "set", _dump, "--type", LeakTypes[0], "--view", "dominators", "--top", "3");
        (await dominators.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(dominators.Output);
        dominators.Process.ExitCode.ShouldBe(0, dominators.Output);
        dominators.Output.ShouldContain("no other member dominates");

        using var none = ChildProcess.Start(TestPaths.CliExe, "set", _dump, "--namespace", "No.Such", "--view", "types");
        (await none.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(none.Output);
        none.Process.ExitCode.ShouldBe(1, none.Output);

        // Sprint 3.5: the objects no root reaches (the count itself is checked against ClrMD in DetectorTests); the dump holds
        // the late garbage fixture.
        using var unreachable = ChildProcess.Start(TestPaths.CliExe, "set", _dump, "--unreachable", "--view", "types", "--all");
        (await unreachable.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(unreachable.Output);
        unreachable.Process.ExitCode.ShouldBe(0, unreachable.Output);
        unreachable.Output.ShouldContain("Fixtures.Leaks.WeakOnlyTarget");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    private static Task<List<Dictionary<string, string?>>> RowsAsync(ResultSetHandle rows) => RetainedSizesTests.AllRowsAsync(rows);

    private static long Long(string? text) => long.Parse(text!, CultureInfo.InvariantCulture);
}
