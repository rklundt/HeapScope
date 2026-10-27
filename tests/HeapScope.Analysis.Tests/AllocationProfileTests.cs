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

    // Main > Handle > Grow allocates 200 bytes of Byte[] (two samples) and Main > Handle > Log 50 bytes of String in the
    // first second; Main > Log allocates 30 bytes of String and Handle itself 20 bytes of Byte[] in the second.
    private static readonly AllocationTrace s_trace = new(
        ["Main", "Handle", "Grow", "Log"],
        ["System.Byte[]", "System.String"],
        stackFrames: [Main, Handle, Grow, Log, Log],
        stackParents: [-1, 0, 1, 1, 0],
        sampleTimes: [0, 10, 20, 1500, 1600],
        sampleStacks: [2, 2, 3, 4, 1],
        sampleTypes: [Bytes, Bytes, Text, Text, Bytes],
        sampleBytes: [100, 100, 50, 30, 20],
        new AllocationTraceInfo(42, "LeakyApi", DateTime.UnixEpoch, 2000, 0, 0));

    [Fact]
    public void The_timeline_sums_each_stretch()
    {
        AllocationProfile.Timeline(s_trace, 1000).ShouldBe([new(0, 250, 3), new(1000, 50, 2)]);
    }

    [Fact]
    public void Types_are_ordered_by_bytes_over_the_interval()
    {
        AllocationProfile.Types(s_trace, AllocationFilter.All).ShouldBe([new(Bytes, 220, 3), new(Text, 80, 2)]);
        AllocationProfile.Types(s_trace, new AllocationFilter(1000, double.PositiveInfinity)).ShouldBe([new(Text, 30, 1), new(Bytes, 20, 1)]);
        AllocationProfile.Totals(s_trace, new AllocationFilter(0, 1000, Text)).ShouldBe((50L, 1));
    }

    [Fact]
    public void Top_stacks_are_ordered_by_bytes_and_read_innermost_first()
    {
        AllocationProfile.TopStacks(s_trace, AllocationFilter.All, 3).ShouldBe([new(2, 200, 2), new(3, 50, 1), new(4, 30, 1)]);
        AllocationProfile.TopStacks(s_trace, AllocationFilter.All with { Type = Text }, 10).Select(s => s.Node).ShouldBe([3, 4]);
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
}
