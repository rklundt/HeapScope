using System.IO;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.Services;
using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.UI.Wpf.Tests;

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
