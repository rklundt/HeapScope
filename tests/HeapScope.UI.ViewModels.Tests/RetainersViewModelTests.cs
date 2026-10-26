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
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The Retainers tab (sprint 2.4): the dominator tree, its icicle, set retention and the links in and out.</summary>
public sealed class RetainersViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly SnapshotsViewModel _snapshots;

    public RetainersViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        _snapshots = new SnapshotsViewModel(new SnapshotSession(_worker, operations.Jobs), operations, new ImmediateUiDispatcher(), new FakeUserInteraction());
        _worker.Service.Rows.Add(["App.Order", "2", "80"]);
        _worker.Service.InstanceRows.Add(["0x1000", "App.Order", "40", "Gen2", ""]);
        _worker.Service.TreeNodes.AddRange(
        [
            new FakeTreeNode("o:100", string.Empty, "App.Cache", 0x100, 800),
            new FakeTreeNode("o:200", "o:100", "App.Entry", 0x200, 400),
            new FakeTreeNode("o:300", "o:100", "App.Entry", 0x300, 300),
            new FakeTreeNode("o:400", string.Empty, "App.Other", 0x400, 50),
        ]);
        _worker.Service.SetRows.AddRange([("App.Order", 2, 80), ("System.Byte[]", 2, 2000)]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RetainersViewModel Retainers => _snapshots.Retainers;

    [Fact]
    public async Task Nothing_is_asked_until_the_tab_is_shown_then_the_biggest_retainers_and_their_icicle_load()
    {
        await OpenAsync();
        _worker.Service.TreeQueries.ShouldBeEmpty("the first query of a snapshot computes its dominators");

        await ShowTabAsync();

        var query = _worker.Service.TreeQueries.ShouldHaveSingleItem();
        (query.Expanded.Count, query.FocusAddress, query.Grouping).ShouldBe((0, 0UL, DominatorGroupingDto.None));
        (await RowsAsync()).Select(r => (r.Name, r.Depth, r.Retained, r.IsObject)).ShouldBe([("App.Cache", 0, 800L, true), ("App.Other", 0, 50L, true)]);
        Retainers.Totals!.ShouldContain("2 top-level retainers");
        Retainers.Note!.ShouldContain("exactly");
        Retainers.IcicleSegments.Select(s => s.Name).ShouldBe(["All reachable objects", "App.Cache", "App.Other", "App.Entry", "App.Entry"]);
        Retainers.IcicleTotal.ShouldBe(850);
        Retainers.HasFocus.ShouldBeFalse();
    }

    [Fact]
    public async Task A_row_expands_and_collapses_in_place_and_stays_selected()
    {
        await OpenAsync();
        await ShowTabAsync();
        var cache = (await RowsAsync())[0];

        Retainers.ToggleCommand.Execute(cache);
        await Retainers.QueryTask;

        _worker.Service.TreeQueries[^1].Expanded.ShouldBe(["o:100"]);
        var rows = await RowsAsync();
        rows.Select(r => (r.Name, r.Depth)).ShouldBe([("App.Cache", 0), ("App.Entry", 1), ("App.Entry", 1), ("App.Other", 0)]);
        (rows[0].IsExpanded, rows[0].Glyph, rows[1].Indent).ShouldBe((true, "▾", DominatorRowItem.IndentPerLevel));
        await WaitUntilAsync(() => Retainers.SelectedRow?.Key == "o:100");

        Retainers.ExpandCommand.Execute(rows[0]);
        _worker.Service.TreeQueries.Count.ShouldBe(2, "an expanded row does not expand again");
        Retainers.CollapseCommand.Execute(rows[0]);
        await Retainers.QueryTask;
        (await RowsAsync()).Count.ShouldBe(2);
        Retainers.Expanded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Grouping_focus_and_the_trail_ask_the_worker_again()
    {
        await OpenAsync();
        await ShowTabAsync();

        Retainers.Grouping = DominatorGroupingDto.Type;
        await Retainers.QueryTask;
        _worker.Service.TreeQueries[^1].Grouping.ShouldBe(DominatorGroupingDto.Type);

        Retainers.FocusCommand.Execute((await RowsAsync())[0]);
        await Retainers.QueryTask;
        await Retainers.IcicleTask;
        (_worker.Service.TreeQueries[^1].FocusAddress, _worker.Service.IcicleQueries[^1].FocusAddress).ShouldBe((0x100UL, 0x100UL));
        Retainers.HasFocus.ShouldBeTrue();
        Retainers.Totals!.ShouldContain("Retains");

        Retainers.FocusSegmentCommand.Execute(Retainers.IcicleSegments.First(s => s.Address == 0x200));
        await Retainers.QueryTask;
        _worker.Service.TreeQueries[^1].FocusAddress.ShouldBe(0x200UL);

        Retainers.ShowTopCommand.Execute(null);
        await Retainers.QueryTask;
        (_worker.Service.TreeQueries[^1].FocusAddress, Retainers.HasFocus).ShouldBe((0UL, false));
    }

    [Fact]
    public async Task A_bar_of_the_icicle_selects_its_row_and_a_row_its_bar()
    {
        await OpenAsync();
        await ShowTabAsync();
        var rows = await RowsAsync();

        Retainers.SelectedSegment = Retainers.IcicleSegments.Single(s => s.Address == 0x400);
        Retainers.SelectedRow.ShouldBeSameAs(rows[1]);

        Retainers.SelectedRow = rows[0];
        Retainers.SelectedSegment!.Address.ShouldBe(0x100UL);
    }

    [Fact]
    public async Task A_tree_node_opens_its_object_its_types_objects_and_why_it_is_alive()
    {
        await OpenAsync();
        await ShowTabAsync();
        var cache = (await RowsAsync())[0];

        Retainers.OpenObjectCommand.Execute(cache);
        _snapshots.Tab.ShouldBe(SnapshotTabs.Snapshot);
        _snapshots.Navigator.Current.ShouldBeOfType<ObjectViewModel>().Address.ShouldBe(0x100UL);

        Retainers.OpenPathsCommand.Execute(cache);
        _snapshots.Navigator.Current.ShouldBeOfType<ObjectViewModel>().ReferencesView.ShouldBe(ObjectReferenceTabs.Retention);

        Retainers.OpenInstancesCommand.Execute(cache);
        _snapshots.Navigator.Current.ShouldBeOfType<InstancesViewModel>().TypeName.ShouldBe("App.Cache");
    }

    [Fact]
    public async Task A_type_row_shows_its_group_in_the_tree_and_what_its_objects_retain_together()
    {
        await OpenAsync();
        var typeRow = _snapshots.Types.Rows.GetRow(0);
        await _snapshots.Types.Rows.WhenLoadedAsync();

        _snapshots.Types.RetainedBySetCommand.Execute(typeRow);
        await Retainers.Set.QueryTask;

        _snapshots.Tab.ShouldBe(SnapshotTabs.Retainers);
        _worker.Service.SetQueries.ShouldHaveSingleItem().ShouldBe(new RetainedBySetRequest(_snapshots.Selected!.Handle.Descriptor.SnapshotId, "App.Order", "App.dll"));
        (Retainers.Set.IsShown, Retainers.Set.Rows.Count).ShouldBe((true, 2));
        Retainers.Set.Summary!.ShouldContain("exactly");
        Retainers.Set.Columns.Select(c => c.Column.Name).ShouldNotContain(RetainedBySetColumns.TypeName);
        var setRow = Retainers.Set.Rows.GetRow(1);
        await Retainers.Set.Rows.WhenLoadedAsync();
        Retainers.Set.OpenRowCommand!.Execute(setRow);
        _snapshots.Navigator.Current.ShouldBeOfType<InstancesViewModel>().TypeName.ShouldBe("System.Byte[]");

        _snapshots.Types.ShowInRetainersCommand.Execute(typeRow);
        await Retainers.QueryTask;
        _snapshots.Tab.ShouldBe(SnapshotTabs.Retainers);
        var query = _worker.Service.TreeQueries[^1];
        (query.Grouping, query.FocusAddress).ShouldBe((DominatorGroupingDto.Type, 0UL));
        query.Expanded.ShouldBe([DominatorTreeKeys.TypeGroup(DominatorTreeKeys.Root, "App.Order", "App.dll")]);

        Retainers.Set.HideCommand.Execute(null);
        Retainers.Set.IsShown.ShouldBeFalse();
    }

    [Fact]
    public async Task An_object_shows_itself_in_the_tree_and_its_type_retained_by_all_from_its_objects()
    {
        await OpenAsync();
        var snapshot = _snapshots.Selected!.Handle;
        var instances = _snapshots.Navigator.OpenInstances(snapshot, "App.Order", "App.dll");
        await instances.QueryTask;
        var obj = _snapshots.Navigator.OpenObject(snapshot, 0x1000);

        obj.ShowInRetainersCommand.Execute(null);
        await Retainers.QueryTask;
        _snapshots.Tab.ShouldBe(SnapshotTabs.Retainers);
        _worker.Service.TreeQueries.ShouldHaveSingleItem("the target is applied before the tab is shown, so the top is never asked for").FocusAddress.ShouldBe(0x1000UL);

        instances.RetainedBySetCommand.Execute(null);
        await Retainers.Set.QueryTask;
        _worker.Service.SetQueries.ShouldHaveSingleItem().TypeName.ShouldBe("App.Order");
    }

    [Fact]
    public async Task Another_snapshot_starts_from_its_biggest_retainers()
    {
        await OpenAsync();
        await ShowTabAsync();
        Retainers.FocusCommand.Execute((await RowsAsync())[0]);
        await Retainers.QueryTask;

        await OpenAsync("second.dmp");
        await Retainers.QueryTask;

        (_worker.Service.TreeQueries[^1].FocusAddress, Retainers.HasFocus, Retainers.Set.IsShown).ShouldBe((0UL, false, false));
        _worker.Service.TreeQueries[^1].SnapshotId.ShouldBe(_snapshots.Selected!.Handle.Descriptor.SnapshotId);
    }

    public void Dispose()
    {
        _snapshots.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task OpenAsync(string name = "app.dmp")
    {
        var path = Path.Combine(_folder, name);
        await File.WriteAllBytesAsync(path, [1], Ct);
        await _snapshots.OpenPathAsync(path);
        await _snapshots.Types.QueryTask;
    }

    private async Task ShowTabAsync()
    {
        _snapshots.Tab = SnapshotTabs.Retainers;
        await Retainers.QueryTask;
        await Retainers.IcicleTask;
    }

    private async Task<List<DominatorRowItem>> RowsAsync()
    {
        var rows = Enumerable.Range(0, Retainers.Rows.Count).Select(Retainers.Rows.GetRow).ToList();
        await Retainers.Rows.WhenLoadedAsync();
        return rows;
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
