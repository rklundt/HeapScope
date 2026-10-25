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

/// <summary>Sprint 2.2: retention paths with known answers.</summary>
public sealed class RetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_chain_is_explained_by_its_static_head()
    {
        using var graph = SyntheticGraphs.Chain(5);
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var tree = RetentionSearch.Find(graph, reverse, 4, cancellationToken: Ct);

        tree.IsReachable.ShouldBeTrue();
        var (node, root) = tree.Roots.ShouldHaveSingleItem();
        root.Root.Kind.ShouldBe(RootKind.StaticField);
        tree.PathOf(node).Select(n => tree.Nodes[n].ObjectIndex).ShouldBe([0, 1, 2, 3, 4]);
        PathRoots.Describe(root, PathRoots.Detail(graph, root)).ShouldBe("Static field Synthetic.Chain.s_head");
        tree.StoppedEarly.ShouldBeFalse();
    }

    [Fact]
    public void A_diamond_bottom_has_one_root_through_its_first_side_and_a_cycle_is_walked_once()
    {
        using var diamond = SyntheticGraphs.Diamond();
        using var diamondReverse = ReverseReferences.Build(diamond, cancellationToken: Ct);
        using var cycle = SyntheticGraphs.Cycle(4);
        using var cycleReverse = ReverseReferences.Build(cycle, cancellationToken: Ct);

        var d = RetentionSearch.Find(diamond, diamondReverse, 3, cancellationToken: Ct);
        var c = RetentionSearch.Find(cycle, cycleReverse, 2, cancellationToken: Ct);

        d.PathOf(d.Roots.ShouldHaveSingleItem().Node).Select(n => d.Nodes[n].ObjectIndex).ShouldBe([0, 1, 3]);
        c.PathOf(c.Roots.ShouldHaveSingleItem().Node).Select(n => c.Nodes[n].ObjectIndex).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void An_object_held_only_by_garbage_or_a_self_loop_is_unreachable()
    {
        var builder = new SyntheticGraphBuilder();
        var garbage = builder.Object("App.Garbage");
        var held = builder.Object("App.Held");
        var loop = builder.Object("App.Loop");
        builder.Reference(garbage, held).Reference(loop, loop);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var paths = RootPathTree.Build(graph, cancellationToken: Ct);

        RetentionSearch.Find(graph, reverse, held.Id, cancellationToken: Ct).IsReachable.ShouldBeFalse();
        RetentionSearch.Find(graph, reverse, loop.Id, cancellationToken: Ct).IsReachable.ShouldBeFalse();
        paths.IsReachable(held.Id).ShouldBeFalse();
        paths.ReachableCount.ShouldBe(0);
    }

    [Fact]
    public void A_frozen_object_is_a_root_that_is_never_collected()
    {
        var builder = new SyntheticGraphBuilder();
        var literal = builder.Object("System.String", heapKind: HeapKind.Frozen);
        var held = builder.Object("App.Held");
        builder.Reference(literal, held);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var tree = RetentionSearch.Find(graph, reverse, held.Id, cancellationToken: Ct);

        var root = tree.Roots.ShouldHaveSingleItem().Root;
        root.IsFrozen.ShouldBeTrue();
        PathRoots.Describe(root, null).ShouldStartWith("Never collected");
        RootPathTree.Build(graph, cancellationToken: Ct).Parent(held.Id).ShouldBe(literal.Id);
    }

    [Fact]
    public void A_static_field_explains_an_object_better_than_a_handle_at_the_same_distance()
    {
        var builder = new SyntheticGraphBuilder();
        var storage = builder.Object("System.Object[]");
        var holder = builder.Object("App.Holder");
        var value = builder.Object("App.Value");
        builder.Reference(storage, value).Reference(holder, value);
        builder.Root(storage, RootKind.StrongHandle, "static storage").Root(holder, RootKind.StaticField, "App.Program.s_holder");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var tree = RetentionSearch.Find(graph, reverse, value.Id, cancellationToken: Ct);

        tree.Roots.Select(r => r.Root.Root.Kind).ShouldBe([RootKind.StaticField, RootKind.StrongHandle]);
        RootPathTree.Build(graph, cancellationToken: Ct).Parent(value.Id).ShouldBe(holder.Id, "statics are queued first");
        PathRoots.Describe(tree.Roots[1].Root, "static storage").ShouldBe("The runtime's storage of static fields");
    }

    [Fact]
    public void A_rooted_object_has_a_path_of_its_own_and_other_holders_still_show()
    {
        var builder = new SyntheticGraphBuilder();
        var stack = builder.Object("App.Local");
        var cache = builder.Object("App.Cache");
        builder.Reference(cache, stack);
        builder.Root(stack, RootKind.ThreadStack, "thread 0x1a2c").Root(cache, RootKind.StaticField, "App.s_cache").Root(stack, RootKind.WeakHandle, flags: RootFlags.None);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var tree = RetentionSearch.Find(graph, reverse, stack.Id, cancellationToken: Ct);

        tree.Roots.Select(r => (tree.Nodes[r.Node].Depth, r.Root.Root.Kind)).ShouldBe([(0, RootKind.ThreadStack), (1, RootKind.StaticField)]);
        PathRoots.Describe(tree.Roots[0].Root, "thread 0x1a2c").ShouldBe("Local variable or argument on thread 0x1a2c");
    }

    [Fact]
    public void A_dependent_handle_value_is_held_through_its_key()
    {
        var builder = new SyntheticGraphBuilder();
        var key = builder.Object("App.Key");
        var value = builder.Object("App.Value");
        builder.DependentHandle(key, value).Root(key, RootKind.StaticField, "App.s_key");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var tree = RetentionSearch.Find(graph, reverse, value.Id, cancellationToken: Ct);

        var path = tree.PathOf(tree.Roots.ShouldHaveSingleItem().Node).Select(n => tree.Nodes[n].ObjectIndex).ToArray();
        path.ShouldBe([key.Id, value.Id]);
        graph.IsDependentHandleEdge(path[0], path[1]).ShouldBeTrue();
    }

    [Fact]
    public void The_search_keeps_the_closest_roots_and_says_when_it_stopped_early()
    {
        var builder = new SyntheticGraphBuilder();
        var target = builder.Object("App.Target");
        for (var i = 0; i < 15; i++)
        {
            var holder = builder.Object("App.Holder");
            builder.Reference(holder, target).Root(holder, RootKind.StrongHandle);
        }

        var far = builder.Object("App.Far");
        var link = builder.Object("App.Link");
        builder.Reference(far, link).Reference(link, target).Root(far, RootKind.StaticField, "App.s_far");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var limited = RetentionSearch.Find(graph, reverse, target.Id, maxRoots: 10, cancellationToken: Ct);
        var budget = RetentionSearch.Find(graph, reverse, target.Id, maxRoots: 100, maxVisited: 5, cancellationToken: Ct);

        limited.Roots.Count.ShouldBe(10);
        limited.Roots.ShouldAllBe(r => r.Root.Root.Kind == RootKind.StrongHandle, "the static is farther away");
        limited.StoppedEarly.ShouldBeTrue();
        budget.Visited.ShouldBe(5);
        budget.StoppedEarly.ShouldBeTrue();
    }

    [Fact]
    public void Key_paths_group_a_types_objects_by_path_fold_repeats_and_count_the_unreachable()
    {
        var builder = new SyntheticGraphBuilder();
        var list = builder.Object("App.List");
        var array = builder.Object("App.Item[]");
        builder.Reference(list, array).Root(list, RootKind.StaticField, "App.s_items");
        var items = Enumerable.Range(0, 3).Select(_ => builder.Object("App.Item", 40)).ToArray();
        foreach (var item in items)
        {
            builder.Reference(array, item);
        }

        var rootedItem = builder.Object("App.Item", 40);
        builder.Root(rootedItem, RootKind.ThreadStack, "thread 0x1");
        builder.Object("App.Item", 40);
        builder.Object("App.Item", 40);

        // A linked list of nodes from a static head: every node after the head shares one folded path.
        var head = builder.Object("App.Node");
        builder.Root(head, RootKind.StaticField, "App.s_head");
        var previous = head;
        for (var i = 0; i < 4; i++)
        {
            var node = builder.Object("App.Node");
            builder.Reference(previous, node);
            previous = node;
        }

        using var graph = builder.Build();
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);

        var itemPaths = KeyPaths.ForTypes(graph, tree, TypeGroups.MatchTypes(graph, "App.Item", null, out _), cancellationToken: Ct);
        var nodePaths = KeyPaths.ForTypes(graph, tree, TypeGroups.MatchTypes(graph, "App.Node", null, out _), cancellationToken: Ct);

        itemPaths.Objects.ShouldBe(6);
        (itemPaths.Unreachable, itemPaths.UnreachableBytes).ShouldBe((2L, 80UL));
        var first = itemPaths.Paths[0];
        (first.Objects, first.Bytes, first.Root.Root.Kind).ShouldBe((3L, 120UL, RootKind.StaticField));
        first.Steps.Select(t => graph.Types.GetName(t)).ShouldBe(["App.List", "App.Item[]"]);
        var rooted = itemPaths.Paths[1];
        (rooted.Objects, rooted.Root.Root.Kind, rooted.Steps.Count).ShouldBe((1L, RootKind.ThreadStack, 0));

        nodePaths.Paths.Select(p => (p.Objects, p.Steps.Count)).ShouldBe([(4L, 1), (1L, 0)]);
        graph.Types.GetName(nodePaths.Paths[0].Steps[0]).ShouldBe("App.Node");
    }

    [Fact]
    public void A_search_that_stops_before_any_root_falls_back_to_the_tree_and_skips_garbage()
    {
        // Review H2: a live object behind a long chain must not look unreachable.
        var builder = new SyntheticGraphBuilder();
        var target = builder.Object("App.Target");
        var previous = target;
        for (var i = 0; i < 50; i++)
        {
            var link = builder.Object("App.Link");
            builder.Reference(link, previous);
            previous = link;
        }

        builder.Root(previous, RootKind.StaticField, "App.s_head");
        for (var i = 0; i < 1_000; i++)
        {
            builder.Reference(builder.Object("App.Garbage"), target);
        }

        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);

        var limited = RetentionSearch.Find(graph, reverse, target.Id, maxVisited: 20, cancellationToken: Ct);
        var pruned = RetentionSearch.Find(graph, reverse, target.Id, maxVisited: 60, cancellationToken: Ct, paths: tree);
        var fallback = RetentionSearch.FromRootPath(graph, tree, target.Id);

        (limited.IsReachable, limited.StoppedEarly).ShouldBe((false, true), "the bounded search alone cannot tell");
        pruned.IsReachable.ShouldBeTrue("garbage referrers are not visited, so the limit reaches the root");
        pruned.Visited.ShouldBe(51, "the target and its 50 links");
        fallback.IsReachable.ShouldBeTrue();
        fallback.StoppedEarly.ShouldBeTrue();
        fallback.PathOf(fallback.Roots[0].Node).Count.ShouldBe(51);
        RetentionSearch.FromRootPath(graph, tree, graph.ObjectCount - 1).IsReachable.ShouldBeFalse();
    }

    [Fact]
    public void A_static_beats_a_handle_at_equal_distance_deeper_in_the_graph()
    {
        // Review M9: the tie rule at depth 3, not only at depth 1.
        var builder = new SyntheticGraphBuilder();
        var handleHolder = builder.Object("App.ByHandle");
        var staticHolder = builder.Object("App.ByStatic");
        var a1 = builder.Object("App.A");
        var b1 = builder.Object("App.B");
        var a2 = builder.Object("App.A");
        var b2 = builder.Object("App.B");
        var value = builder.Object("App.Value");
        builder.Reference(handleHolder, a1).Reference(a1, a2).Reference(a2, value);
        builder.Reference(staticHolder, b1).Reference(b1, b2).Reference(b2, value);
        builder.Root(handleHolder, RootKind.StrongHandle).Root(staticHolder, RootKind.StaticField, "App.s_value");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var search = RetentionSearch.Find(graph, reverse, value.Id, maxRoots: 1, cancellationToken: Ct);
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);

        search.Roots.ShouldHaveSingleItem().Root.Root.Kind.ShouldBe(RootKind.StaticField);
        tree.Parent(value.Id).ShouldBe(b2.Id);
    }

    [Fact]
    public void Every_retention_computation_stops_on_cancellation()
    {
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(200_000, 3, TypeCount: 10, RootCount: 5, Seed: 7), cancellationToken: Ct);
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Should.Throw<OperationCanceledException>(() => RootPathTree.Build(graph, cancellationToken: canceled.Token));
        Should.Throw<OperationCanceledException>(() => KeyPaths.ForTypes(graph, tree, [.. Enumerable.Repeat(true, graph.Types.Count)], cancellationToken: canceled.Token));
        Should.Throw<OperationCanceledException>(() => RetentionSearch.Find(graph, reverse, 0, maxRoots: 50, cancellationToken: canceled.Token));
    }

    [Fact]
    public void Key_paths_beyond_the_limit_are_summed()
    {
        var builder = new SyntheticGraphBuilder();
        for (var i = 0; i < 5; i++)
        {
            var holder = builder.Object($"App.Holder{i}");
            builder.Root(holder, RootKind.StaticField, $"App.s_{i}");
            for (var j = 0; j <= i; j++)
            {
                builder.Reference(holder, builder.Object("App.Item", 10));
            }
        }

        using var graph = builder.Build();
        var paths = KeyPaths.ForTypes(graph, RootPathTree.Build(graph, cancellationToken: Ct), TypeGroups.MatchTypes(graph, "App.Item", null, out _), maxPaths: 2, cancellationToken: Ct);

        paths.Paths.Select(p => p.Objects).ShouldBe([5L, 4L]);
        (paths.OtherPaths, paths.OtherObjects, paths.OtherBytes).ShouldBe((3, 6L, 60UL));
    }
}
