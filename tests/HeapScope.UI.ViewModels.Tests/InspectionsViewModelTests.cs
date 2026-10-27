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

        _snapshots.Tab = SnapshotTabs.Inspections;
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

        _snapshots.Tab = SnapshotTabs.Snapshot;
        _snapshots.Tab = SnapshotTabs.Inspections;
        await Inspections.RunTask;
        _worker.Service.InspectionRuns.Count.ShouldBe(4, "a snapshot's inspections run once");

        // The fragmentation rows become bars.
        Inspections.HasFragments.ShouldBeTrue();
        Inspections.Fragments.Select(f => (f.Heap, f.Live, f.Free, f.PinnedObjects)).ShouldBe([("Gen0", 1000L, (long?)100, 0L), ("Gen2", 8000L, (long?)3000, 1L)]);
        Inspections.Fragments[1].FreeFraction.ShouldBe(3000.0 / 11000, 1e-9);
    }

    [Fact]
    public async Task Leak_detectors_come_first_and_the_growth_detector_compares_with_the_earlier_snapshot_of_the_process()
    {
        _worker.Service.Inspections.Insert(0, new InspectionInfo("growing-static-collections", "Growing static collections", "Grows.", "Bound it.", null, InspectionKinds.Leak, UsesBaseline: true));
        _worker.Service.Inspections.Insert(1, new InspectionInfo("timer-leaks", "Timer leaks", "Timers.", "Dispose them.", null, InspectionKinds.Leak));
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        _worker.Service.CapturedAt = path => path.EndsWith("a.dmp", StringComparison.Ordinal) ? earlier : earlier.AddMinutes(2);
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        await _snapshots.OpenPathAsync(Dump("b.dmp"));

        _snapshots.Tab = SnapshotTabs.Inspections;
        await Inspections.RunTask;

        Inspections.Tiles.Select(t => (t.Title, t.GroupHeader)).Take(3).ShouldBe([("Growing static collections", "Leak detectors"), ("Timer leaks", null), ("Duplicate strings", "Memory waste")]);
        Inspections.UsesBaseline.ShouldBeTrue();
        Inspections.Baseline!.Snapshot!.Path.ShouldEndWith("a.dmp", Case.Sensitive, "the earlier snapshot of the same process");
        var growth = _worker.Service.InspectionRequests.Single(r => r.Name == "growing-static-collections");
        growth.BaselineSnapshotId.ShouldBe(Inspections.Baseline.Snapshot.Descriptor.SnapshotId);
        _worker.Service.InspectionRequests.Single(r => r.Name == "timer-leaks").BaselineSnapshotId.ShouldBeNull("only the growth detector uses a baseline");
        Inspections.Tiles[0].Summary!.ShouldContain("growth since a.dmp");

        Inspections.Baseline = Inspections.BaselineChoices[0];
        await Inspections.RunTask;

        _worker.Service.InspectionRequests.Last().ShouldBe(new InspectionRequest(growth.SnapshotId, "growing-static-collections"));
        _worker.Service.InspectionRuns.Count(n => n == "timer-leaks").ShouldBe(1, "choosing a baseline runs only the detectors that use one");
    }

    [Fact]
    public async Task A_baseline_chosen_while_the_first_run_goes_on_reruns_the_growth_detector_and_no_tile_is_left_waiting()
    {
        _worker.Service.Inspections.Insert(0, new InspectionInfo("growing-static-collections", "Growing static collections", "Grows.", "Bound it.", null, InspectionKinds.Leak, UsesBaseline: true));
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        _worker.Service.CapturedAt = path => path.EndsWith("a.dmp", StringComparison.Ordinal) ? earlier : earlier.AddMinutes(2);
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        await _snapshots.OpenPathAsync(Dump("b.dmp"));
        _worker.Service.InspectionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _snapshots.Tab = SnapshotTabs.Inspections;
        Inspections.Baseline = Inspections.BaselineChoices[0];
        _worker.Service.InspectionGate.SetResult();
        await Inspections.RunTask;

        Inspections.Tiles.ShouldAllBe(t => t.Status == null, "every tile finished (review H6)");
        var growth = _worker.Service.InspectionRequests.Where(r => r.Name == "growing-static-collections").ToList();
        growth.Count.ShouldBe(2, "run with the first baseline, then again with the one chosen meanwhile");
        growth[^1].BaselineSnapshotId.ShouldBeNull();
        Inspections.Tiles[0].Summary!.ShouldNotContain("growth since");
    }

    [Fact]
    public async Task A_row_naming_one_object_opens_that_object_and_a_tile_selected_while_running_fills_when_done()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _worker.Service.InspectionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshots.Tab = SnapshotTabs.Inspections;
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
        _snapshots.Tab = SnapshotTabs.Inspections;
        await Inspections.RunTask;

        Inspections.Rows.Cast<CellRowItem>().First().Display.Count.ShouldBe(8, "the view binds Display[i] before the page arrives");
    }

    [Fact]
    public async Task A_row_opens_its_objects_on_the_snapshot_tab_in_its_scope()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _snapshots.Tab = SnapshotTabs.Inspections;
        await Inspections.RunTask;

        Inspections.Rows.GetRow(0);
        await Inspections.Rows.WhenLoadedAsync();
        Inspections.OpenRowCommand.Execute(Inspections.Rows.GetRow(0));

        _snapshots.Tab.ShouldBe(SnapshotTabs.Snapshot);
        var instances = _snapshots.Navigator.Current.ShouldBeOfType<InstancesViewModel>();
        (instances.TypeName, instances.Scope, instances.SameValueAs).ShouldBe(("System.String", InstanceScope.SameValue, 0x10UL));
        await instances.QueryTask;
        var query = _worker.Service.InstanceQueries.ShouldHaveSingleItem();
        (query.Scope, query.SameValueAs, query.Module).ShouldBe((InstanceScope.SameValue, 0x10UL, "System.Private.CoreLib.dll"));

        _snapshots.Tab = SnapshotTabs.Inspections;
        Inspections.Selected = Inspections.Tiles[1];
        Inspections.Rows.GetRow(0);
        await Inspections.Rows.WhenLoadedAsync();
        Inspections.OpenRowCommand.Execute(Inspections.Rows.GetRow(0));
        _snapshots.Navigator.Current.ShouldBeOfType<InstancesViewModel>().Title.ShouldBe("App.Buffer [App.dll] on the large object heap");
    }

    [Fact]
    public async Task A_scoped_row_does_not_offer_its_whole_types_set_and_a_row_with_an_address_shows_its_object_in_the_tree()
    {
        // The 2.4 review: "Objects retained by this set" on a scoped row would answer for every object of the type.
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _snapshots.Tab = SnapshotTabs.Inspections;
        await Inspections.RunTask;
        var scoped = Inspections.Rows.GetRow(0);
        await Inspections.Rows.WhenLoadedAsync();

        Inspections.RetainedBySetCommand.CanExecute(scoped).ShouldBeFalse("a duplicate string row stands for some strings, not all");
        Inspections.ShowInRetainersCommand.Execute(scoped);

        _snapshots.Tab.ShouldBe(SnapshotTabs.Retainers);
        _snapshots.Retainers.FocusAddress.ShouldBe(0x10UL, "the row names an object, so the tree shows it");
    }

    [Fact]
    public async Task Another_snapshot_cancels_the_run_and_starts_its_own()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _worker.Service.InspectionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshots.Tab = SnapshotTabs.Inspections;
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

    [Fact]
    public async Task The_baseline_choices_follow_snapshots_opened_and_closed_and_keep_a_chosen_one()
    {
        // 2.6 review M12.
        _worker.Service.Inspections.Insert(0, new InspectionInfo("growing-static-collections", "Growing static collections", "Grows.", "Bound it.", null, InspectionKinds.Leak, UsesBaseline: true));
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        _worker.Service.CapturedAt = path => path.EndsWith("a.dmp", StringComparison.Ordinal) ? earlier : earlier.AddMinutes(2);
        await _snapshots.OpenPathAsync(Dump("b.dmp"));
        _snapshots.Tab = SnapshotTabs.Inspections;
        await Inspections.RunTask;
        Inspections.BaselineChoices.ShouldHaveSingleItem().Text.ShouldBe("None");

        // The earlier capture opens: it is listed, chosen and the growth detector runs again with it.
        var current = _snapshots.Selected!;
        await _snapshots.OpenPathAsync(Dump("a.dmp"));
        _snapshots.Selected = current;
        await Inspections.RunTask;
        Inspections.BaselineChoices.Count.ShouldBe(2);
        Inspections.Baseline!.Snapshot!.Path.ShouldEndWith("a.dmp", Case.Sensitive);
        _worker.Service.InspectionRequests.Last(r => r.Name == "growing-static-collections").BaselineSnapshotId.ShouldBe(Inspections.Baseline.Snapshot.Descriptor.SnapshotId);

        // A baseline the person chose (None) stays while other snapshots close; a chosen snapshot that closes gives way.
        await _snapshots.OpenPathAsync(Dump("c.dmp"));
        _snapshots.Selected = current;
        Inspections.Baseline!.Snapshot!.Path.ShouldEndWith("a.dmp", Case.Sensitive, "selecting a snapshot chooses its default again");
        Inspections.Baseline = Inspections.BaselineChoices[0];
        await Inspections.RunTask;
        await _snapshots.CloseCommand.ExecuteAsync(_snapshots.Snapshots.Single(s => s.Handle.Path.EndsWith("c.dmp", StringComparison.Ordinal)));
        Inspections.Baseline.Snapshot.ShouldBeNull("the person's choice stays");
        Inspections.BaselineChoices.Count.ShouldBe(2);
        Inspections.Baseline = Inspections.BaselineChoices.Single(c => c.Snapshot?.Path.EndsWith("a.dmp", StringComparison.Ordinal) == true);
        await Inspections.RunTask;
        await _snapshots.CloseCommand.ExecuteAsync(_snapshots.Snapshots.Single(s => s.Handle.Path.EndsWith("a.dmp", StringComparison.Ordinal)));
        await Inspections.RunTask;
        Inspections.BaselineChoices.ShouldNotContain(c => c.Snapshot != null && c.Snapshot.Path.EndsWith("a.dmp", StringComparison.Ordinal));
        Inspections.Baseline.Snapshot.ShouldBeNull("c.dmp is a later capture, so the default is none");
        _worker.Service.InspectionRequests.Last(r => r.Name == "growing-static-collections").BaselineSnapshotId.ShouldBeNull();
    }

    [Theory]
    [InlineData(42, "App", 42, "App", true)]
    [InlineData(43, "App", 42, "App", false)]
    [InlineData(null, "App", 42, "App", true)]
    [InlineData(null, "Other", null, "App", false)]
    [InlineData(42, null, 42, "App", true)]
    public void An_earlier_capture_of_the_same_process_matches_by_id_else_by_name(int? id, string? name, int? myId, string? myName, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        InspectionsViewModel.IsEarlierOfSameProcess(Descriptor(id, name, now.AddMinutes(-1)), Descriptor(myId, myName, now)).ShouldBe(expected);
        InspectionsViewModel.IsEarlierOfSameProcess(Descriptor(id, name, now.AddMinutes(1)), Descriptor(myId, myName, now)).ShouldBeFalse("a later capture never");
        InspectionsViewModel.IsEarlierOfSameProcess(Descriptor(id, name, now.AddMinutes(-1)) with { CaptureTimeFromFile = true }, Descriptor(myId, myName, now))
            .ShouldBeFalse("a file's time orders nothing");

        static SnapshotDescriptor Descriptor(int? id, string? name, DateTimeOffset at) =>
            new(Guid.NewGuid(), "x.dmp", SnapshotKindDto.FullDump, SnapshotFeatures.ObjectGraph, name, id, "10.0", at, 1, 1, 1, 1, 1, default, false, null, 0, []);
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
