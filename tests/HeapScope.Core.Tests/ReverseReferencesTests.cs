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

namespace HeapScope.Core.Tests;

/// <summary>Sprint 2.1: reverse references with known in-degrees.</summary>
public sealed class ReverseReferencesTests
{
    [Fact]
    public void A_chain_has_one_referrer_per_node_after_the_head()
    {
        using var graph = SyntheticGraphs.Chain(5);
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        reverse.GetReferrers(0).ToArray().ShouldBeEmpty();
        for (var i = 1; i < 5; i++)
        {
            reverse.GetReferrers(i).ToArray().ShouldBe([i - 1]);
        }

        reverse.EdgeCount.ShouldBe(4);
        reverse.ColumnBytes.ShouldBe((6 + 4) * 4L);
    }

    [Fact]
    public void A_diamond_bottom_has_both_sides_as_referrers()
    {
        using var graph = SyntheticGraphs.Diamond();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        reverse.GetReferrers(3).ToArray().ShouldBe([1, 2]);
        reverse.GetReferrers(1).ToArray().ShouldBe([0]);
        reverse.GetReferrers(2).ToArray().ShouldBe([0]);
        reverse.GetReferrers(0).ToArray().ShouldBeEmpty();
    }

    [Fact]
    public void A_cycle_closes_on_its_first_node()
    {
        using var graph = SyntheticGraphs.Cycle(3);
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        reverse.GetReferrers(0).ToArray().ShouldBe([2]);
        reverse.GetReferrers(1).ToArray().ShouldBe([0]);
        reverse.GetReferrers(2).ToArray().ShouldBe([1]);
    }

    [Fact]
    public void Self_references_and_repeated_references_count_once_per_reference()
    {
        var builder = new SyntheticGraphBuilder();
        var a = builder.Object("T.A");
        var b = builder.Object("T.B");
        var c = builder.Object("T.C");
        builder.Reference(a, a).Reference(a, b).Reference(a, b).Reference(c, b).Reference(b, a);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        reverse.GetReferrers(0).ToArray().ShouldBe([0, 1], "the self reference counts");
        reverse.GetReferrers(1).ToArray().ShouldBe([0, 0, 2], "two fields of A hold B");
        reverse.GetReferenceCount(1).ShouldBe(3);
        reverse.GetReferrers(2).ToArray().ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_graph_has_an_empty_index()
    {
        using var graph = new HeapGraphBuilder().Build(cancellationToken: TestContext.Current.CancellationToken);
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        reverse.ObjectCount.ShouldBe(0);
        reverse.EdgeCount.ShouldBe(0);
        reverse.InEdgeStarts.Span.ToArray().ShouldBe([0]);
    }

    [Fact]
    public void A_random_graph_matches_a_naive_count()
    {
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(5_000, 3, TypeCount: 20, RootCount: 10, Seed: 11), cancellationToken: TestContext.Current.CancellationToken);
        using var reverse = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        var naive = new List<int>[graph.ObjectCount];
        for (var i = 0; i < naive.Length; i++)
        {
            naive[i] = [];
        }

        for (var source = 0; source < graph.ObjectCount; source++)
        {
            foreach (var target in graph.GetReferences(source))
            {
                naive[target].Add(source);
            }
        }

        for (var i = 0; i < graph.ObjectCount; i++)
        {
            reverse.GetReferrers(i).ToArray().ShouldBe(naive[i].ToArray());
        }

        reverse.EdgeCount.ShouldBe(graph.EdgeCount);
    }

    [Fact]
    public void Columns_from_a_file_must_match_the_graph()
    {
        using var graph = SyntheticGraphs.Chain(3);
        using var built = ReverseReferences.Build(graph, cancellationToken: TestContext.Current.CancellationToken);

        using var copy = ReverseReferences.FromColumns(graph, built.InEdgeStarts, built.InEdges, owner: null);
        copy.GetReferrers(2).ToArray().ShouldBe([1]);

        Should.Throw<ArgumentException>(() => ReverseReferences.FromColumns(graph, Column<int>.FromArray([0, 0, 1]), built.InEdges, null));
        Should.Throw<ArgumentException>(() => ReverseReferences.FromColumns(graph, Column<int>.FromArray([1, 1, 1, 2]), built.InEdges, null));
    }

    [Fact]
    public void An_attached_index_is_disposed_with_the_graph_and_not_after()
    {
        var graph = SyntheticGraphs.Chain(2);
        var owned = new Probe();
        graph.Attach(owned).ShouldBeTrue();

        graph.Dispose();
        graph.Dispose();

        owned.Disposals.ShouldBe(1);
        var late = new Probe();
        graph.Attach(late).ShouldBeFalse("a disposed graph takes nothing");
        late.Disposals.ShouldBe(0, "the caller still owns it");
    }

    private sealed class Probe : IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose() => Disposals++;
    }
}
