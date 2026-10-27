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

using HeapScope.Analysis.Allocations;
using HeapScope.Core.Allocations;

namespace HeapScope.Analysis.Tests;

/// <summary>Sampled allocations (sprint 2.5): the timeline, types, top stacks, call tree and back traces of an interval.</summary>
public sealed class AllocationProfileTests
{
    private const int Main = 0;
    private const int Handle = 1;
    private const int Grow = 2;
    private const int Log = 3;
    private const int Bytes = 0;
    private const int Text = 1;

    private static readonly HashSet<string> s_none = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Main > Handle > Grow allocates 200 bytes of Byte[] (two samples, one on the large object heap) and Main > Handle > Log
    // 50 bytes of String in the first second; Main > Log allocates 30 bytes of String and Handle itself 20 bytes of Byte[]
    // (pinned) in the second.
    private static readonly AllocationTrace s_trace = new(
        ["Main", "Handle", "Grow", "Log"],
        ["System.Byte[]", "System.String"],
        stackFrames: [Main, Handle, Grow, Log, Log],
        stackParents: [-1, 0, 1, 1, 0],
        sampleTimes: [0, 10, 20, 1500, 1600],
        sampleStacks: [2, 2, 3, 4, 1],
        sampleTypes: [Bytes, Bytes, Text, Text, Bytes],
        sampleBytes: [100, 100, 50, 30, 20],
        sampleKinds: [AllocationKind.Small, AllocationKind.Large, AllocationKind.Small, AllocationKind.Small, AllocationKind.Pinned],
        new AllocationTraceInfo(42, "LeakyApi", DateTime.UnixEpoch, 2000, 0, 0));

    [Fact]
    public void The_timeline_sums_each_stretch()
    {
        AllocationProfile.Timeline(s_trace, 1000, Ct).ShouldBe([new(0, 250, 3), new(1000, 50, 2)]);
        AllocationProfile.BucketCount(s_trace, 10).ShouldBe(200);
    }

    [Fact]
    public void Types_are_ordered_by_bytes_over_the_interval()
    {
        AllocationProfile.Types(s_trace, AllocationFilter.All, Ct).ShouldBe([new(220, 100, 20, Bytes, 3), new(80, 0, 0, Text, 2)]);
        AllocationProfile.Types(s_trace, new AllocationFilter(1000, double.PositiveInfinity), Ct).ShouldBe([new(30, 0, 0, Text, 1), new(20, 0, 20, Bytes, 1)]);
        AllocationProfile.Totals(s_trace, new AllocationFilter(0, 1000, Text), Ct).ShouldBe((50L, 1));
        AllocationProfile.Types(s_trace, new AllocationFilter(500, 900), Ct).ShouldBeEmpty("no sample falls in the interval");
    }

    [Fact]
    public void Top_stacks_are_ordered_by_bytes_and_read_innermost_first()
    {
        AllocationProfile.TopStacks(s_trace, AllocationFilter.All, 3, Ct).ShouldBe([new(200, 2, 2), new(50, 3, 1), new(30, 4, 1)]);
        AllocationProfile.TopStacks(s_trace, AllocationFilter.All with { Type = Text }, 10, Ct).Select(s => s.Node).ShouldBe([3, 4]);
        AllocationProfile.FramesOf(s_trace, 2).ShouldBe([Grow, Handle, Main]);
    }

    [Fact]
    public void The_call_tree_shows_total_and_own_top_down()
    {
        var view = AllocationTreeRows.Build(s_trace, AllocationFilter.All, AllocationTreeKind.CallTree, new HashSet<string> { "n:0", "n:1" }, 100, Ct);

        view.TotalBytes.ShouldBe(300);
        view.TotalSamples.ShouldBe(5);
        view.Rows.Select(r => (r.Depth, r.Name, r.Total, r.Own, r.Children)).ShouldBe(
        [
            (0, "Main", 300L, 0L, 2),
            (1, "Handle", 270L, 20L, 2),
            (2, "Grow", 200L, 200L, 0),
            (2, "Log", 50L, 50L, 0),
            (1, "Log", 30L, 30L, 0),
        ]);
        view.Rows[2].ParentKey.ShouldBe("n:1");
    }

    [Fact]
    public void Back_traces_start_at_the_allocating_frames_and_merge_their_callers()
    {
        var top = AllocationTreeRows.Build(s_trace, AllocationFilter.All, AllocationTreeKind.BackTraces, s_none, 100, Ct);
        top.Rows.Select(r => (r.Name, r.Total, r.Own, r.Children)).ShouldBe([("Grow", 200L, 200L, 1), ("Log", 80L, 80L, 2), ("Handle", 20L, 20L, 1)]);

        var log = AllocationTreeRows.BackTraceKey(AllocationTreeRows.RootKey, Log);
        var handle = AllocationTreeRows.BackTraceKey(log, Handle);
        var open = AllocationTreeRows.Build(s_trace, AllocationFilter.All, AllocationTreeKind.BackTraces, new HashSet<string> { log, handle }, 100, Ct);
        open.Rows.Select(r => (r.Depth, r.Name, r.Total, r.Own)).ShouldBe(
        [
            (0, "Grow", 200L, 200L),
            (0, "Log", 80L, 80L),
            (1, "Handle", 50L, -1L),
            (2, "Main", 50L, -1L),
            (1, "Main", 30L, -1L),
            (0, "Handle", 20L, 20L),
        ]);
    }

    [Fact]
    public void A_type_and_an_interval_narrow_the_trees()
    {
        var view = AllocationTreeRows.Build(s_trace, new AllocationFilter(1000, double.PositiveInfinity, Text), AllocationTreeKind.CallTree, new HashSet<string> { "n:0" }, 100, Ct);

        view.Rows.Select(r => (r.Name, r.Total)).ShouldBe([("Main", 30L), ("Log", 30L)]);
        view.TotalBytes.ShouldBe(30);
    }

    [Fact]
    public void A_long_list_pages_with_a_more_row_that_opens_the_next_batch()
    {
        var first = AllocationTreeRows.Build(s_trace, AllocationFilter.All, AllocationTreeKind.BackTraces, s_none, 1, Ct);
        first.Rows.Select(r => (r.Kind, r.Name, r.Total)).ShouldBe([(AllocationRowKind.Frame, "Grow", 200L), (AllocationRowKind.More, "2 more frames", 100L)]);

        var more = AllocationTreeRows.Build(s_trace, AllocationFilter.All, AllocationTreeKind.BackTraces, new HashSet<string> { first.Rows[1].Key }, 1, Ct);
        more.Rows.Select(r => r.Name).ShouldBe(["Grow", "Log", "1 more frames"]);
    }

    [Fact]
    public void A_recursive_stack_keeps_each_call_and_back_traces_add_up()
    {
        // Main > Walk > Walk > Alloc: the same method twice on one stack.
        var trace = new AllocationTrace(
            ["Main", "Walk", "Alloc"],
            ["T"],
            stackFrames: [0, 1, 1, 2],
            stackParents: [-1, 0, 1, 2],
            sampleTimes: [0],
            sampleStacks: [3],
            sampleTypes: [0],
            sampleBytes: [64],
            sampleKinds: [AllocationKind.Small],
            new AllocationTraceInfo(1, null, DateTime.UnixEpoch, 10, 0, 0));
        var alloc = AllocationTreeRows.BackTraceKey(AllocationTreeRows.RootKey, 2);
        var walk = AllocationTreeRows.BackTraceKey(alloc, 1);
        var walkAgain = AllocationTreeRows.BackTraceKey(walk, 1);

        var view = AllocationTreeRows.Build(trace, AllocationFilter.All, AllocationTreeKind.BackTraces, new HashSet<string> { alloc, walk, walkAgain }, 100, Ct);

        view.Rows.Select(r => (r.Depth, r.Name, r.Total)).ShouldBe([(0, "Alloc", 64L), (1, "Walk", 64L), (2, "Walk", 64L), (3, "Main", 64L)]);
    }

    [Fact]
    public void The_trace_refuses_samples_out_of_time_order_and_parents_after_children()
    {
        Should.Throw<ArgumentException>(() => new AllocationTrace(
            ["A"], ["T"], [0], [-1], [5, 1], [0, 0], [0, 0], [1, 1], [AllocationKind.Small, AllocationKind.Small],
            new AllocationTraceInfo(1, null, DateTime.UnixEpoch, 10, 0, 0))).Message.ShouldContain("time order");
        Should.Throw<ArgumentException>(() => new AllocationTrace(
            ["A"], ["T"], [0, 0], [1, -1], [0], [0], [0], [1], [AllocationKind.Small],
            new AllocationTraceInfo(1, null, DateTime.UnixEpoch, 10, 0, 0))).Message.ShouldContain("parent must come before");
    }

    [Fact]
    public void A_tree_past_the_row_limit_is_cut_and_says_so()
    {
        var count = AllocationTreeRows.MaxRows + 10;
        var trace = new AllocationTrace(
            [.. Enumerable.Range(0, count).Select(i => "M" + i)],
            ["T"],
            stackFrames: [.. Enumerable.Range(0, count)],
            stackParents: [.. Enumerable.Repeat(-1, count)],
            sampleTimes: [.. Enumerable.Range(0, count).Select(i => (double)i)],
            sampleStacks: [.. Enumerable.Range(0, count)],
            sampleTypes: new int[count],
            sampleBytes: [.. Enumerable.Repeat(1L, count)],
            sampleKinds: new AllocationKind[count],
            new AllocationTraceInfo(1, null, DateTime.UnixEpoch, count, 0, 0));
        var batches = Enumerable.Range(1, (count / 1000) + 1).Select(b => AllocationTreeRows.MoreKey(AllocationTreeRows.RootKey, b * 1000)).ToHashSet();

        var view = AllocationTreeRows.Build(trace, AllocationFilter.All, AllocationTreeKind.CallTree, batches, 1000, Ct);

        view.Truncated.ShouldBeTrue();
        view.Rows.Count.ShouldBe(AllocationTreeRows.MaxRows);
    }
}
