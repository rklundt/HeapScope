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

/// <summary>The Compare tab (sprint 1.4): pickers, swap, sorting by column name, and closing a compared snapshot.</summary>
public sealed class CompareViewModelTests : IDisposable
{
    private static readonly DateTimeOffset s_morning = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly SnapshotsViewModel _snapshots;

    public CompareViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        _snapshots = new SnapshotsViewModel(new SnapshotSession(_worker, operations.Jobs), operations, new ImmediateUiDispatcher(), new FakeUserInteraction());

        // The later file name is captured earlier, so the default baseline comes from the capture time, not open order.
        _worker.Service.CapturedAt = path => Path.GetFileName(path) == "later-name.dmp" ? s_morning : s_morning.AddHours(1);
        _worker.Service.DiffRows.Add(["App.Order", "", "2", "5", "3", "80", "200", "120"]);
        _worker.Service.DiffRows.Add(["App.Cache.Entry", "", "1", "2", "1", "500", "1000", "500"]);
        _worker.Service.DiffRows.Add(["App.Old", "gone", "1", "0", "-1", "100", "0", "-100"]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CompareViewModel Compare => _snapshots.Compare;

    [Fact]
    public async Task With_one_snapshot_open_there_is_nothing_to_compare_and_a_hint_says_why()
    {
        await _snapshots.OpenPathAsync(Dump("later-name.dmp"));

        Compare.Hint.ShouldNotBeNull().ShouldContain("Open two snapshots");
        Compare.Baseline.ShouldBeNull();
        _worker.Service.DiffQueries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Opening_a_second_snapshot_compares_the_earlier_capture_against_the_later_one_by_bytes_growth()
    {
        await OpenBothAsync();

        Compare.Baseline!.Title.ShouldBe("later-name.dmp");
        Compare.Current!.Title.ShouldBe("earlier-name.dmp");
        Compare.Hint.ShouldBeNull();
        var query = _worker.Service.DiffQueries.ShouldHaveSingleItem();
        query.BaselineId.ShouldBe(Compare.Baseline.Handle.Descriptor.SnapshotId);
        query.SortColumn.ShouldBe(TypeDiffColumns.BytesDelta);
        query.Descending.ShouldBeTrue();
        Compare.SortState.ShouldBe("BytesDelta|desc");
        Compare.Totals!.ShouldContain("3 of 3 types (0 new, 1 gone)");

        var first = Compare.Rows.GetRow(0);
        await Compare.Rows.WhenLoadedAsync();
        (first.Name, first.ObjectsDelta, first.BytesDelta, first.Trend).ShouldBe(("App.Cache.Entry", 1L, 500L, 1));
        Compare.Rows.GetRow(2);
        await Compare.Rows.WhenLoadedAsync();
        (Compare.Rows.GetRow(2).Change, Compare.Rows.GetRow(2).Trend).ShouldBe(("gone", -1));
    }

    [Fact]
    public async Task When_a_capture_time_is_only_the_files_time_the_snapshot_opened_first_is_the_baseline()
    {
        // Sprint 2.3.6 (2.3.5 review M4): a copied core gets a new file time, so the times do not pick the baseline.
        _worker.Service.CaptureTimeFromFile = path => Path.GetFileName(path) == "earlier-name.dmp";

        await OpenBothAsync();

        Compare.Baseline!.Title.ShouldBe("earlier-name.dmp", "opened first");
        Compare.Current!.Title.ShouldBe("later-name.dmp");
        Compare.Hint.ShouldNotBeNull().ShouldContain("opened first is the baseline");
    }

    [Fact]
    public async Task Swapping_queries_once_with_the_sides_exchanged()
    {
        await OpenBothAsync();
        var baseline = Compare.Baseline!;

        Compare.SwapCommand.Execute(null);
        await Compare.QueryTask;

        Compare.Current.ShouldBeSameAs(baseline);
        _worker.Service.DiffQueries.Count.ShouldBe(2, "a swap is one query, not two");
        _worker.Service.DiffQueries[1].CurrentId.ShouldBe(baseline.Handle.Descriptor.SnapshotId);
    }

    [Fact]
    public async Task Header_clicks_sort_by_the_column_name_and_a_second_click_flips_the_direction()
    {
        await OpenBothAsync();

        Compare.SortByCommand.Execute(TypeDiffColumns.ObjectsDelta);
        await Compare.QueryTask;
        Compare.SortState.ShouldBe("ObjectsDelta|desc");
        Compare.SortByCommand.Execute(TypeDiffColumns.ObjectsDelta);
        await Compare.QueryTask;
        Compare.SortByCommand.Execute(TypeDiffColumns.Name);
        await Compare.QueryTask;

        _worker.Service.DiffQueries.Skip(1).Select(q => (q.SortColumn, q.Descending)).ShouldBe(
            [(TypeDiffColumns.ObjectsDelta, true), (TypeDiffColumns.ObjectsDelta, false), (TypeDiffColumns.Name, false)]);
        Compare.Rows.GetRow(0);
        await Compare.Rows.WhenLoadedAsync();
        Compare.Rows.GetRow(0).Name.ShouldBe("App.Cache.Entry");
    }

    [Fact]
    public async Task Closing_a_compared_snapshot_clears_the_comparison_and_releases_its_rows()
    {
        await OpenBothAsync();

        await _snapshots.CloseCommand.ExecuteAsync(Compare.Current);

        Compare.Current.ShouldBeNull();
        Compare.Rows.Count.ShouldBe(0);
        Compare.Totals.ShouldBeNull();
        Compare.Hint.ShouldNotBeNull();
        _worker.Service.DiffSets.ShouldNotBeEmpty();
        _worker.Service.DiffSets.ShouldAllBe(id => !_worker.Service.LiveSets.Contains(id));
    }

    [Fact]
    public async Task Closing_a_compared_snapshot_while_its_comparison_runs_leaves_the_view_empty()
    {
        await _snapshots.OpenPathAsync(Dump("earlier-name.dmp"));
        _worker.Service.SummaryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _snapshots.OpenPathAsync(Dump("later-name.dmp"));
        var running = Compare.QueryTask;
        await WaitUntilAsync(() => _worker.Service.DiffQueries.Count == 1);

        await _snapshots.CloseCommand.ExecuteAsync(Compare.Current);
        _worker.Service.SummaryGate.SetResult();
        await running;

        Compare.Rows.Count.ShouldBe(0);
        Compare.Status.ShouldBeNull();
        Compare.Totals.ShouldBeNull();
        _worker.Service.DiffSets.ShouldAllBe(id => !_worker.Service.LiveSets.Contains(id));
    }

    [Fact]
    public async Task Only_the_latest_of_quick_sort_changes_owns_the_comparison()
    {
        await OpenBothAsync();
        _worker.Service.SummaryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Compare.SortByCommand.Execute(TypeDiffColumns.Name);
        var stale = Compare.QueryTask;
        Compare.SortByCommand.Execute(TypeDiffColumns.ObjectsDelta);
        _worker.Service.SummaryGate.SetResult();
        await stale;
        await Compare.QueryTask;

        Compare.SortState.ShouldBe("ObjectsDelta|desc");
        Compare.Rows.GetRow(0);
        await Compare.Rows.WhenLoadedAsync();
        Compare.Rows.GetRow(0).Name.ShouldBe("App.Order");
        await WaitUntilAsync(() => _worker.Service.DiffSets.Count(id => _worker.Service.LiveSets.Contains(id)) == 1);
    }

    [Fact]
    public async Task An_empty_picker_takes_the_next_open_snapshot_and_compares_again()
    {
        await OpenBothAsync();
        var baseline = Compare.Baseline!;
        await _snapshots.CloseCommand.ExecuteAsync(Compare.Current);
        Compare.Current.ShouldBeNull();

        await _snapshots.OpenPathAsync(Dump("third.dmp"));
        await Compare.QueryTask;

        Compare.Baseline.ShouldBeSameAs(baseline);
        Compare.Current!.Title.ShouldBe("third.dmp");
        Compare.Rows.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Estimated_counts_and_reloaded_copies_are_shown()
    {
        _worker.Service.DiffExact = false;
        _worker.Service.DiffRows.Add(["Plugin.Cache", "copies", "5", "5", "0", "200", "200", "0", "1", "2"]);

        await OpenBothAsync();
        Compare.Filter = "Plugin";
        await WaitUntilAsync(() => Compare.Rows.Count == 1);
        var row = Compare.Rows.GetRow(0);
        await Compare.Rows.WhenLoadedAsync();

        Compare.Totals!.ShouldContain("estimated");
        (row.Name, row.Change, row.BaselineTypes, row.CurrentTypes).ShouldBe(("Plugin.Cache", "copies", 1, 2));
    }

    [Fact]
    public async Task A_gone_row_opens_its_objects_in_the_baseline_and_a_kept_row_in_the_current_snapshot()
    {
        _worker.Service.DiffRows.Add(["App.Retired", "gone", "4", "0", "-4", "160", "0", "-160", "1", "0", "App.Retired", "App.dll"]);
        _worker.Service.DiffRows.Add(["App.Kept", "", "1", "3", "2", "40", "120", "80", "1", "1", "App.Kept", "App.dll"]);
        await OpenBothAsync();

        foreach (var name in new[] { "App.Retired", "App.Kept" })
        {
            Compare.Filter = name;
            await WaitUntilAsync(() => Compare.Rows.Count == 1 && Compare.Rows.GetRow(0) is { IsLoaded: true } row && row.Name == name);
            Compare.OpenRowCommand!.Execute(Compare.Rows.GetRow(0));
        }

        _worker.Service.InstanceQueries.Select(q => (q.TypeName, q.SnapshotId)).ShouldBe(
        [
            ("App.Retired", Compare.Baseline!.Handle.Descriptor.SnapshotId),
            ("App.Kept", Compare.Current!.Handle.Descriptor.SnapshotId),
        ]);
    }

    [Fact]
    public async Task New_and_dead_objects_are_asked_for_shown_and_opened_as_sets_on_the_right_side()
    {
        // Sprint 3.3 (ADR-032).
        _worker.Service.DiffRows.Add(["App.Kept", "", "1", "3", "2", "40", "120", "80", "1", "1", "App.Kept", "App.dll"]);
        await OpenBothAsync();
        Compare.VisibleColumnGroups.ShouldNotContain(ColumnGroups.Identity);

        Compare.MatchObjects = true;
        await WaitUntilAsync(() => _worker.Service.DiffQueries.Count == 2);
        await Compare.QueryTask;

        _worker.Service.DiffQueries[^1].MatchObjects.ShouldBeTrue();
        Compare.IdentityShown.ShouldBeTrue();
        Compare.VisibleColumnGroups.ShouldContain(ColumnGroups.Identity);
        Compare.IdentityNote!.ShouldContain("by address");
        Compare.Filter = "App.Kept";
        await WaitUntilAsync(() => Compare.Rows.Count == 1 && Compare.Rows.GetRow(0) is { IsLoaded: true, Name: "App.Kept" });
        var row = Compare.Rows.GetRow(0);
        (row.NewObjects, row.DeadObjects).ShouldBe((1L, 0L));

        Compare.OpenDeadCommand.Execute(row);
        var dead = _snapshots.Navigator.Current.ShouldBeOfType<SetViewModel>();
        (dead.Snapshot, dead.Set.Spec.Kind, dead.Set.Spec.Change, dead.Set.Baseline).ShouldBe((Compare.Baseline!.Handle, ObjectSetKind.Compared, ObjectChange.Dead, Compare.Current!.Handle));
        Compare.OpenNewCommand.Execute(row);
        var fresh = _snapshots.Navigator.Current.ShouldBeOfType<SetViewModel>();
        (fresh.Snapshot, fresh.Set.Spec.Change, fresh.Set.Baseline, fresh.Title).ShouldBe((Compare.Current.Handle, ObjectChange.New, Compare.Baseline.Handle, "New by address: App.Kept"));
    }

    [Fact]
    public async Task Snapshots_of_different_processes_say_why_there_are_no_new_and_dead_objects()
    {
        _worker.Service.IdentityNote = "these snapshots are of different processes";
        await OpenBothAsync();

        Compare.MatchObjects = true;
        await WaitUntilAsync(() => _worker.Service.DiffQueries.Count == 2);
        await Compare.QueryTask;

        (Compare.IdentityShown, Compare.IdentityNote).ShouldBe((false, "these snapshots are of different processes"));
        Compare.OpenNewCommand.Execute(await FirstRowAsync());
        Compare.Status.ShouldBe("these snapshots are of different processes");
    }

    [Fact]
    public async Task A_trend_takes_the_ticked_snapshots_oldest_first_and_a_row_opens_in_the_last()
    {
        _worker.Service.TrendRows.Add(("App.Leak", [1, 3, 5]));
        _worker.Service.CapturedAt = path => Path.GetFileName(path) switch
        {
            "third.dmp" => s_morning.AddHours(2),
            "first.dmp" => s_morning,
            _ => s_morning.AddHours(1),
        };
        foreach (var name in new[] { "third.dmp", "first.dmp", "second.dmp" })
        {
            await _snapshots.OpenPathAsync(Dump(name));
        }

        var trend = Compare.Trend;
        Compare.IsTrendShown = true;
        await WaitUntilAsync(() => _worker.Service.TrendQueries.Count == 1);
        await trend.QueryTask;

        trend.Ordered.Select(s => s.Title).ShouldBe(["first.dmp", "second.dmp", "third.dmp"]);
        _worker.Service.TrendQueries[0].SnapshotIds.ShouldBe(trend.Ordered.Select(s => s.Handle.Descriptor.SnapshotId));
        trend.Columns.Select(c => c.Column.Name).ShouldContain(TypeTrendColumns.Bytes(3));
        trend.Totals!.ShouldContain("across 3 snapshots (first.dmp, then second.dmp, then third.dmp)");
        var row = trend.Rows.GetRow(0);
        await trend.Rows.WhenLoadedAsync();
        row[TypeTrendColumns.GrowthSteps].ShouldBe("2");
        trend.OpenRowCommand!.Execute(row);
        _worker.Service.InstanceQueries.ShouldHaveSingleItem().SnapshotId.ShouldBe(trend.Ordered[^1].Handle.Descriptor.SnapshotId);

        trend.Choices.Single(c => c.Snapshot.Title == "second.dmp").IsIncluded = false;
        await WaitUntilAsync(() => _worker.Service.TrendQueries.Count == 2);
        _worker.Service.TrendQueries[^1].SnapshotIds.Count.ShouldBe(2);
        trend.Choices.Single(c => c.Snapshot.Title == "first.dmp").IsIncluded = false;
        trend.Hint.ShouldNotBeNull().ShouldContain("Tick two or more");
    }

    [Fact]
    public async Task Snapshots_from_different_session_folders_compare_like_any_two()
    {
        // Decision 6: Compare pairs any two open snapshots, whichever session folder they come from.
        var other = Path.Combine(_folder, "other-session");
        Directory.CreateDirectory(other);
        await _snapshots.OpenPathAsync(Dump("earlier-name.dmp"));
        var path = Path.Combine(other, "later-name.dmp");
        await File.WriteAllBytesAsync(path, [1], Ct);
        await _snapshots.OpenPathAsync(path);
        await Compare.QueryTask;

        _worker.Service.DiffQueries.ShouldHaveSingleItem();
        Compare.Baseline!.Handle.Path.ShouldBe(path);
    }

    private async Task<DiffRowItem> FirstRowAsync()
    {
        var row = Compare.Rows.GetRow(0);
        await Compare.Rows.WhenLoadedAsync();
        return row;
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

    [Fact]
    public async Task Interface_grouping_is_offered_only_when_both_snapshots_list_it()
    {
        await OpenBothAsync();
        Compare.Groupings.ShouldContain(TypeGrouping.Interface);

        await _snapshots.CloseCommand.ExecuteAsync(Compare.Current);
        await _snapshots.OpenPathAsync(Dump("survivors.gcdump"));
        await Compare.QueryTask;

        Compare.Current!.Title.ShouldBe("survivors.gcdump");

        Compare.Groupings.ShouldNotContain(TypeGrouping.Interface, "a gcdump lists interface grouping as unavailable");
    }

    private async Task OpenBothAsync()
    {
        await _snapshots.OpenPathAsync(Dump("earlier-name.dmp"));
        await _snapshots.OpenPathAsync(Dump("later-name.dmp"));
        await Compare.QueryTask;
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

    private string Dump(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }
}
