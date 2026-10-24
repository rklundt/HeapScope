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
using System.Windows;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.UI.Wpf.Tests;

public sealed class TypeSummaryViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly SnapshotSession _session;
    private readonly TypeSummaryViewModel _types;

    public TypeSummaryViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        _session = new SnapshotSession(_worker, new JobTracker(TimeProvider.System));
        _types = new TypeSummaryViewModel(_session);
        for (var i = 0; i < 450; i++)
        {
            _worker.Service.Rows.Add([$"App.Type{i:D3}", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ((i + 1) * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        _worker.Service.Rows.Add(["Fixtures.Leaks.StaticCacheEntry", "3000", "96000"]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Showing_a_snapshot_queries_it_largest_first_and_shows_totals_and_heap_kinds()
    {
        await ShowAsync();

        _worker.Service.SummaryQueries.ShouldHaveSingleItem().ShouldBe(new TypeSummaryRequest(_worker.Service.SummaryQueries[0].SnapshotId, TypeGrouping.Type, null, "Bytes", true));
        _types.Rows.Count.ShouldBe(451);
        _types.Totals!.ShouldContain("451 of 451 types");
        _types.HasGenerations.ShouldBeTrue();
        _types.GenerationsExplanation.ShouldBeNull();
        _types.HeapKinds.Select(k => k.BrushKey).ShouldBe(["HeapKind.Gen0", "HeapKind.Gen2"]);
        _types.HeapKinds.Sum(k => k.Fraction).ShouldBe(1, tolerance: 0.001);
    }

    [Fact]
    public async Task Rows_are_fetched_one_page_at_a_time_as_the_grid_asks_for_them()
    {
        await ShowAsync();
        var rows = _types.Rows;

        var first = rows.GetRow(0);
        await rows.WhenLoadedAsync();

        first.IsLoaded.ShouldBeTrue();
        first.Name.ShouldBe("Fixtures.Leaks.StaticCacheEntry");
        rows.RequestedPages.ShouldBe([0]);
        rows.GetRow(VirtualRowList.PageSize * 2);
        await rows.WhenLoadedAsync();
        rows.RequestedPages.Order().ShouldBe([0, 2]);
        _worker.Service.PageRequests.ShouldAllBe(r => r.Count <= VirtualRowList.PageSize);
    }

    [Fact]
    public async Task Clicking_a_column_sorts_in_the_worker_and_a_second_click_flips_the_direction()
    {
        await ShowAsync();

        _types.SortByCommand.Execute("Name");
        await _types.QueryTask;
        _types.SortByCommand.Execute("Name");
        await _types.QueryTask;

        _worker.Service.SummaryQueries.Skip(1).Select(q => (q.SortColumn, q.Descending)).ShouldBe([("Name", false), ("Name", true)]);
    }

    [Fact]
    public async Task Typing_a_filter_queries_once_after_a_pause_and_changing_the_grouping_requeries()
    {
        await ShowAsync();

        _types.Filter = "S";
        _types.Filter = "SC";
        _types.Filter = "SCE";
        await WaitUntilAsync(() => _worker.Service.SummaryQueries.Count == 2);
        await _types.QueryTask;
        await Task.Delay(TypeSummaryViewModel.FilterDelay * 2, Ct);

        _worker.Service.SummaryQueries.Count.ShouldBe(2, "the keystrokes are debounced into one query");
        _worker.Service.SummaryQueries[1].Filter.ShouldBe("SCE");
        _types.Rows.Count.ShouldBe(1);

        _types.GroupBy = TypeGrouping.Namespace;
        await _types.QueryTask;
        _worker.Service.SummaryQueries[^1].GroupBy.ShouldBe(TypeGrouping.Namespace);
    }

    [Fact]
    public async Task Without_generations_the_columns_hide_and_the_reason_is_shown()
    {
        _worker.Service.HasGenerations = false;

        await ShowAsync();

        _types.HasGenerations.ShouldBeFalse();
        _types.GenerationsExplanation.ShouldNotBeNull().ShouldContain("Generations");
        _types.HeapKinds.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_query_releases_the_previous_result_set()
    {
        await ShowAsync();
        var first = _worker.Service.SummaryQueries.Count;

        _types.SortByCommand.Execute("Objects");
        await _types.QueryTask;

        _worker.Service.SummaryQueries.Count.ShouldBe(first + 1);
        await WaitUntilAsync(() => _worker.Service.Released.Count == 1);
    }

    [Fact]
    public async Task Closing_the_snapshot_while_its_query_runs_leaves_the_view_empty()
    {
        _worker.Service.SummaryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = StartShowAsync();
        await WaitUntilAsync(() => _worker.Service.SummaryQueries.Count == 1);

        _types.Show(null);
        _worker.Service.SummaryGate.SetResult();
        await (await query).QueryTask;

        _types.Rows.Count.ShouldBe(0);
        _types.Totals.ShouldBeNull();
        _types.Status.ShouldBeNull();
        _worker.Service.LiveSets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_latest_of_several_quick_queries_owns_the_view()
    {
        await ShowAsync();
        _worker.Service.SummaryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _types.SortByCommand.Execute("Name");
        var stale = _types.QueryTask;
        _types.SortByCommand.Execute("Objects");
        _worker.Service.SummaryGate.SetResult();
        await stale;
        await _types.QueryTask;

        _types.SortState.ShouldBe("Objects|desc");
        _types.Status.ShouldBeNull();
        _types.Rows.GetRow(0);
        await _types.Rows.WhenLoadedAsync();
        _types.Rows.GetRow(0).Name.ShouldBe("Fixtures.Leaks.StaticCacheEntry");
        await WaitUntilAsync(() => _worker.Service.LiveSets.Count == 1);
    }

    [Fact]
    public async Task A_page_still_loading_when_a_new_query_replaces_the_rows_is_dropped_and_leaves_no_set()
    {
        await ShowAsync();
        var oldRows = _types.Rows;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker.Service.PageGate = gate;
        var oldRow = oldRows.GetRow(oldRows.Count - 1); // past the preloaded first page

        // Only the old page waits: the new query preloads its first page before it replaces the rows.
        _worker.Service.PageGate = null;
        _types.SortByCommand.Execute("Name");
        await _types.QueryTask;
        gate.SetResult();
        await oldRows.WhenLoadedAsync();

        oldRow.IsLoaded.ShouldBeFalse();
        _types.Status.ShouldBeNull();
        await WaitUntilAsync(() => _worker.Service.LiveSets.Count == 1);
        _worker.Service.SummaryQueries.Count.ShouldBe(2, "the old page did not re-run its released query");
    }

    [Fact]
    public async Task A_page_that_fails_shows_a_message_and_is_fetched_again_when_asked_for()
    {
        await ShowAsync();
        _worker.Service.PageFailure = new FormatException("bad cell");
        var last = _types.Rows.Count - 1; // past the preloaded first page

        _types.Rows.GetRow(last);
        await _types.Rows.WhenLoadedAsync();

        _types.Status.ShouldNotBeNull().ShouldContain("could not be loaded");
        _worker.Service.PageFailure = null;
        _types.Rows.GetRow(last);
        await _types.Rows.WhenLoadedAsync();
        _types.Rows.GetRow(last).IsLoaded.ShouldBeTrue();
    }

    [Fact]
    public async Task Totals_name_the_grouping_and_count_the_rows_before_the_filter()
    {
        await ShowAsync();

        _types.SortState.ShouldBe("Bytes|desc");
        _types.Totals!.ShouldStartWith("451 of 451 types");
        _types.SortByCommand.Execute("Name");
        await _types.QueryTask;
        _types.SortState.ShouldBe("Name|asc");
    }

    [Fact]
    public void Enumerating_the_rows_fetches_nothing()
    {
        var info = new ResultSetInfo(Guid.NewGuid(), 1000, [new("Name", ResultColumnKind.Text, true), new("Objects", ResultColumnKind.Integer), new("Bytes", ResultColumnKind.Bytes)]);
        var rows = TypeRowItem.CreateList(null, info);

        rows.Cast<TypeRowItem>().Count().ShouldBe(1000);
        rows.RequestedPages.ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => TypeRowItem.CreateList(null, info with { Columns = [new("Name", ResultColumnKind.Text, true)] }));
    }

    [Fact]
    public void Both_themes_define_the_same_keys_and_a_color_for_every_heap_kind()
    {
        var light = Keys("Light.xaml");
        var dark = Keys("Dark.xaml");

        light.ShouldBe(dark);
        foreach (var kind in Enum.GetValues<HeapKindDto>())
        {
            light.ShouldContain("HeapKind." + kind);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<string> Keys(string file)
    {
        var path = Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI.Wpf", "Themes", file);
        var document = System.Xml.Linq.XDocument.Load(path);
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return [.. document.Root!.Elements().Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().Order(StringComparer.Ordinal)];
    }

    private async Task ShowAsync() => await (await StartShowAsync()).QueryTask;

    private async Task<TypeSummaryViewModel> StartShowAsync()
    {
        var dump = Path.Combine(_folder, "app.dmp");
        await File.WriteAllBytesAsync(dump, [1], Ct);
        var handle = await _session.StartOpen(dump).Result;
        _types.Show(handle);
        return _types;
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
