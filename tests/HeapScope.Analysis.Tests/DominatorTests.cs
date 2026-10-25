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

/// <summary>Sprint 2.3: dominators and retained sizes with known answers, and against a brute-force definition.</summary>
public sealed class DominatorTests
{
    private const int Root = DominatorTree.VirtualRoot;
    private const int None = DominatorTree.Unreachable;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_chain_is_dominated_link_by_link_and_its_head_retains_it_all()
    {
        using var graph = SyntheticGraphs.Chain(5);

        using var dominators = Compute(graph);

        Idoms(dominators).ShouldBe([Root, 0, 1, 2, 3]);
        Retained(dominators).ShouldBe([120UL, 96, 72, 48, 24]);
    }

    [Fact]
    public void A_diamond_bottom_is_dominated_by_the_top_and_neither_side_retains_it()
    {
        using var graph = SyntheticGraphs.Diamond();

        using var dominators = Compute(graph);

        Idoms(dominators).ShouldBe([Root, 0, 0, 0]);
        Retained(dominators).ShouldBe([1072UL, 24, 24, 1000]);
    }

    [Fact]
    public void A_cycle_is_dominated_along_its_order_from_the_rooted_object()
    {
        using var graph = SyntheticGraphs.Cycle(4);

        using var dominators = Compute(graph);

        Idoms(dominators).ShouldBe([Root, 0, 1, 2]);
        dominators.RetainedSize(0).ShouldBe(96UL);
    }

    [Fact]
    public void A_self_loop_neither_dominates_itself_nor_counts_twice()
    {
        var builder = new SyntheticGraphBuilder();
        var loop = builder.Object("App.Loop");
        var child = builder.Object("App.Child");
        builder.Reference(loop, loop).Reference(loop, child).Reference(child, child).Root(loop);
        using var graph = builder.Build();

        using var dominators = Compute(graph);

        Idoms(dominators).ShouldBe([Root, 0]);
        Retained(dominators).ShouldBe([48UL, 24]);
    }

    [Fact]
    public void A_root_reached_through_another_root_stays_alive_by_itself()
    {
        var builder = new SyntheticGraphBuilder();
        var first = builder.Object("App.First");
        var second = builder.Object("App.Second");
        var behind = builder.Object("App.Behind");
        builder.Reference(first, second).Reference(second, behind).Root(first).Root(second, RootKind.ThreadStack, "thread 0x1");
        using var graph = builder.Build();

        using var dominators = Compute(graph);

        Idoms(dominators).ShouldBe([Root, Root, 1]);
        Retained(dominators).ShouldBe([24UL, 48, 24]);
    }

    [Fact]
    public void An_object_two_roots_share_belongs_to_neither()
    {
        var builder = new SyntheticGraphBuilder();
        var a = builder.Object("App.A");
        var b = builder.Object("App.B");
        var shared = builder.Object("App.Shared", size: 500);
        builder.Reference(a, shared).Reference(b, shared).Root(a).Root(b, RootKind.StrongHandle);
        using var graph = builder.Build();

        using var dominators = Compute(graph);

        Idoms(dominators).ShouldBe([Root, Root, Root]);
        Retained(dominators).ShouldBe([24UL, 24, 500]);
    }

    [Fact]
    public void A_list_held_from_both_ends_is_retained_by_neither_end()
    {
        // Cooper-Harvey-Kennedy needs a pass per node on this shape (1.9 review H1); semi-NCA does not care.
        const int length = 200_000;
        var builder = new SyntheticGraphBuilder();
        var head = builder.Object("App.Head");
        var tail = builder.Object("App.Tail");
        var nodes = Enumerable.Range(0, length).Select(_ => builder.Object("App.Node")).ToArray();
        for (var i = 0; i + 1 < length; i++)
        {
            builder.Reference(nodes[i], nodes[i + 1]).Reference(nodes[i + 1], nodes[i]);
        }

        builder.Reference(head, nodes[0]).Reference(tail, nodes[^1]).Root(head).Root(tail);
        using var graph = builder.Build();

        using var dominators = Compute(graph);

        dominators.RetainedSize(0).ShouldBe(24UL);
        dominators.RetainedSize(1).ShouldBe(24UL);
        Enumerable.Range(2, length).All(i => dominators.ImmediateDominator(i) == Root).ShouldBeTrue();
    }

    [Fact]
    public void A_long_chain_needs_no_stack()
    {
        using var graph = SyntheticGraphs.Chain(1_000_000);

        using var dominators = Compute(graph);

        dominators.ImmediateDominator(999_999).ShouldBe(999_998);
        dominators.RetainedSize(0).ShouldBe(24UL * 1_000_000);
    }

    [Fact]
    public void Frozen_objects_are_alive_and_garbage_or_weakly_held_objects_are_not()
    {
        var builder = new SyntheticGraphBuilder();
        var literal = builder.Object("System.String", heapKind: HeapKind.Frozen);
        var garbage = builder.Object("App.Garbage");
        var weak = builder.Object("App.Weak");
        var held = builder.Object("App.HeldByGarbage");
        builder.Reference(garbage, literal).Reference(garbage, held).Root(weak, RootKind.WeakHandle, flags: RootFlags.None);
        using var graph = builder.Build();

        using var dominators = Compute(graph);

        Idoms(dominators).ShouldBe([Root, None, None, None]);
        Retained(dominators).ShouldBe([24UL, 0, 0, 0]);
        dominators.IsReachable(1).ShouldBeFalse();
    }

    [Fact]
    public void A_dependent_handle_edge_makes_the_primary_the_dominator()
    {
        var builder = new SyntheticGraphBuilder();
        var key = builder.Object("App.Key");
        var value = builder.Object("App.Value", size: 100);
        builder.DependentHandle(key, value).Root(key);
        using var graph = builder.Build();

        using var dominators = Compute(graph);

        Idoms(dominators).ShouldBe([Root, 0]);
        dominators.RetainedSize(0).ShouldBe(124UL);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Random_graphs_match_the_definition(int seed)
    {
        using var graph = RandomGraph(seed, objects: 300);

        using var dominators = Compute(graph);

        var (idoms, retained) = BruteForce(graph);
        Idoms(dominators).ShouldBe(idoms);
        Retained(dominators).ShouldBe(retained);
    }

    [Fact]
    public void A_circular_doubly_linked_list_is_retained_by_its_head_alone()
    {
        // Review M5: LinkedList<T> is circular; evaluating the head walks a compression path through the whole list.
        const int length = 100_000;
        var builder = new SyntheticGraphBuilder();
        var owner = builder.Object("App.Lru");
        var nodes = Enumerable.Range(0, length).Select(_ => builder.Object("App.Node")).ToArray();
        for (var i = 0; i < length; i++)
        {
            builder.Reference(nodes[i], nodes[(i + 1) % length]).Reference(nodes[i], nodes[(i + length - 1) % length]);
        }

        builder.Reference(owner, nodes[0]).Root(owner);
        using var graph = builder.Build();

        using var dominators = Compute(graph);

        dominators.RetainedSize(0).ShouldBe(24UL * (length + 1));
        dominators.ImmediateDominator(1).ShouldBe(0);
        Enumerable.Range(2, length - 1).All(i => dominators.ImmediateDominator(i) == 1).ShouldBeTrue("every node is reached only through the head");
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Random_graphs_with_long_chains_match_the_definition(int seed)
    {
        // Long chains with random cross and back references make deep depth-first trees and long compression paths.
        var random = new Random(seed);
        var builder = new SyntheticGraphBuilder();
        var all = Enumerable.Range(0, 1_500).Select(i => builder.Object($"App.T{i % 5}", size: (ulong)random.Next(8, 100))).ToArray();
        for (var i = 0; i + 1 < all.Length; i++)
        {
            if (random.Next(50) != 0)
            {
                builder.Reference(all[i], all[i + 1]);
            }

            if (random.Next(4) == 0)
            {
                builder.Reference(all[i], all[random.Next(all.Length)]);
            }
        }

        builder.Root(all[0]).Root(all[random.Next(all.Length)], RootKind.ThreadStack, "thread 0x2");
        using var graph = builder.Build();

        using var dominators = Compute(graph);

        var (idoms, retained) = BruteForce(graph);
        Idoms(dominators).ShouldBe(idoms);
        Retained(dominators).ShouldBe(retained);
    }

    [Fact]
    public void An_empty_graph_and_a_graph_without_roots_have_nothing_reachable()
    {
        using var empty = new SyntheticGraphBuilder().Build();
        var builder = new SyntheticGraphBuilder();
        var a = builder.Object("App.A");
        builder.Reference(a, builder.Object("App.B"));
        using var unrooted = builder.Build();

        using var none = Compute(empty);
        using var garbage = Compute(unrooted);

        none.ObjectCount.ShouldBe(0);
        Idoms(garbage).ShouldBe([None, None]);
        Retained(garbage).ShouldBe([0UL, 0]);
    }

    [Fact]
    public void Results_in_given_spans_equal_the_managed_ones_and_count_the_reachable()
    {
        using var graph = RandomGraph(9, objects: 500);
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        using var managed = Dominators.Compute(graph, reverse, cancellationToken: Ct);
        var idoms = Enumerable.Repeat(77, graph.ObjectCount).ToArray();
        var retained = Enumerable.Repeat(77UL, graph.ObjectCount).ToArray();
        var progress = new List<DominatorProgress>();

        var reachable = DominatorTree.Compute(graph, reverse, idoms, retained, new InlineProgress<DominatorProgress>(progress.Add), Ct);

        idoms.ShouldBe(Idoms(managed));
        retained.ShouldBe(Retained(managed));
        reachable.ShouldBe(idoms.Count(d => d != None));
        progress.Select(p => p.Phase).Distinct().ShouldBe([DominatorPhase.Numbering, DominatorPhase.Dominators, DominatorPhase.RetainedSizes]);
        Should.Throw<ArgumentException>(() => DominatorTree.Compute(graph, reverse, new int[1], retained));
    }

    [Fact]
    public void A_canceled_computation_stops()
    {
        using var graph = SyntheticGraphs.Chain(3 << 20);
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Should.Throw<OperationCanceledException>(() => Dominators.Compute(graph, reverse, cancellationToken: canceled.Token));
    }

    [Fact]
    public void The_semantics_version_follows_the_path_roots_rules() =>
        (DominatorTree.SemanticsVersion >> 16, DominatorTree.SemanticsVersion & 0xFFFF).ShouldBe((PathRoots.SemanticsVersion, DominatorTree.AlgorithmVersion));

    [Fact]
    public void Columns_from_a_file_must_match_the_graph()
    {
        using var graph = SyntheticGraphs.Chain(3);

        Should.Throw<ArgumentException>(() => Dominators.FromColumns(graph, Column<int>.FromArray([Root, 0]), Column<ulong>.FromArray([1, 2, 3]), null));
    }

    [Fact]
    public void A_group_counts_only_its_objects_no_other_of_its_objects_dominates()
    {
        // Two lists of App.Node, held by App.Owner objects; App.Other sits between nodes of the second list.
        var builder = new SyntheticGraphBuilder();
        var first = builder.Object("App.Owner");
        var second = builder.Object("App.Owner");
        var a1 = builder.Object("App.Node", size: 100);
        var a2 = builder.Object("App.Node", size: 100);
        var b1 = builder.Object("App.Node", size: 100);
        var other = builder.Object("Lib.Other", size: 10);
        var b2 = builder.Object("App.Node", size: 100);
        var garbage = builder.Object("App.Node", size: 100);
        builder.Reference(first, a1).Reference(a1, a2).Reference(second, b1).Reference(b1, other).Reference(other, b2).Reference(garbage, a1);
        builder.Root(first).Root(second, RootKind.StrongHandle);
        using var graph = builder.Build();
        using var dominators = Compute(graph);
        var node = TypeOf(graph, "App.Node");
        var owner = TypeOf(graph, "App.Owner");
        var lib = TypeOf(graph, "Lib.Other");

        // By type: every type its own group.
        var byType = GroupRetention.Compute(graph, dominators, [.. Enumerable.Range(0, graph.Types.Count)], graph.Types.Count, cancellationToken: Ct);
        byType[node].ShouldBe(200UL + 210, "each list's first node, not the nodes it dominates, and never the garbage");
        byType[owner].ShouldBe(224UL + 234);
        byType[lib].ShouldBe(110UL);

        // By namespace: App holds both owners, whose lists are inside them; Lib's object is inside an App list.
        var groups = new int[graph.Types.Count];
        groups[node] = groups[owner] = 0;
        groups[lib] = 1;
        GroupRetention.Compute(graph, dominators, groups, 2, cancellationToken: Ct).ShouldBe([458UL, 110]);
    }

    private static Dominators Compute(HeapGraph graph)
    {
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        return Dominators.Compute(graph, reverse, cancellationToken: Ct);
    }

    private static int[] Idoms(Dominators dominators) => dominators.ImmediateDominators.Span.ToArray();

    private static ulong[] Retained(Dominators dominators) => dominators.RetainedSizes.Span.ToArray();

    private static int TypeOf(HeapGraph graph, string name) =>
        Enumerable.Range(0, graph.Types.Count).Single(t => graph.Types.GetName(t) == name);

    /// <summary>
    /// A random graph with retaining and weak roots of several kinds, frozen objects, dependent handles and garbage.
    /// </summary>
    private static HeapGraph RandomGraph(int seed, int objects)
    {
        var random = new Random(seed);
        var builder = new SyntheticGraphBuilder();
        var all = Enumerable.Range(0, objects)
            .Select(i => builder.Object($"App.T{i % 7}", size: (ulong)random.Next(8, 200), heapKind: random.Next(25) == 0 ? HeapKind.Frozen : HeapKind.Gen2))
            .ToArray();
        foreach (var from in all)
        {
            for (var r = random.Next(4); r > 0; r--)
            {
                builder.Reference(from, all[random.Next(objects)]);
            }
        }

        RootKind[] kinds = [RootKind.StaticField, RootKind.ThreadStack, RootKind.StrongHandle, RootKind.FinalizerQueue];
        for (var r = random.Next(2, 8); r > 0; r--)
        {
            builder.Root(all[random.Next(objects)], kinds[random.Next(kinds.Length)], "root");
        }

        builder.Root(all[random.Next(objects)], RootKind.WeakHandle, flags: RootFlags.None);
        for (var d = random.Next(3); d > 0; d--)
        {
            builder.DependentHandle(all[random.Next(objects)], all[random.Next(objects)]);
        }

        return builder.Build();
    }

    /// <summary>
    /// Dominators by their definition: d dominates x when x is reachable and no root reaches it once d is removed. The
    /// immediate dominator is the strict dominator that dominates the fewest objects; retained size sums what d dominates.
    /// </summary>
    private static (int[] Idoms, ulong[] Retained) BruteForce(HeapGraph graph)
    {
        var n = graph.ObjectCount;
        var sources = PathRoots.Sources(graph).ToArray();
        var reachable = Reach(graph, sources, removed: -1);
        var dominated = new List<int>[n];
        for (var d = 0; d < n; d++)
        {
            dominated[d] = [];
            if (!reachable[d])
            {
                continue;
            }

            var without = Reach(graph, sources, removed: d);
            for (var x = 0; x < n; x++)
            {
                if (reachable[x] && (x == d || !without[x]))
                {
                    dominated[d].Add(x);
                }
            }
        }

        var idoms = new int[n];
        var retained = new ulong[n];
        for (var x = 0; x < n; x++)
        {
            if (!reachable[x])
            {
                idoms[x] = None;
                continue;
            }

            var strict = Enumerable.Range(0, n).Where(d => d != x && dominated[d].Contains(x)).ToList();
            idoms[x] = strict.Count == 0 ? Root : strict.MinBy(d => dominated[d].Count);
            retained[x] = (ulong)dominated[x].Sum(o => (long)graph.GetSize(o));
        }

        return (idoms, retained);
    }

    private static bool[] Reach(HeapGraph graph, int[] sources, int removed)
    {
        var seen = new bool[graph.ObjectCount];
        var queue = new Queue<int>(sources.Where(s => s != removed));
        foreach (var s in queue)
        {
            seen[s] = true;
        }

        while (queue.Count > 0)
        {
            foreach (var target in graph.GetReferences(queue.Dequeue()))
            {
                if (target != removed && !seen[target])
                {
                    seen[target] = true;
                    queue.Enqueue(target);
                }
            }
        }

        return seen;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
