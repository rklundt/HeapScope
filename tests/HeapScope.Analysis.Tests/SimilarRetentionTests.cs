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

/// <summary>Sprint 3.2 (ADR-031): Similar Retention, key paths of a set, and interface grouping.</summary>
public sealed class SimilarRetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Members_held_the_same_way_form_one_group_and_each_group_shows_its_most_different_paths()
    {
        var (graph, items) = Patterns();
        using var owned = graph;
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);
        var set = ObjectSet.OfIndices(graph, items.All);

        var result = SimilarRetention.Group(graph, tree, reverse, set, maxGroups: 10, Ct);

        result.Groups.Select(g => g.Objects).ShouldBe([3L, 2L, 1L]);
        (result.GroupCount, result.Unreachable, result.UnreachableBytes, result.LimitedMembers).ShouldBe((3, 1L, 10UL, 0L));
        var listOnly = result.Groups[0];
        (listOnly.PathCount, listOnly.Shown.Count, listOnly.Bytes, listOnly.Types).ShouldBe((1, 1, 30UL, 1));
        Names(graph, listOnly.Shown[0]).ShouldBe(["App.List", "App.Item[]"]);
        var both = result.Groups[1];
        (both.PathCount, both.Shown.Count).ShouldBe((2, 2));
        both.Shown.Select(p => Names(graph, p)).ShouldBe([["App.List", "App.Item[]"], ["App.Map", "App.Entry[]"]], ignoreOrder: true);
        both.Shown.Select(p => PathRoots.Detail(graph, p.Root)).ShouldBe(["App.s_list", "App.s_map"], ignoreOrder: true);
        Names(graph, result.Groups[2].Shown.ShouldHaveSingleItem()).ShouldBe(["App.Holder[]", "App.Holder"]);

        SimilarRetention.MembersOf(graph, tree, reverse, set, rank: 1, Ct).ShouldBe(items.Both);
        SimilarRetention.MembersOf(graph, tree, reverse, set, rank: 3, Ct).ShouldBeEmpty();
    }

    [Fact]
    public void Groups_beyond_the_limit_are_summed_and_referrers_beyond_sixteen_are_counted()
    {
        var builder = new SyntheticGraphBuilder();
        var items = new List<int>();
        for (var pattern = 0; pattern < 3; pattern++)
        {
            var holder = builder.Object("App.Holder" + pattern);
            builder.Root(holder, RootKind.StaticField, "App.s_" + pattern);
            for (var i = 0; i <= pattern; i++)
            {
                var item = builder.Object("App.Item", 10);
                builder.Reference(holder, item);
                items.Add(item.Id);
            }
        }

        var crowded = builder.Object("App.Item", 10);
        items.Add(crowded.Id);
        for (var i = 0; i < SimilarRetention.MaxReferrers + 2; i++)
        {
            var holder = builder.Object("App.Many" + i);
            builder.Root(holder, RootKind.StaticField, "App.s_many" + i);
            builder.Reference(holder, crowded);
        }

        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);

        var result = SimilarRetention.Group(graph, tree, reverse, ObjectSet.OfIndices(graph, items), maxGroups: 2, Ct);

        result.Groups.Select(g => g.Objects).ShouldBe([3L, 2L]);
        (result.GroupCount, result.OtherObjects, result.OtherBytes, result.LimitedMembers).ShouldBe((4, 2L, 20UL, 1L));
    }

    [Fact]
    public void A_sets_key_paths_equal_its_types_key_paths()
    {
        var (graph, items) = Patterns();
        using var owned = graph;
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);

        var byMembers = KeyPaths.ForMembers(graph, tree, ObjectSet.OfIndices(graph, items.All).Members, cancellationToken: Ct);
        var byType = KeyPaths.ForTypes(graph, tree, TypeGroups.MatchTypes(graph, "App.Item", null, out _), cancellationToken: Ct);

        byMembers.Paths.Select(p => (p.Objects, p.Bytes, Names(graph, p).Count)).ShouldBe(byType.Paths.Select(p => (p.Objects, p.Bytes, Names(graph, p).Count)));
        (byMembers.Objects, byMembers.Unreachable).ShouldBe((byType.Objects, byType.Unreachable));
    }

    [Fact]
    public void Interface_rows_count_a_type_under_each_of_its_interfaces()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Object("App.A", 10);
        builder.Object("App.A", 10);
        builder.Object("App.B", 20);
        builder.Object("App.C", 40);
        using var graph = builder.Build();
        var interfaces = Interfaces(graph, ("App.A", ["App.I1", "App.I2"]), ("App.B", ["App.I1"]));
        var aggregation = TypeAggregation.Compute(graph, cancellationToken: Ct);

        var rows = TypeGroups.Build(graph, aggregation, GroupKey.Interface, interfaces: interfaces).ToDictionary(r => r.Name, r => (r.Count, r.Bytes));

        rows.ShouldBe(new Dictionary<string, (long, ulong)> { ["App.I1"] = (3, 40), ["App.I2"] = (2, 20), [TypeGroups.NoInterface] = (1, 40) }, ignoreOrder: true);
        TypeGroups.MatchInterface(graph, interfaces, "App.I1", out var matched).Count(m => m).ShouldBe(2);
        matched.ShouldBe(2);
        Should.Throw<ArgumentException>(() => TypeGroups.GroupOfType(graph, GroupKey.Interface, out _));
        Should.Throw<ArgumentNullException>(() => TypeGroups.Build(graph, aggregation, GroupKey.Interface));
    }

    [Fact]
    public void A_diff_by_interface_groups_each_snapshot_by_its_own_interfaces()
    {
        var before = new SyntheticGraphBuilder();
        before.Object("App.A", 10);
        var after = new SyntheticGraphBuilder();
        after.Object("App.A", 10);
        after.Object("App.A", 10);
        after.Object("App.B", 30);
        using var baseline = before.Build();
        using var current = after.Build();

        var rows = TypeDiff.Build(
            baseline,
            TypeAggregation.Compute(baseline, cancellationToken: Ct),
            current,
            TypeAggregation.Compute(current, cancellationToken: Ct),
            GroupKey.Interface,
            baselineInterfaces: Interfaces(baseline, ("App.A", ["App.I1"])),
            currentInterfaces: Interfaces(current, ("App.A", ["App.I1"]), ("App.B", ["App.I1", "App.I2"]))).ToDictionary(r => r.Name);

        (rows["App.I1"].BaselineCount, rows["App.I1"].CurrentCount, rows["App.I1"].CurrentBytes).ShouldBe((1L, 3L, 50UL));
        (rows["App.I2"].BaselineCount, rows["App.I2"].CurrentCount).ShouldBe((0L, 1L));
        rows["App.I1"].TypeName.ShouldBeNull("an interface row opens no single type");
    }

    /// <summary>
    /// App.Item objects held three ways (the RetentionPatterns fixture in small): three by a list only, two by the list
    /// and a map, one by a holder in an array; one more is unreachable.
    /// </summary>
    private static (HeapGraph Graph, (int[] All, int[] Both) Items) Patterns()
    {
        var builder = new SyntheticGraphBuilder();
        var list = builder.Object("App.List");
        var listArray = builder.Object("App.Item[]");
        builder.Root(list, RootKind.StaticField, "App.s_list").Reference(list, listArray);
        var map = builder.Object("App.Map");
        var entries = builder.Object("App.Entry[]");
        builder.Root(map, RootKind.StaticField, "App.s_map").Reference(map, entries);
        var holders = builder.Object("App.Holder[]");
        builder.Root(holders, RootKind.StaticField, "App.s_holders");
        var all = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var item = builder.Object("App.Item", 10);
            builder.Reference(listArray, item);
            all.Add(item.Id);
        }

        var both = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            var item = builder.Object("App.Item", 10);
            builder.Reference(listArray, item).Reference(entries, item);
            both.Add(item.Id);
        }

        var holder = builder.Object("App.Holder");
        var held = builder.Object("App.Item", 10);
        builder.Reference(holders, holder).Reference(holder, held);
        all.Add(held.Id);
        all.Add(builder.Object("App.Item", 10).Id);
        all.AddRange(both);
        return (builder.Build(), ([.. all.Order()], [.. both]));
    }

    private static List<string> Names(HeapGraph graph, KeyPath path) => [.. path.Steps.Select(graph.Types.GetName)];

    private static string[][] Interfaces(HeapGraph graph, params (string Type, string[] Interfaces)[] types) =>
        [.. Enumerable.Range(0, graph.Types.Count).Select(t => types.FirstOrDefault(x => x.Type == graph.Types.GetName(t)).Interfaces ?? [])];
}
