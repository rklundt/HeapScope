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

using System.IO;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.Services;
using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.UI.Wpf.Tests;

/// <summary>The Snapshot tab's drill-down (sprint 1.5): types, a type's objects, one object; back and forward.</summary>
public sealed class NavigatorViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly SnapshotsViewModel _snapshots;

    public NavigatorViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        _snapshots = new SnapshotsViewModel(new SnapshotSession(_worker, operations.Jobs), operations, new ImmediateUiDispatcher(), new FakeUserInteraction());
        _worker.Service.Rows.Add(["App.Order", "2", "80"]);
        _worker.Service.InstanceRows.Add(["0x1000", "App.Order", "40", "Gen2", ""]);
        _worker.Service.InstanceRows.Add(["0x2000", "App.Order", "40", "Gen0", ""]);
        _worker.Service.ReferenceRows.Add(["Customer", "0x3000", "App.Customer", "60"]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NavigatorViewModel Navigator => _snapshots.Navigator;

    [Fact]
    public async Task A_type_row_opens_its_objects_and_back_and_forward_move_through_the_history()
    {
        await OpenAsync("app.dmp");
        var types = Navigator.Current.ShouldBeOfType<TypeSummaryViewModel>();
        var row = await FirstRowAsync(types.Rows);

        types.OpenRowCommand!.Execute(row);
        var instances = Navigator.Current.ShouldBeOfType<InstancesViewModel>();
        await instances.QueryTask;

        _worker.Service.InstanceQueries.ShouldHaveSingleItem().ShouldBe(new InstancesRequest(_snapshots.Selected!.Handle.Descriptor.SnapshotId, "App.Order", "App.dll", null, InstanceColumns.Bytes, true));
        instances.Rows.Count.ShouldBe(2);
        Navigator.Trail.Count.ShouldBe(2);
        Navigator.BackCommand.Execute(null);
        Navigator.Current.ShouldBeSameAs(types);
        Navigator.CanGoForward.ShouldBeTrue();
        Navigator.ForwardCommand.Execute(null);
        Navigator.Current.ShouldBeSameAs(instances);
    }

    [Fact]
    public async Task An_object_shows_its_fields_and_references_and_a_reference_opens_the_next_object()
    {
        var instances = await OpenInstancesAsync();
        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var obj = Navigator.Current.ShouldBeOfType<ObjectViewModel>();
        await obj.LoadTask;

        obj.Address.ShouldBe(0x1000UL);
        obj.Fields.ShouldHaveSingleItem().Value.ShouldBe("7");
        obj.FieldsUnavailable.ShouldBeNull();
        obj.Summary!.ShouldContain("App.Order");
        var reference = await FirstRowAsync(obj.ReferenceRows);
        reference.Field.ShouldBe("Customer");

        obj.OpenCommand!.Execute(reference);
        var next = Navigator.Current.ShouldBeOfType<ObjectViewModel>();
        next.Address.ShouldBe(0x3000UL);
        Navigator.Trail.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Opening_after_going_back_drops_the_forward_history_and_releases_its_rows()
    {
        var instances = await OpenInstancesAsync();
        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var obj = (ObjectViewModel)Navigator.Current;
        await obj.LoadTask;
        Navigator.BackCommand.Execute(null);
        Navigator.BackCommand.Execute(null);
        var released = _worker.Service.Released.Count;

        Navigator.OpenInstances(_snapshots.Selected!.Handle, "App.Customer", "App.dll");

        Navigator.CanGoForward.ShouldBeFalse();
        Navigator.Trail.Count.ShouldBe(2);
        await WaitUntilAsync(() => _worker.Service.Released.Count >= released + 2);
    }

    [Fact]
    public async Task Without_field_values_the_object_explains_why()
    {
        _worker.Service.Objects[0x1000] = new ObjectDetails(0x1000, "App.Order", 40, HeapKindDto.Gen2, 0, true, null, "Field values are not stored in a gcdump.", null);
        var instances = await OpenInstancesAsync();

        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var obj = (ObjectViewModel)Navigator.Current;
        await obj.LoadTask;

        obj.Fields.ShouldBeEmpty();
        obj.FieldsUnavailable.ShouldNotBeNull().ShouldContain("gcdump");
        obj.Summary!.ShouldContain("held by a root");
    }

    [Fact]
    public async Task A_namespace_row_asks_for_type_grouping_and_another_snapshot_starts_a_new_history()
    {
        await OpenInstancesAsync();
        Navigator.BackCommand.Execute(null);
        var types = (TypeSummaryViewModel)Navigator.Current;
        types.GroupBy = TypeGrouping.Namespace;
        await types.QueryTask;

        types.OpenRowCommand!.Execute(await FirstRowAsync(types.Rows));
        types.Status.ShouldNotBeNull().ShouldContain("Group by type");
        Navigator.Current.ShouldBeSameAs(types);

        await OpenAsync("other.dmp");
        Navigator.Trail.Count.ShouldBe(1);
        Navigator.CanGoForward.ShouldBeFalse();
    }

    [Fact]
    public async Task A_compare_row_opens_its_objects_in_the_current_snapshot()
    {
        _worker.Service.DiffRows.Add(["App.Order", "", "1", "2", "1", "40", "80", "40", "1", "1", "App.Order", "App.dll"]);
        await OpenAsync("first.dmp");
        await OpenAsync("second.dmp");
        await _snapshots.Compare.QueryTask;
        _snapshots.SelectedTab = 1;

        _snapshots.Compare.OpenRowCommand!.Execute(await FirstRowAsync(_snapshots.Compare.Rows));

        _snapshots.SelectedTab.ShouldBe(0);
        _snapshots.Selected.ShouldBeSameAs(_snapshots.Compare.Current);
        var instances = Navigator.Current.ShouldBeOfType<InstancesViewModel>();
        (instances.TypeName, instances.Module).ShouldBe(("App.Order", "App.dll"));
    }

    [Fact]
    public async Task Incoming_references_load_when_their_tab_is_first_shown_and_a_referrer_opens()
    {
        _worker.Service.ReferrerRows.Add(["_items[2]", "0x5000", "App.Order[]", "64", "2"]);
        var instances = await OpenInstancesAsync();
        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var obj = (ObjectViewModel)Navigator.Current;
        await obj.LoadTask;

        _worker.Service.ReferrerQueries.ShouldBeEmpty("nothing is indexed until the Incoming tab is shown");
        obj.ReferencesTab = 1;
        await obj.IncomingTask;
        obj.ReferencesTab = 0;
        obj.ReferencesTab = 1;

        _worker.Service.ReferrerQueries.ShouldHaveSingleItem().Address.ShouldBe(0x1000UL);
        obj.IncomingTotals.ShouldBe(string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{1:N0} objects, {2:N0} references"));
        obj.IncomingStatus.ShouldBeNull();
        var referrer = await FirstRowAsync(obj.IncomingRows);
        (referrer.Field, referrer.Type, referrer.References).ShouldBe(("_items[2]", "App.Order[]", 2L));

        obj.OpenCommand!.Execute(referrer);
        Navigator.Current.ShouldBeOfType<ObjectViewModel>().Address.ShouldBe(0x5000UL);
    }

    [Fact]
    public async Task An_incomplete_snapshot_says_referrers_may_be_missing_and_progress_reads_as_a_status()
    {
        _worker.Service.CompleteGraph = false;
        var instances = await OpenInstancesAsync();
        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var obj = (ObjectViewModel)Navigator.Current;
        await obj.LoadTask;

        obj.ReferencesTab = 1;
        await obj.IncomingTask;
        Navigator.BackCommand.Execute(null);
        instances.ReferencedByCommand!.Execute(null);
        var referrers = (TypeReferrersViewModel)Navigator.Current;
        await referrers.QueryTask;

        obj.IncomingStatus!.ShouldContain("incomplete");
        referrers.Status!.ShouldContain("incomplete");
        ReferrersText.Progress(new JobProgress("Indexing incoming references", 21, 50)).ShouldBe(
            string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Indexing incoming references... {42.0:N0}%"));
        ReferrersText.Progress(new JobProgress("Mapping the incoming references index", 0, null)).ShouldBe("Mapping the incoming references index...");
    }

    [Fact]
    public async Task The_retention_tab_loads_once_folds_framework_steps_and_opens_a_node()
    {
        var root = new PathRootDto(RootKindDto.StaticField, false, "Static field App.Program.s_cache");
        _worker.Service.ObjectPaths = new ObjectPathsResult(
        [
            new RetentionNodeDto(0, -1, 0x1000, "App.Order", null, false, false, 0, null, 1, 0),
            new RetentionNodeDto(1, 0, 0x7000, "System.Collections.Generic.List<App.Order>", "[0]", false, true, 1, null, 1, 0),
            new RetentionNodeDto(2, 1, 0x8000, "System.Object[]", "_items", false, true, 2, null, 1, 0),
            new RetentionNodeDto(3, 2, 0x9000, "App.Cache", "_list", false, false, 3, root, 1, 0),
        ],
        [3],
        [new KeyPathDto(root, [new("App.Cache", false), new("System.Object[]", true), new("System.Collections.Generic.List<App.Order>", true)], "App.Order", 1, 40, 0x9000)],
        true, false, 4, null, true, new ReverseIndexInfo(true, SnapshotMemoryMode.InMemory, 0, 1024, true), false, "No root reaches it.");
        var instances = await OpenInstancesAsync();
        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var obj = (ObjectViewModel)Navigator.Current;
        await obj.LoadTask;

        obj.ReferencesTab = 2;
        await obj.RetentionTask;
        obj.ReferencesTab = 0;
        obj.ReferencesTab = 2;

        _worker.Service.ObjectPathQueries.ShouldHaveSingleItem().Address.ShouldBe(0x1000UL);
        obj.RetentionStatus.ShouldBe(string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{1:N0} roots hold this object."));
        obj.KeyPaths.ShouldHaveSingleItem().PathText.ShouldBe("App.Cache > (2 framework types) > App.Order");
        var target = obj.RetentionTree.ShouldHaveSingleItem();
        var cache = target.Children.ShouldHaveSingleItem();
        cache.Text.ShouldBe("(2 framework objects) _list of App.Cache");
        cache.Root.ShouldBe("Static field App.Program.s_cache");

        obj.ShowFrameworkSteps = true;
        obj.RetentionTree[0].Children[0].Text.ShouldBe("[0] of System.Collections.Generic.List<App.Order>");
        obj.KeyPaths[0].PathText.ShouldBe("App.Cache > System.Object[] > System.Collections.Generic.List<App.Order> > App.Order");

        obj.OpenCommand!.Execute(cache);
        Navigator.Current.ShouldBeOfType<ObjectViewModel>().Address.ShouldBe(0x9000UL);
    }

    [Fact]
    public async Task Retained_sizes_are_asked_for_on_the_types_grid_and_carried_into_its_objects()
    {
        // Sprint 2.3: off by default (the first query computes the dominators), a column and a note when on.
        await OpenAsync("app.dmp");
        var types = Navigator.Current.ShouldBeOfType<TypeSummaryViewModel>();
        await types.QueryTask;
        types.RetainedColumnShown.ShouldBeFalse();
        _worker.Service.SummaryQueries[^1].IncludeRetained.ShouldBeFalse();

        types.ShowRetained = true;
        await types.QueryTask;
        types.SortByCommand.Execute(TypeSummaryColumns.Retained);
        await types.QueryTask;

        _worker.Service.SummaryQueries[^1].ShouldBe(_worker.Service.SummaryQueries[^1] with { IncludeRetained = true, SortColumn = TypeSummaryColumns.Retained, Descending = true });
        types.RetainedColumnShown.ShouldBeTrue();
        types.RetainedNote!.ShouldContain("at least");
        (await FirstRowAsync(types.Rows)).Retained.ShouldBe(240);

        var instances = await OpenInstancesAsync(types);
        _worker.Service.InstanceQueries[^1].IncludeRetained.ShouldBeTrue("the objects of a type follow the types grid's choice");
        instances.RetainedColumnShown.ShouldBeTrue();
        instances.Rows.GetRow(0).Retained.ShouldBe(80);
        instances.Rows.GetRow(1).Retained.ShouldBeNull("no root reaches it");

        Navigator.BackCommand.Execute(null);
        types.ShowRetained = false;
        await types.QueryTask;
        (_worker.Service.SummaryQueries[^1].IncludeRetained, types.SortColumn).ShouldBe((false, TypeSummaryColumns.Bytes));
        types.RetainedColumnShown.ShouldBeFalse();
        types.RetainedNote.ShouldBeNull();
    }

    [Fact]
    public async Task A_failing_retained_query_still_shows_the_rows_and_says_why()
    {
        // Review M8: retained sizes are optional; the grids load without them and the switch turns itself off.
        _worker.Service.RetainedFailure = new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.InsufficientMemory, "Not enough memory.", null);
        await OpenAsync("app.dmp");
        var types = Navigator.Current.ShouldBeOfType<TypeSummaryViewModel>();
        await types.QueryTask;
        types.SortByCommand.Execute(TypeSummaryColumns.Bytes);
        await types.QueryTask;

        types.ShowRetained = true;
        await types.QueryTask;

        (types.ShowRetained, types.RetainedColumnShown, types.Rows.Count).ShouldBe((false, false, 1));
        types.RetainedNote.ShouldBe("Retained sizes are not shown: Not enough memory.");
        types.Status.ShouldBeNull();
        _worker.Service.SummaryQueries[^1].IncludeRetained.ShouldBeFalse();

        _worker.Service.RetainedFailure = new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.InsufficientMemory, "Still not.", null);
        var instances = await OpenInstancesAsync(types);
        instances.ShowRetained = true;
        await instances.QueryTask;
        (instances.ShowRetained, instances.Rows.Count, instances.RetainedNote).ShouldBe((false, 2, "Retained sizes are not shown: Still not."));
    }

    [Fact]
    public async Task The_retention_tab_shows_the_retained_size_and_opens_the_dominator()
    {
        var instances = await OpenInstancesAsync();
        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var obj = (ObjectViewModel)Navigator.Current;
        await obj.LoadTask;

        obj.ReferencesTab = 2;
        await obj.RetainedTask;

        _worker.Service.RetainedQueries.ShouldHaveSingleItem().Address.ShouldBe(0x1000UL);
        obj.RetainedSummary!.ShouldStartWith(string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Retains {4096:N0} bytes"));
        obj.DominatorText.ShouldBe("0x500 App.Cache");
        obj.OpenCommand!.Execute(obj.DominatorAddress);
        Navigator.Current.ShouldBeOfType<ObjectViewModel>().Address.ShouldBe(0x500UL);
    }

    [Fact]
    public async Task An_object_no_root_reaches_retains_nothing_and_links_nowhere()
    {
        _worker.Service.Retained = new RetainedResult(0x1000, "App.Order", 40, false, 0, 0, null, false, "No root reaches it: it is garbage.", _worker.Service.Dominators, true);
        var instances = await OpenInstancesAsync();
        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var obj = (ObjectViewModel)Navigator.Current;
        await obj.LoadTask;

        obj.ReferencesTab = 2;
        await obj.RetainedTask;

        obj.RetainedSummary.ShouldBe("Retains nothing. No root reaches it: it is garbage.");
        obj.DominatorText.ShouldBeNull();
    }

    [Fact]
    public async Task The_retention_tab_shows_a_worker_error_and_a_tree_with_two_roots_in_order()
    {
        // Review M8.
        var instances = await OpenInstancesAsync();
        instances.OpenRowCommand!.Execute(await FirstRowAsync(instances.Rows));
        var failing = (ObjectViewModel)Navigator.Current;
        await failing.LoadTask;
        _worker.Service.ObjectPathsFailure = new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.InsufficientMemory, "Not enough memory.", null);

        failing.ReferencesTab = 2;
        await failing.RetentionTask;

        failing.RetentionStatus.ShouldBe("Not enough memory.");
        failing.RetentionTree.ShouldBeEmpty();

        _worker.Service.ObjectPathsFailure = null;
        var a = new PathRootDto(RootKindDto.StaticField, false, "Static field App.s_a");
        var b = new PathRootDto(RootKindDto.ThreadStack, false, "Local variable or argument on thread 0x1");
        _worker.Service.ObjectPaths = new ObjectPathsResult(
        [
            new RetentionNodeDto(0, -1, 0x1000, "App.Order", null, false, false, 0, null, 2, 0),
            new RetentionNodeDto(1, 0, 0x2000, "App.Cache", "_order", false, false, 1, a, 1, 0),
            new RetentionNodeDto(2, 0, 0x3000, "App.Node", "_value", false, false, 1, null, 1, 41),
            new RetentionNodeDto(3, 2, 0x4000, "App.Worker", "_head", false, false, 2, b, 1, 0),
        ],
        [1, 3], [], true, false, 4, null, true, new ReverseIndexInfo(true, SnapshotMemoryMode.InMemory, 0, 1024, true), false, "No root reaches it.");
        Navigator.BackCommand.Execute(null);
        instances.OpenRowCommand!.Execute(instances.Rows.GetRow(1));
        await instances.Rows.WhenLoadedAsync();
        var obj = (ObjectViewModel)Navigator.Current;
        await obj.LoadTask;

        obj.ReferencesTab = 2;
        await obj.RetentionTask;

        var holders = obj.RetentionTree.ShouldHaveSingleItem().Children;
        holders.Select(h => h.Text).ShouldBe(["_order of App.Cache", "_value of App.Node (and 41 more of this type)"]);
        holders[1].Children.ShouldHaveSingleItem().Root.ShouldBe("Local variable or argument on thread 0x1");
    }

    [Fact]
    public async Task Why_alive_lists_a_types_key_paths_whose_rows_open_an_example()
    {
        _worker.Service.TypePaths.Add(new KeyPathDto(
            new PathRootDto(RootKindDto.StaticField, false, "Static field App.s_orders"), [new("App.Cache", false)], "App.Order", 5, 200, 0x2000));
        var instances = await OpenInstancesAsync();

        instances.WhyAliveCommand!.Execute(null);
        var paths = Navigator.Current.ShouldBeOfType<TypePathsViewModel>();
        await paths.LoadTask;

        Navigator.IsTypePathsShown.ShouldBeTrue();
        paths.Title.ShouldBe("Why alive: App.Order [App.dll]");
        _worker.Service.TypePathQueries.ShouldHaveSingleItem().ShouldBe(new TypePathsRequest(_snapshots.Selected!.Handle.Descriptor.SnapshotId, "App.Order", "App.dll", 20));
        var row = paths.Paths.ShouldHaveSingleItem();
        (row.Objects, row.Bytes, row.Root, row.PathText).ShouldBe((5L, 200L, "Static field App.s_orders", "App.Cache > App.Order"));
        paths.Totals!.ShouldContain("2 not reachable from any root");
        paths.Status.ShouldBeNull();

        paths.OpenRowCommand!.Execute(row);
        Navigator.Current.ShouldBeOfType<ObjectViewModel>().Address.ShouldBe(0x2000UL);
    }

    [Fact]
    public async Task Why_alive_is_offered_for_a_types_whole_list_only()
    {
        // 2.2 review L12: a scoped list (equal strings, LOH) would get the paths of every object of the type.
        var whole = await OpenInstancesAsync();
        whole.WhyAliveCommand!.CanExecute(null).ShouldBeTrue();

        var scoped = Navigator.OpenInstances(_snapshots.Selected!.Handle, "App.Order", "App.dll", InstanceScope.LargeObjectHeap);
        await scoped.QueryTask;

        scoped.WhyAliveCommand!.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Referenced_by_lists_referrer_types_whose_rows_open_their_objects()
    {
        _worker.Service.TypeReferrerRows.Add(["App.Cache", "App.dll", "1", "2", "2", "100"]);
        var instances = await OpenInstancesAsync();

        instances.ReferencedByCommand!.Execute(null);
        var referrers = Navigator.Current.ShouldBeOfType<TypeReferrersViewModel>();
        await referrers.QueryTask;

        Navigator.IsTypeReferrersShown.ShouldBeTrue();
        Navigator.ShownTypeReferrers.ShouldBeSameAs(referrers);
        referrers.Title.ShouldBe("Referrers of App.Order [App.dll]");
        _worker.Service.TypeReferrerQueries.ShouldHaveSingleItem().ShouldBe(
            new TypeReferrersRequest(_snapshots.Selected!.Handle.Descriptor.SnapshotId, "App.Order", "App.dll", TypeReferrerColumns.References, true));
        var row = await FirstRowAsync(referrers.Rows);
        (row.TypeName, row.Objects, row.References, row.Targets, row.Bytes).ShouldBe(("App.Cache", 1L, 2L, 2L, 100L));

        referrers.OpenRowCommand!.Execute(row);
        var cacheObjects = Navigator.Current.ShouldBeOfType<InstancesViewModel>();
        (cacheObjects.TypeName, cacheObjects.Module).ShouldBe(("App.Cache", "App.dll"));
        Navigator.Trail.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_long_list_keeps_a_bounded_window_of_pages()
    {
        for (var i = 1; i < 100 * VirtualRowList.PageSize; i++)
        {
            _worker.Service.Rows.Add([$"App.Type{i:D5}", "1", "8"]);
        }

        await OpenAsync("big.dmp");
        var list = _snapshots.Types.Rows;
        for (var page = 0; page < 60; page++)
        {
            list.GetRow(page * VirtualRowList.PageSize);
        }

        await list.WhenLoadedAsync();
        list.RequestedPages.Count.ShouldBe(VirtualRowList.MaxPages);
        list.RequestedPages.ShouldNotContain(0, "the page farthest from the last one asked for went first");
        list.RequestedPages.ShouldContain(59);
        list.GetRow(0);
        await list.WhenLoadedAsync();
        list.RequestedPages.ShouldContain(0, "a dropped page is fetched again when asked for");
        list.GetRow(0).IsLoaded.ShouldBeTrue();
    }

    public void Dispose()
    {
        _snapshots.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<InstancesViewModel> OpenInstancesAsync()
    {
        await OpenAsync("app.dmp");
        var types = (TypeSummaryViewModel)Navigator.Current;
        types.OpenRowCommand!.Execute(await FirstRowAsync(types.Rows));
        var instances = (InstancesViewModel)Navigator.Current;
        await instances.QueryTask;
        return instances;
    }

    private async Task<InstancesViewModel> OpenInstancesAsync(TypeSummaryViewModel types)
    {
        types.OpenRowCommand!.Execute(await FirstRowAsync(types.Rows));
        var instances = (InstancesViewModel)Navigator.Current;
        await instances.QueryTask;
        return instances;
    }

    private async Task OpenAsync(string name)
    {
        var path = Path.Combine(_folder, name);
        await File.WriteAllBytesAsync(path, [1], Ct);
        await _snapshots.OpenPathAsync(path);
        await _snapshots.Types.QueryTask;
    }

    private static async Task<TRow> FirstRowAsync<TRow>(VirtualRowList<TRow> rows)
        where TRow : class, IIndexedRow
    {
        var row = rows.GetRow(0);
        await rows.WhenLoadedAsync();
        return row;
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
}
