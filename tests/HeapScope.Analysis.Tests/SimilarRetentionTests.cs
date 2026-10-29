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

        // A group is named by its pattern's key; the rank is only a hint.
        result.Groups.Select(g => g.Key).Distinct().Count().ShouldBe(3);
        SimilarRetention.MembersOf(graph, tree, reverse, set, both.Key, rank: 1, Ct).ShouldBe(items.Both);
        SimilarRetention.MembersOf(graph, tree, reverse, set, both.Key, rank: 0, Ct).ShouldBe(items.Both, "a wrong hint still finds the group by its key");
        SimilarRetention.MembersOf(graph, tree, reverse, set, "0000000000000000", rank: 1, Ct).ShouldBeEmpty();
    }

    [Fact]
    public void A_back_pointer_from_a_members_child_retains_nothing_and_does_not_split_its_group()
    {
        // The 3.2 review: child.Owner = member must not count as a way the member is retained.
        var builder = new SyntheticGraphBuilder();
        var list = builder.Object("App.List");
        builder.Root(list, RootKind.StaticField, "App.s_list");
        var items = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var item = builder.Object("App.Item", 10);
            builder.Reference(list, item);
            items.Add(item.Id);
            if (i % 2 == 0)
            {
                var child = builder.Object("App.Child");
                builder.Reference(item, child).Reference(child, item);
            }
        }

        var selfHeld = builder.Object("App.Item", 10);
        builder.Reference(list, selfHeld).Reference(selfHeld, selfHeld);
        items.Add(selfHeld.Id);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);

        var result = SimilarRetention.Group(graph, tree, reverse, ObjectSet.OfIndices(graph, items), maxGroups: 10, Ct);

        result.Groups.ShouldHaveSingleItem().Objects.ShouldBe(5, "back pointers and self references are no retention");
        result.Groups[0].PathCount.ShouldBe(1);
    }

    [Fact]
    public void A_member_that_is_a_root_and_one_held_through_a_dependent_handle_have_patterns_of_their_own()
    {
        var builder = new SyntheticGraphBuilder();
        var rooted = builder.Object("App.Item", 10);
        builder.Root(rooted, RootKind.StrongHandle);
        var both = builder.Object("App.Item", 10);
        var holder = builder.Object("App.Holder");
        builder.Root(both, RootKind.StrongHandle).Root(holder, RootKind.StaticField, "App.s_holder").Reference(holder, both);
        var key = builder.Object("App.Key");
        builder.Root(key, RootKind.StaticField, "App.s_key");
        var value = builder.Object("App.Item", 10);
        builder.DependentHandle(key, value);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);

        var result = SimilarRetention.Group(graph, tree, reverse, ObjectSet.OfIndices(graph, [rooted.Id, both.Id, value.Id]), maxGroups: 10, Ct);

        result.Groups.Count.ShouldBe(3);
        result.Groups.Single(g => g.ExampleObject == rooted.Id).Shown.ShouldHaveSingleItem().Steps.ShouldBeEmpty("a root's own path has no steps");
        result.Groups.Single(g => g.ExampleObject == both.Id).PathCount.ShouldBe(2, "its root and the holder");
        result.Groups.Single(g => g.ExampleObject == value.Id).Shown.ShouldHaveSingleItem().Steps.Select(graph.Types.GetName).ShouldBe(["App.Key"]);
        (result.Unreachable, result.LimitedMembers).ShouldBe((0L, 0L));
    }

    [Fact]
    public void Interfaces_are_read_through_the_next_object_when_one_fails_and_types_never_read_are_kept_apart()
    {
        var builder = new SyntheticGraphBuilder();
        var a1 = builder.Object("App.A", 10);
        var a2 = builder.Object("App.A", 10);
        var b = builder.Object("App.B", 20);
        builder.Object("App.C", 40);
        using var graph = builder.Build();
        var reader = new InterfaceReader(new() { [a1.Address] = null, [a2.Address] = ["App.I1"], [b.Address] = [] }, throwFor: graph.Addresses[3]);
        var failures = new List<(int Type, string Reason)>();

        var table = TypeInterfaces.Read(graph, reader, failures, cancellationToken: Ct);

        table.Keys(graph.TypeIndices[a1.Id]).ShouldBe(["App.I1"], "the second object answered");
        table.Keys(graph.TypeIndices[b.Id]).ShouldBe([TypeGroups.NoInterface]);
        table.Keys(graph.TypeIndices[3]).ShouldBe([TypeGroups.Unread]);
        failures.ShouldHaveSingleItem().Reason.ShouldBe(nameof(ArgumentException));
        table.UnreadTypes.ShouldBe(1);
        var rows = TypeGroups.Build(graph, TypeAggregation.Compute(graph, cancellationToken: Ct), GroupKey.Interface, interfaces: table).ToDictionary(r => r.Name, r => r.Count);
        rows.ShouldBe(new Dictionary<string, long> { ["App.I1"] = 2, [TypeGroups.NoInterface] = 1, [TypeGroups.Unread] = 1 }, ignoreOrder: true);
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

    private static TypeInterfaces Interfaces(HeapGraph graph, params (string Type, string[] Interfaces)[] types) =>
        new([.. Enumerable.Range(0, graph.Types.Count).Select(t => types.FirstOrDefault(x => x.Type == graph.Types.GetName(t)).Interfaces ?? [])]);

    /// <summary>Answers interfaces by address (null: cannot tell) and throws for one address.</summary>
    private sealed class InterfaceReader(Dictionary<ulong, string[]?> answers, ulong throwFor) : HeapScope.Core.IObjectReader
    {
        public IReadOnlyList<string>? ReadInterfaces(ulong address) =>
            address == throwFor ? throw new ArgumentException("bad object") : answers.GetValueOrDefault(address);

        public IReadOnlyList<HeapScope.Core.ObjectField> ReadFields(ulong address, int maxElements, int maxText, CancellationToken cancellationToken) => throw new NotSupportedException();

        public HeapScope.Core.ObjectValue? ReadField(ulong address, string path, int maxText = 1000) => throw new NotSupportedException();

        public string? ReadString(ulong address, int maxLength) => throw new NotSupportedException();

        public void ReadStrings(IReadOnlyList<ulong> addresses, int maxLength, Action<int, string?> onEach, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ReadStringPieces(IReadOnlyList<ulong> addresses, HeapScope.Core.StringPieceHandler onPiece, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
