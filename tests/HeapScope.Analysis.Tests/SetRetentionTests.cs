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

/// <summary>Exact retention of a set (sprint 2.4): what a type's objects keep alive together.</summary>
public sealed class SetRetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_set_retains_what_its_members_share_which_no_single_member_does()
    {
        // A holder keeps two items; both items share one payload, the first also has its own; another root keeps an object
        // the second item points at too; a third item nothing reaches.
        var builder = new SyntheticGraphBuilder();
        var holder = builder.Object("App.Holder", size: 10, module: "App.dll");
        var first = builder.Object("App.Item", size: 20, module: "App.dll");
        var second = builder.Object("App.Item", size: 20, module: "App.dll");
        var shared = builder.Object("Lib.Data", size: 100, module: "Lib.dll");
        var own = builder.Object("Lib.Data", size: 50, module: "Lib.dll");
        var other = builder.Object("App.Other", size: 5, module: "App.dll");
        var kept = builder.Object("Lib.Data", size: 30, module: "Lib.dll");
        builder.Object("App.Item", size: 20, module: "App.dll");
        builder.Reference(holder, first).Reference(holder, second).Reference(first, shared).Reference(second, shared)
            .Reference(first, own).Reference(second, kept).Reference(other, kept);
        builder.Root(holder).Root(other, RootKind.StrongHandle);
        var (graph, dominators) = Build(builder);

        var result = SetRetention.Compute(graph, dominators, TypesNamed(graph, "App.Item"), cancellationToken: Ct);

        result.SetObjects.ShouldBe(2, "the item no root reaches is not counted");
        result.SetBytes.ShouldBe(40UL);
        result.RetainedObjects.ShouldBe(4);
        result.RetainedBytes.ShouldBe(190UL, "both items, the shared payload and the first item's own; not the object another root holds");
        var group = dominators.RetainedSize(first.Id) + dominators.RetainedSize(second.Id);
        group.ShouldBe(90UL, "summed per member, the shared payload belongs to neither");
        result.RetainedBytes.ShouldBeGreaterThan(group);

        var data = TypeIndex(graph, "Lib.Data");
        result.ObjectsByType[data].ShouldBe(2);
        result.BytesByType[data].ShouldBe(150UL);
        result.BytesByType[TypeIndex(graph, "App.Item")].ShouldBe(40UL);
        result.BytesByType[TypeIndex(graph, "App.Holder")].ShouldBe(0UL);
    }

    [Fact]
    public void A_set_that_holds_a_root_retains_everything_only_it_reaches()
    {
        var builder = new SyntheticGraphBuilder();
        var cache = builder.Object("App.Cache", size: 40, module: "App.dll");
        var entry = builder.Object("App.Entry", size: 100, module: "App.dll");
        var other = builder.Object("App.Other", size: 10, module: "App.dll");
        builder.Reference(cache, entry);
        builder.Root(cache).Root(other, RootKind.StrongHandle);
        var (graph, dominators) = Build(builder);

        var result = SetRetention.Compute(graph, dominators, TypesNamed(graph, "App.Cache"), cancellationToken: Ct);

        result.RetainedObjects.ShouldBe(2);
        result.RetainedBytes.ShouldBe(dominators.RetainedSize(cache.Id), "a set of one object retains what it dominates");
    }

    [Fact]
    public void Dependent_handles_frozen_objects_and_weak_handles_follow_the_retention_rules()
    {
        // ADR-024 (the 2.4 review): a dependent handle's value is retained by its key; a frozen object is a source; a weak
        // handle retains nothing; a retained object that points back into the set changes nothing.
        var builder = new SyntheticGraphBuilder();
        var key = builder.Object("App.Key", size: 10);
        var value = builder.Object("App.Value", size: 100);
        var literal = builder.Object("System.String", size: 30, heapKind: HeapKind.Frozen);
        var fromLiteral = builder.Object("App.Value", size: 7);
        var weakOnly = builder.Object("App.Key", size: 10);
        var back = builder.Object("App.Back", size: 5);
        builder.DependentHandle(key, value).Reference(literal, fromLiteral).Reference(value, back).Reference(back, key);
        builder.Root(key).Root(weakOnly, RootKind.WeakHandle, flags: RootFlags.None);
        var (graph, dominators) = Build(builder);

        var keys = SetRetention.Compute(graph, dominators, TypesNamed(graph, "App.Key"), cancellationToken: Ct);
        var values = SetRetention.Compute(graph, dominators, TypesNamed(graph, "App.Value"), cancellationToken: Ct);

        (keys.SetObjects, keys.RetainedObjects, keys.RetainedBytes).ShouldBe((1L, 3L, 115UL), "the key, its handle's value and what the value holds; not the weakly held key");
        (values.SetObjects, values.RetainedObjects, values.RetainedBytes).ShouldBe((2L, 3L, 112UL), "the value with what only it holds, and the frozen literal's own value, which only the literal reaches");
    }

    [Fact]
    public void An_empty_set_retains_nothing()
    {
        var builder = new SyntheticGraphBuilder();
        var cache = builder.Object("App.Cache", size: 40, module: "App.dll");
        builder.Reference(cache, builder.Object("App.Entry", size: 100, module: "App.dll"));
        builder.Root(cache);
        var (graph, dominators) = Build(builder);

        var result = SetRetention.Compute(graph, dominators, new bool[graph.Types.Count], cancellationToken: Ct);

        result.SetObjects.ShouldBe(0);
        result.RetainedObjects.ShouldBe(0);
        result.RetainedBytes.ShouldBe(0UL);
    }

    [Fact]
    public void A_set_that_does_not_match_the_graph_is_refused()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Root(builder.Object("App.Cache", size: 40));
        var (graph, dominators) = Build(builder);

        Should.Throw<ArgumentException>(() => SetRetention.Compute(graph, dominators, new bool[graph.Types.Count + 1], cancellationToken: Ct));
    }

    private static (HeapGraph Graph, Dominators Dominators) Build(SyntheticGraphBuilder builder)
    {
        var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        return (graph, Dominators.Compute(graph, reverse, cancellationToken: Ct));
    }

    private static int TypeIndex(HeapGraph graph, string name) =>
        Enumerable.Range(0, graph.Types.Count).Single(t => graph.Types.GetName(t) == name);

    private static bool[] TypesNamed(HeapGraph graph, string name)
    {
        var set = new bool[graph.Types.Count];
        set[TypeIndex(graph, name)] = true;
        return set;
    }
}
