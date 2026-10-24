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
