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

using HeapScope.Analysis.Retention;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Analysis.Tests;

/// <summary>The icicle chart's layout (sprint 2.4).</summary>
public sealed class IcicleLayoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A cache (40 bytes) holds three entries of 100, 100 and 1 bytes; the first entry holds a 200-byte payload. Another root
    // holds a 59-byte object. The reachable heap is 500 bytes.
    private static (HeapGraph Graph, Dominators Dominators, SyntheticObject Cache, SyntheticObject Payload) Heap()
    {
        var builder = new SyntheticGraphBuilder();
        var cache = builder.Object("App.Cache", size: 40);
        var big = builder.Object("App.Entry", size: 100);
        var payload = builder.Object("System.Byte[]", size: 200);
        var middle = builder.Object("App.Entry", size: 100);
        var tiny = builder.Object("App.Entry", size: 1);
        var other = builder.Object("App.Other", size: 59);
        builder.Reference(cache, big).Reference(cache, middle).Reference(cache, tiny).Reference(big, payload);
        builder.Root(cache).Root(other, RootKind.StrongHandle);
        var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        return (graph, Dominators.Compute(graph, reverse, cancellationToken: Ct), cache, payload);
    }

    [Fact]
    public void Children_sit_side_by_side_inside_their_parent_as_wide_as_what_they_retain()
    {
        var (graph, dominators, cache, payload) = Heap();

        var segments = IcicleLayout.Build(graph, dominators, -1, levels: 6, minShare: 0, Ct);

        segments.Select(s => (s.Level, s.Offset, s.Width)).ShouldBe(
        [
            (0, 0UL, 500UL), // The whole reachable heap.
            (1, 0UL, 441UL), // The cache: 40 + 300 + 100 + 1.
            (1, 441UL, 59UL),
            (2, 0UL, 300UL), // The first entry with its payload.
            (2, 300UL, 100UL),
            (2, 400UL, 1UL),
            (3, 0UL, 200UL),
        ]);
        (segments[0].Object, segments[1].Object, segments[6].Object).ShouldBe((-1, cache.Id, payload.Id));
    }

    [Fact]
    public void Segments_below_the_share_are_left_out_with_their_subtrees_and_levels_stop_at_the_limit()
    {
        var (graph, dominators, cache, _) = Heap();

        var segments = IcicleLayout.Build(graph, dominators, cache.Id, levels: 1, minShare: 0.01, Ct);

        segments.Select(s => (s.Level, s.Offset, s.Width)).ShouldBe([(0, 0UL, 441UL), (1, 0UL, 300UL), (1, 300UL, 100UL)], "the 1-byte entry is under 1 percent; the payload is two levels down");
    }
}
