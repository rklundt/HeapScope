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
using HeapScope.Application.Allocations;
using HeapScope.Application.Jobs;
using HeapScope.Application.Sessions;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The Allocations page (sprint 2.5): a trace's types, trees and stacks over an interval, narrowed by type.</summary>
public sealed class AllocationsViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly FakeUserInteraction _interaction = new();
    private readonly AllocationsViewModel _page;
    private int _shown;

    public AllocationsViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var jobs = new JobTracker(TimeProvider.System);
        var session = new AllocationSession(_worker, jobs, new TraceService(), new SessionLibrary(Path.Combine(_folder, "sessions")));
        _page = new AllocationsViewModel(session, new ImmediateUiDispatcher(), _interaction);
        _page.TraceShown += (_, _) => _shown++;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeWorkerService Service => _worker.Service;

    [Fact]
    public async Task Opening_a_trace_shows_its_types_back_traces_stacks_and_timeline()
    {
        _page.IsEmpty.ShouldBeTrue();

        var trace = await OpenAsync();

        _page.IsEmpty.ShouldBeFalse();
        _page.Selected.ShouldBe(trace);
        _shown.ShouldBe(1, "the shell shows the page");
        Service.TracesOpened.ShouldHaveSingleItem().Path.ShouldEndWith("app.nettrace");
        trace.Summary.ShouldContain("App (42)");
        trace.Note.ShouldContain("Estimated from samples");
        trace.Interval.ShouldBe("The whole trace");
        trace.Buckets.Count.ShouldBe(2);
        (await TypesAsync(trace)).ShouldBe(["System.Byte[]", "System.String"]);
        trace.Types.Columns.Single(c => c.Column.Name == AllocationTypeColumns.Bytes).Column.Bound.ShouldBe(ValueBound.Estimate);
        (await TreeAsync(trace)).Select(r => (r.Name, r.Total, r.Own)).ShouldBe([("App.Grow", 300L, (long?)300), ("App.Log", 100L, (long?)100)]);
        Service.AllocationTreeQueries.ShouldHaveSingleItem().Kind.ShouldBe(AllocationTreeKindDto.BackTraces);
        trace.Stacks.Select(s => (s.Rank, s.Top, s.Caller, s.Percent)).ShouldBe([(1, "App.Grow", "App.Handle", 75.0), (2, "App.Log", "App.Main", 25.0)]);
        trace.SelectedStack.ShouldBe(trace.Stacks[0]);
    }

    [Fact]
    public async Task Selecting_a_type_narrows_the_trees_and_stacks_and_all_types_restores_them()
    {
        var trace = await OpenAsync();
        await TypesAsync(trace);

        trace.Types.SelectedRow = trace.Types.Rows.GetRow(1);
        await SettleAsync(trace);

        trace.TypeName.ShouldBe("System.String");
        Service.AllocationTreeQueries[^1].TypeName.ShouldBe("System.String");
        Service.StackQueries[^1].TypeName.ShouldBe("System.String");
        trace.Stacks.Select(s => s.Top).ShouldBe(["App.Log"]);
        Service.TypeQueries.Count.ShouldBe(1, "the types list is what one picks from: it does not narrow itself");

        trace.AllTypesCommand.Execute(null);
        await SettleAsync(trace);

        trace.TypeName.ShouldBeNull();
        Service.StackQueries[^1].TypeName.ShouldBeNull();
        trace.Stacks.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Selecting_an_interval_asks_every_view_about_it()
    {
        var trace = await OpenAsync();

        trace.Selection = new TimeInterval(6, 2);
        await SettleAsync(trace);

        trace.Interval!.ShouldContain("2");
        (Service.TypeQueries[^1].FromSeconds, Service.TypeQueries[^1].ToSeconds).ShouldBe((2.0, (double?)6));
        (Service.AllocationTreeQueries[^1].FromSeconds, Service.AllocationTreeQueries[^1].ToSeconds).ShouldBe((2.0, (double?)6));
        (Service.StackQueries[^1].FromSeconds, Service.StackQueries[^1].ToSeconds).ShouldBe((2.0, (double?)6));

        trace.WholeTraceCommand.Execute(null);
        await SettleAsync(trace);

        trace.Interval.ShouldBe("The whole trace");
        Service.StackQueries[^1].ToSeconds.ShouldBeNull();
    }

    [Fact]
    public async Task Expanding_a_row_shows_its_callers_and_keeps_it_selected()
    {
        var trace = await OpenAsync();
        var grow = (await TreeAsync(trace))[0];

        trace.Tree.ToggleCommand.Execute(grow);
        await trace.Tree.QueryTask;
        await trace.Tree.SelectTask;

        var query = Service.AllocationTreeQueries[^1];
        query.Expanded.ShouldBe(["b:1"]);
        query.RevealKey.ShouldBe("b:1");
        (await TreeAsync(trace)).Select(r => (r.Depth, r.Name)).ShouldBe([(0, "App.Grow"), (1, "App.Handle"), (0, "App.Log")]);
        trace.Tree.SelectedRow!.Key.ShouldBe("b:1");
        trace.Tree.CollapseCommand.CanExecute(trace.Tree.SelectedRow).ShouldBeTrue();
        trace.Tree.ExpandCommand.CanExecute(trace.Tree.SelectedRow).ShouldBeFalse();
    }

    [Fact]
    public async Task The_call_tree_starts_closed_and_shows_own_bytes()
    {
        var trace = await OpenAsync();
        trace.Tree.ToggleCommand.Execute((await TreeAsync(trace))[0]);
        await trace.Tree.QueryTask;

        trace.ShowCallTree = true;
        await trace.Tree.QueryTask;

        var query = Service.AllocationTreeQueries[^1];
        (query.Kind, query.Expanded.Count).ShouldBe((AllocationTreeKindDto.CallTree, 0));
        (await TreeAsync(trace)).Select(r => (r.Name, r.Total, r.Own)).ShouldBe([("App.Main", 400L, (long?)0)]);
    }

    [Fact]
    public async Task Closing_a_trace_closes_it_in_the_worker_and_opening_it_again_shows_the_open_tab()
    {
        var trace = await OpenAsync();
        await _page.OpenPathAsync(trace.Trace.Path);
        _page.Traces.Count.ShouldBe(1, "a trace already open is shown, not read again");
        Service.TracesOpened.Count.ShouldBe(1);

        await _page.CloseCommand.ExecuteAsync(trace);

        _page.IsEmpty.ShouldBeTrue();
        _page.Selected.ShouldBeNull();
        Service.TracesClosed.ShouldHaveSingleItem().ShouldBe(trace.Trace.Descriptor.TraceId);
    }

    [Fact]
    public async Task A_file_that_is_not_a_trace_says_why_on_the_page()
    {
        Service.OpenFailure = new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.SnapshotFormat, "notes.txt is not an EventPipe trace (.nettrace).", null);
        _interaction.NextFile = Path.Combine(_folder, "notes.txt");

        await _page.OpenCommand.ExecuteAsync(null);

        _page.Status.ShouldBe("notes.txt is not an EventPipe trace (.nettrace).");
        _page.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void The_timeline_maps_buckets_to_bars_and_pixels_to_seconds()
    {
        AllocationBucketDto[] buckets = [new(0, 300, 3), new(5, 100, 1)];

        var bars = AllocationTimelineGeometry.Bars(buckets, 5, 10, 200, 100);

        bars.Select(b => (b.X, Math.Round(b.Height, 6))).ShouldBe([(0.0, 100.0), (100.0, 33.333333)]);
        bars[1].Y.ShouldBe(100 - bars[1].Height);
        AllocationTimelineGeometry.SecondsAt(50, 200, buckets, 5, 10).ShouldBe(2.5);
        AllocationTimelineGeometry.SecondsAt(-5, 200, buckets, 5, 10).ShouldBe(0);
        AllocationTimelineGeometry.SecondsAt(500, 200, buckets, 5, 10).ShouldBe(10);
        AllocationTimelineGeometry.XOf(5, 200, buckets, 5, 10).ShouldBe(100);
        AllocationTimelineGeometry.Bars([], 5, 10, 200, 100).ShouldBeEmpty();
        new TimeInterval(6, -1).ShouldBe(new TimeInterval(0, 6), "ends in either order, never before the start");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<AllocationTraceViewModel> OpenAsync()
    {
        var path = Path.Combine(_folder, "app.nettrace");
        await File.WriteAllBytesAsync(path, [1], Ct);
        await _page.OpenPathAsync(path);
        var trace = _page.Selected.ShouldNotBeNull();
        await SettleAsync(trace);
        return trace;
    }

    private static async Task SettleAsync(AllocationTraceViewModel trace)
    {
        await trace.LoadTask;
        await trace.Types.QueryTask;
        await trace.Tree.QueryTask;
    }

    private static async Task<List<string?>> TypesAsync(AllocationTraceViewModel trace)
    {
        var rows = Enumerable.Range(0, trace.Types.Rows.Count).Select(trace.Types.Rows.GetRow).ToList();
        await trace.Types.Rows.WhenLoadedAsync();
        return [.. rows.Select(r => r[AllocationTypeColumns.Type])];
    }

    private static async Task<List<AllocationRowItem>> TreeAsync(AllocationTraceViewModel trace)
    {
        var rows = Enumerable.Range(0, trace.Tree.Rows.Count).Select(trace.Tree.Rows.GetRow).ToList();
        await trace.Tree.Rows.WhenLoadedAsync();
        return rows;
    }
}
