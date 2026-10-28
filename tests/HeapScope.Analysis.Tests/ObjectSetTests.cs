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
using HeapScope.Analysis.Sets;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Analysis.Tests;

/// <summary>Object sets (sprint 3.1, ADR-030): members, aggregation, retention and the set's dominators.</summary>
public sealed class ObjectSetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_set_of_types_holds_their_objects_in_index_order_with_totals()
    {
        var builder = new SyntheticGraphBuilder();
        var a = builder.Object("App.A", size: 10);
        builder.Object("App.B", size: 20);
        var c = builder.Object("App.A", size: 30);
        var d = builder.Object("App.C", size: 40);
        var graph = builder.Build();
        var mask = new bool[graph.Types.Count];
        mask[TypeIndex(graph, "App.A")] = true;
        mask[TypeIndex(graph, "App.C")] = true;

        var set = ObjectSet.OfTypes(graph, mask, Ct);

        set.Members.ShouldBe([a.Id, c.Id, d.Id]);
        (set.Count, set.Bytes, set.TypeCount).ShouldBe((3, 80UL, 2));
        set.Contains(a.Id).ShouldBeTrue();
        set.Contains(a.Id + 1).ShouldBeFalse();
    }

    [Fact]
    public void A_set_of_addresses_leaves_out_addresses_with_no_object_and_repeats()
    {
        var builder = new SyntheticGraphBuilder();
        var a = builder.Object("App.A", size: 10);
        var b = builder.Object("App.B", size: 20);
        var graph = builder.Build();

        var set = ObjectSet.OfAddresses(graph, [graph.Addresses[b.Id], graph.Addresses[a.Id], graph.Addresses[b.Id], 1]);

        set.Members.ShouldBe([a.Id, b.Id]);
        set.Bytes.ShouldBe(30UL);
    }

    [Fact]
    public void Aggregating_a_set_counts_its_members_only_per_type_and_heap_kind()
    {
        var builder = new SyntheticGraphBuilder();
        var a = builder.Object("App.A", size: 10, heapKind: HeapKind.Gen0);
        builder.Object("App.A", size: 99, heapKind: HeapKind.Gen0);
        var b = builder.Object("App.B", size: 100, heapKind: HeapKind.LargeObjectHeap);
        var graph = builder.Build();
        var set = ObjectSet.OfIndices(graph, [a.Id, b.Id]);

        var aggregation = TypeAggregation.Compute(graph, set, Ct);

        (aggregation.TotalObjects, aggregation.TotalBytes).ShouldBe((2L, 110UL));
        aggregation.Counts[TypeIndex(graph, "App.A")].ShouldBe(1);
        aggregation.Bytes[TypeIndex(graph, "App.A")].ShouldBe(10UL);
        aggregation.KindBytes[(int)HeapKind.LargeObjectHeap].ShouldBe(100UL);
        aggregation.KindObjects[(int)HeapKind.Gen0].ShouldBe(1);
    }

    [Fact]
    public void A_set_of_objects_retains_what_they_keep_alive_together_and_returns_it_as_a_set()
    {
        // Two holders share a payload; each has its own data; a third holder of the same type is left out of the set.
        var builder = new SyntheticGraphBuilder();
        var root = builder.Object("App.Root", size: 1);
        var first = builder.Object("App.Holder", size: 10);
        var second = builder.Object("App.Holder", size: 10);
        var third = builder.Object("App.Holder", size: 10);
        var shared = builder.Object("App.Data", size: 100);
        var own = builder.Object("App.Data", size: 50);
        builder.Reference(root, first).Reference(root, second).Reference(root, third)
            .Reference(first, shared).Reference(second, shared).Reference(first, own);
        builder.Root(root);
        var (graph, dominators) = Build(builder);
        var set = ObjectSet.OfIndices(graph, [first.Id, second.Id]);

        var result = SetRetention.Compute(graph, dominators, set, collectRetained: true, cancellationToken: Ct);

        (result.SetObjects, result.RetainedObjects, result.RetainedBytes).ShouldBe((2L, 4L, 170UL));
        result.Retained.ShouldNotBeNull().Members.ShouldBe([first.Id, second.Id, shared.Id, own.Id]);
        result.Retained.Contains(third.Id).ShouldBeFalse();
    }

    [Fact]
    public void A_types_set_retains_the_same_as_the_type_mask()
    {
        var builder = new SyntheticGraphBuilder();
        var holder = builder.Object("App.Holder", size: 10);
        var item = builder.Object("App.Item", size: 20);
        builder.Reference(holder, item).Reference(item, builder.Object("App.Data", size: 30));
        builder.Root(holder);
        var (graph, dominators) = Build(builder);
        var mask = new bool[graph.Types.Count];
        mask[TypeIndex(graph, "App.Item")] = true;

        var byMask = SetRetention.Compute(graph, dominators, mask, cancellationToken: Ct);
        var bySet = SetRetention.Compute(graph, dominators, ObjectSet.OfTypes(graph, mask, Ct), cancellationToken: Ct);

        (bySet.RetainedObjects, bySet.RetainedBytes, bySet.SetObjects).ShouldBe((byMask.RetainedObjects, byMask.RetainedBytes, byMask.SetObjects));
        bySet.Retained.ShouldBeNull("not asked for");
    }

    [Fact]
    public void The_sets_dominators_are_the_members_no_other_member_dominates()
    {
        // root -> outer (member) -> inner (member) -> leaf; root -> lone (member); a member no root reaches.
        var builder = new SyntheticGraphBuilder();
        var root = builder.Object("App.Root", size: 1);
        var outer = builder.Object("App.Node", size: 10);
        var inner = builder.Object("App.Node", size: 10);
        var leaf = builder.Object("App.Leaf", size: 100);
        var lone = builder.Object("App.Node", size: 10);
        var garbage = builder.Object("App.Node", size: 10);
        builder.Reference(root, outer).Reference(outer, inner).Reference(inner, leaf).Reference(root, lone);
        builder.Root(root);
        var (graph, dominators) = Build(builder);
        var mask = new bool[graph.Types.Count];
        mask[TypeIndex(graph, "App.Node")] = true;
        var set = ObjectSet.OfTypes(graph, mask, Ct);

        var tops = SetDominators.Top(dominators, set, out var unreachable, Ct);

        tops.Select(t => (t.ObjectIndex, t.MembersBelow)).OrderBy(t => t.ObjectIndex).ShouldBe([(outer.Id, 1), (lone.Id, 0)]);
        unreachable.ShouldBe(1);
        set.Contains(garbage.Id).ShouldBeTrue();
        tops.Sum(t => (long)dominators.RetainedSize(t.ObjectIndex)).ShouldBe(130, "outer retains inner and the leaf; lone itself");
    }

    [Fact]
    public void A_group_matches_the_types_of_a_namespace_or_an_assembly_as_the_rows_name_them()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Object("App.Orders.Order", size: 10, module: "App.dll");
        builder.Object("App.Orders.Line", size: 10, module: "App.dll");
        builder.Object("Lib.Cache", size: 10, module: @"C:\x\Lib.dll");
        var graph = builder.Build();

        TypeGroups.MatchGroup(graph, GroupKey.Namespace, "App.Orders", out var inNamespace);
        TypeGroups.MatchGroup(graph, GroupKey.Assembly, "lib.DLL", out var inAssembly);

        (inNamespace, inAssembly).ShouldBe((2, 1));
        Should.Throw<ArgumentException>(() => TypeGroups.MatchGroup(graph, GroupKey.Type, "x", out _));
    }

    private static (HeapGraph Graph, Dominators Dominators) Build(SyntheticGraphBuilder builder)
    {
        var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        return (graph, Dominators.Compute(graph, reverse, cancellationToken: Ct));
    }

    private static int TypeIndex(HeapGraph graph, string name) =>
        Enumerable.Range(0, graph.Types.Count).Single(t => graph.Types.GetName(t) == name);
}
