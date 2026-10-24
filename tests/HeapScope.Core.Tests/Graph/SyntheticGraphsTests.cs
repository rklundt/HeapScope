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

using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Core.Tests.Graph;

public class SyntheticGraphsTests
{
    [Fact]
    public void Chain_links_each_object_to_the_next_and_roots_the_head()
    {
        using var graph = SyntheticGraphs.Chain(5);

        graph.ObjectCount.ShouldBe(5);
        graph.EdgeCount.ShouldBe(4);
        for (var i = 0; i < 4; i++)
        {
            graph.GetReferences(i).ToArray().ShouldBe([i + 1]);
        }

        graph.Roots.GetRoots(0)[0].Kind.ShouldBe(RootKind.StaticField);
    }

    [Fact]
    public void Cycle_closes_back_to_the_first_object()
    {
        using var graph = SyntheticGraphs.Cycle(3);

        graph.GetReferences(2).ToArray().ShouldBe([0]);
        graph.EdgeCount.ShouldBe(3);
    }

    [Fact]
    public void Tree_has_the_expected_node_and_edge_counts()
    {
        using var graph = SyntheticGraphs.Tree(depth: 3, fanOut: 2);

        graph.ObjectCount.ShouldBe(7);
        graph.EdgeCount.ShouldBe(6);
        graph.GetReferences(0).ToArray().ShouldBe([1, 2]);
    }

    [Fact]
    public void Diamond_gives_the_bottom_object_two_incoming_references()
    {
        using var graph = SyntheticGraphs.Diamond();

        var incoming = 0;
        for (var i = 0; i < graph.ObjectCount; i++)
        {
            incoming += graph.GetReferences(i).Count(3);
        }

        incoming.ShouldBe(2);
        graph.GetSize(3).ShouldBe(1000UL);
    }

    [Fact]
    public void Random_graph_is_deterministic_per_seed()
    {
        var options = new RandomGraphOptions(ObjectCount: 5_000, AverageReferences: 2.5, TypeCount: 10, RootCount: 10, Seed: 11);
        using var first = SyntheticGraphs.Random(options, cancellationToken: TestContext.Current.CancellationToken);
        using var second = SyntheticGraphs.Random(options, cancellationToken: TestContext.Current.CancellationToken);
        using var other = SyntheticGraphs.Random(options with { Seed = 12 }, cancellationToken: TestContext.Current.CancellationToken);

        first.Edges.Span.SequenceEqual(second.Edges.Span).ShouldBeTrue();
        first.Edges.Span.SequenceEqual(other.Edges.Span).ShouldBeFalse();
        first.Types.Count.ShouldBe(10);
        first.Roots.Count.ShouldBe(10);
        ((double)first.EdgeCount / first.ObjectCount).ShouldBeInRange(2.4, 2.6);
    }

    [Fact]
    public void Random_graph_in_reverse_order_is_sorted_and_keeps_its_shape()
    {
        var options = new RandomGraphOptions(ObjectCount: 2_000, AverageReferences: 3, TypeCount: 5, RootCount: 3, Seed: 5, AddInReverseOrder: true);
        using var graph = SyntheticGraphs.Random(options, cancellationToken: TestContext.Current.CancellationToken);

        var addresses = graph.Addresses.Span;
        for (var i = 1; i < addresses.Length; i++)
        {
            addresses[i].ShouldBeGreaterThan(addresses[i - 1]);
        }

        graph.ObjectCount.ShouldBe(2_000);
        ((double)graph.EdgeCount / graph.ObjectCount).ShouldBe(3);
    }

    [Fact]
    public void Random_graph_honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => SyntheticGraphs.Random(new RandomGraphOptions(10), cancellationToken: cts.Token));
    }

    [Fact]
    public void Random_graph_reports_progress()
    {
        var reports = new List<long>();
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(10), new SynchronousProgress(reports), TestContext.Current.CancellationToken);

        reports.ShouldNotBeEmpty();
    }

    private sealed class SynchronousProgress(List<long> reports) : IProgress<long>
    {
        public void Report(long value) => reports.Add(value);
    }
}
