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

/// <summary>The Inspections tab (sprint 1.6): runs when shown, tiles, unavailable reasons, rows, drill-down.</summary>
public sealed class InspectionsViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly SnapshotsViewModel _snapshots;

    public InspectionsViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        _snapshots = new SnapshotsViewModel(new SnapshotSession(_worker, operations.Jobs), operations, new ImmediateUiDispatcher(), new FakeUserInteraction());
    }

    private InspectionsViewModel Inspections => _snapshots.Inspections;

    [Fact]
    public async Task Inspections_run_only_once_the_tab_is_shown_and_fill_their_tiles()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _worker.Service.InspectionRuns.ShouldBeEmpty("nothing runs before the tab is shown");

        _snapshots.SelectedTab = 2;
        await Inspections.RunTask;

        _worker.Service.InspectionRuns.ShouldBe(["duplicate-strings", "loh", "large-arrays", "fragmentation"], "unavailable inspections are not run");
        Inspections.Tiles.Select(t => t.Title).ShouldBe(["Duplicate strings", "Large object heap", "Empty collections", "Large arrays", "Fragmentation"]);
        var duplicates = Inspections.Tiles[0];
        (duplicates.Status, duplicates.HasFindings).ShouldBe((null, true));
        duplicates.Summary.ShouldNotBeNull().ShouldContain("wasted");
        duplicates.FirstRows.ShouldBe(["alpha"]);
        var empty = Inspections.Tiles[2];
        (empty.IsAvailable, empty.Summary).ShouldBe((false, "Field values are not stored in a gcdump."));

        Inspections.Selected.ShouldBeSameAs(duplicates, "the first inspection with findings is selected");
        Inspections.Columns.Select(c => c.Column.Name).ShouldBe(["Value", "Copies", "Bytes", "Wasted", InspectionColumns.Address], "hidden columns are not shown");
        Inspections.Rows.GetRow(0);
        await Inspections.Rows.WhenLoadedAsync();
        Inspections.Rows.GetRow(0).Display[1].ShouldBe(3.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

        _snapshots.SelectedTab = 0;
        _snapshots.SelectedTab = 2;
        await Inspections.RunTask;
        _worker.Service.InspectionRuns.Count.ShouldBe(4, "a snapshot's inspections run once");

        // The fragmentation rows become bars.
        Inspections.HasFragments.ShouldBeTrue();
        Inspections.Fragments.Select(f => (f.Heap, f.Live, f.Free, f.PinnedObjects)).ShouldBe([("Gen0", 1000L, (long?)100, 0L), ("Gen2", 8000L, (long?)3000, 1L)]);
        Inspections.Fragments[1].FreeFraction.ShouldBe(3000.0 / 11000, 1e-9);
    }

    [Fact]
    public async Task A_row_naming_one_object_opens_that_object_and_a_tile_selected_while_running_fills_when_done()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _worker.Service.InspectionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshots.SelectedTab = 2;
        await WaitUntilAsync(() => Inspections.Tiles.Count == 5);
        Inspections.Selected = Inspections.Tiles[3];
        Inspections.Rows.Count.ShouldBe(0, "not run yet");

        _worker.Service.InspectionGate.SetResult();
        await Inspections.RunTask;

        Inspections.Rows.Count.ShouldBe(1, "the selected tile shows its rows once its run is done");
        Inspections.Rows.GetRow(0);
        await Inspections.Rows.WhenLoadedAsync();
        Inspections.OpenRowCommand.Execute(Inspections.Rows.GetRow(0));
        _snapshots.Navigator.Current.ShouldBeOfType<ObjectViewModel>().Address.ShouldBe(0x20UL);
    }

    [Fact]
    public async Task Placeholder_rows_have_a_blank_cell_for_every_column()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _snapshots.SelectedTab = 2;
        await Inspections.RunTask;

        Inspections.Rows.Cast<InspectionRowItem>().First().Display.Count.ShouldBe(8, "the view binds Display[i] before the page arrives");
    }

    [Fact]
    public async Task A_row_opens_its_objects_on_the_snapshot_tab_in_its_scope()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _snapshots.SelectedTab = 2;
        await Inspections.RunTask;

        Inspections.Rows.GetRow(0);
        await Inspections.Rows.WhenLoadedAsync();
        Inspections.OpenRowCommand.Execute(Inspections.Rows.GetRow(0));

        _snapshots.SelectedTab.ShouldBe(0);
        var instances = _snapshots.Navigator.Current.ShouldBeOfType<InstancesViewModel>();
        (instances.TypeName, instances.Scope, instances.SameValueAs).ShouldBe(("System.String", InstanceScope.SameValue, 0x10UL));
        await instances.QueryTask;
        var query = _worker.Service.InstanceQueries.ShouldHaveSingleItem();
        (query.Scope, query.SameValueAs, query.Module).ShouldBe((InstanceScope.SameValue, 0x10UL, "System.Private.CoreLib.dll"));

        _snapshots.SelectedTab = 2;
        Inspections.Selected = Inspections.Tiles[1];
        Inspections.Rows.GetRow(0);
        await Inspections.Rows.WhenLoadedAsync();
        Inspections.OpenRowCommand.Execute(Inspections.Rows.GetRow(0));
        _snapshots.Navigator.Current.ShouldBeOfType<InstancesViewModel>().Title.ShouldBe("App.Buffer [App.dll] on the large object heap");
    }

    [Fact]
    public async Task Another_snapshot_cancels_the_run_and_starts_its_own()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _worker.Service.InspectionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshots.SelectedTab = 2;
        var first = Inspections.RunTask;
        var firstTiles = Inspections.Tiles.ToList();

        _worker.Service.InspectionGate = null;
        await _snapshots.OpenPathAsync(Dump("b.dmp"));
        await first;
        await Inspections.RunTask;

        firstTiles[0].Run.ShouldBeNull("the canceled run kept nothing");
        Inspections.Snapshot!.Path.ShouldEndWith("b.dmp");
        Inspections.Tiles.Count.ShouldBe(5);
        Inspections.Tiles[0].Run.ShouldNotBeNull();
        var kept = Inspections.Tiles.Where(t => t.Run is not null).Select(t => t.Run!.Rows.Info.Id).ToList();
        kept.ShouldAllBe(id => _worker.Service.LiveSets.Contains(id));

        _snapshots.Selected = null;
        Inspections.Tiles.ShouldBeEmpty();
        await WaitUntilAsync(() => kept.All(id => !_worker.Service.LiveSets.Contains(id)));
    }

    [Theory]
    [InlineData("NullSlots", "Null slots")]
    [InlineData("FreePercent", "Free %")]
    [InlineData("PinnedObjects", "Pinned objects")]
    [InlineData("Value", "Value")]
    public void Column_headers_split_names_into_words(string name, string header)
    {
        ColumnHeaders.Header(name).ShouldBe(header);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private string Dump(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }
}
