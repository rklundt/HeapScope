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

using HeapScope.Analysis;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>Sprint 2.2: retention results on the wire.</summary>
public sealed class RetentionMappingTests : IDisposable
{
    private static readonly ReverseIndexInfo s_index = new(false, SnapshotMemoryMode.InMemory, 0, 0, true);

    private readonly HeapGraph _graph;
    private readonly ReverseReferences _reverse;
    private readonly SyntheticObject _value;
    private readonly SyntheticObject _key;
    private readonly SyntheticObject _dictionary;
    private readonly SyntheticObject _entries;

    public RetentionMappingTests()
    {
        // App.Cache (static) -> Dictionary -> Entry[] -> App.Key ~(dependent handle)~> App.Value; App.Other (static) -> App.Key.
        var builder = new SyntheticGraphBuilder();
        var cache = builder.Object("App.Cache");
        _dictionary = builder.Object("System.Collections.Generic.Dictionary<App.Key, App.Cache>");
        _entries = builder.Object("System.Collections.Generic.Dictionary<App.Key, App.Cache>+Entry[]");
        _key = builder.Object("App.Key");
        _value = builder.Object("App.Value", 64);
        var other = builder.Object("App.Other");
        builder.Reference(cache, _dictionary).Reference(_dictionary, _entries).Reference(_entries, _key).Reference(other, _key);
        builder.DependentHandle(_key, _value);
        builder.Root(cache, RootKind.StaticField, "App.Program.s_cache").Root(other, RootKind.StaticField, "App.Program.s_other");
        _graph = builder.Build();
        _reverse = ReverseReferences.Build(_graph, cancellationToken: Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Nodes_carry_fields_roots_framework_steps_and_dependent_handles()
    {
        var tree = RetentionSearch.Find(_graph, _reverse, _value.Id, cancellationToken: Ct);
        var reader = new NamingReader(new Dictionary<ulong, string>
        {
            [_dictionary.Address] = "_items",
            [_entries.Address] = "_entries",
            [_key.Address] = "[3].key",
        });

        var result = RetentionMapping.ObjectPaths(_graph, tree, reader, null, true, s_index, Ct);

        result.Reachable.ShouldBeTrue();
        var byType = result.Nodes.ToDictionary(n => n.TypeName);
        byType["App.Value"].Id.ShouldBe(0);
        byType["App.Value"].RootsBelow.ShouldBe(2);
        byType["App.Key"].ViaDependentHandle.ShouldBeTrue("no field of the key holds the value");
        byType["App.Key"].Field.ShouldBeNull();
        byType["System.Collections.Generic.Dictionary<App.Key, App.Cache>+Entry[]"].Field.ShouldBe("[3].key");
        byType["System.Collections.Generic.Dictionary<App.Key, App.Cache>+Entry[]"].IsFramework.ShouldBeTrue();
        byType["App.Cache"].Root!.Description.ShouldBe("Static field App.Program.s_cache");
        byType["App.Other"].Root!.Kind.ShouldBe(RootKindDto.StaticField);
        result.RootNodeIds.Select(id => result.Nodes[id].TypeName).ShouldBe(["App.Other", "App.Cache"], "closest first");
        result.KeyPaths.Select(p => string.Join(" > ", p.Steps.Select(s => s.TypeName))).ShouldBe(
            ["App.Other > App.Key", "App.Cache > System.Collections.Generic.Dictionary<App.Key, App.Cache> > System.Collections.Generic.Dictionary<App.Key, App.Cache>+Entry[] > App.Key"]);
        result.KeyPaths[0].ExampleAddress.ShouldBe(result.Nodes[result.RootNodeIds[0]].Address);
        result.FieldsNote.ShouldBeNull();
    }

    [Fact]
    public void Without_a_reader_the_fields_are_explained_and_an_unrooted_object_has_no_paths()
    {
        var builder = new SyntheticGraphBuilder();
        var garbage = builder.Object("App.Garbage");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var withPaths = RetentionMapping.ObjectPaths(_graph, RetentionSearch.Find(_graph, _reverse, _value.Id, cancellationToken: Ct), null, "Field values are not stored in a gcdump.", false, s_index, Ct);
        var unreachable = RetentionMapping.ObjectPaths(graph, RetentionSearch.Find(graph, reverse, garbage.Id, cancellationToken: Ct), null, "no reader", true, s_index, Ct);

        withPaths.FieldsNote.ShouldBe("Field values are not stored in a gcdump.");
        withPaths.CompleteGraph.ShouldBeFalse();
        withPaths.Nodes.Single(n => n.TypeName == "App.Key").ViaDependentHandle.ShouldBeTrue();
        (unreachable.Reachable, unreachable.Nodes.Count, unreachable.KeyPaths.Count, unreachable.FieldsNote).ShouldBe((false, 1, 0, (string?)null));
    }

    [Fact]
    public void A_long_list_folds_into_one_node_and_a_long_mixed_path_keeps_its_ends_around_a_gap()
    {
        // Review H1: a path through 100,000 objects must not become 100,000 nested nodes.
        var builder = new SyntheticGraphBuilder();
        var target = builder.Object("App.Target");
        var previous = target;
        for (var i = 0; i < 100_000; i++)
        {
            var node = builder.Object("App.Node");
            builder.Reference(node, previous);
            previous = node;
        }

        var head = builder.Object("App.Head");
        builder.Reference(head, previous).Root(head, RootKind.StaticField, "App.s_head");

        var mixedTarget = builder.Object("App.Target");
        previous = mixedTarget;
        for (var i = 0; i < 1_000; i++)
        {
            var link = builder.Object(i % 2 == 0 ? "App.Even" : "App.Odd");
            builder.Reference(link, previous);
            previous = link;
        }

        builder.Root(previous, RootKind.StaticField, "App.s_mixed");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var list = RetentionMapping.ObjectPaths(graph, RetentionSearch.Find(graph, reverse, target.Id, cancellationToken: Ct), null, null, true, s_index, Ct);
        var mixed = RetentionMapping.ObjectPaths(graph, RetentionSearch.Find(graph, reverse, mixedTarget.Id, cancellationToken: Ct), null, null, true, s_index, Ct);

        list.Nodes.Select(n => (n.TypeName, n.Folded)).ShouldBe([("App.Target", 0), ("App.Node", 99_999), ("App.Head", 0)]);
        list.Nodes[2].Root!.Description.ShouldBe("Static field App.s_head");
        mixed.Nodes.Count.ShouldBe(RetentionMapping.MaxPathSegments);
        var gap = mixed.Nodes.Single(n => n.Address == 0);
        (gap.TypeName, gap.Folded).ShouldBe(("(938 objects not shown)", 938), "1,001 objects, 63 of them kept");
        mixed.Nodes.Max(n => n.Depth).ShouldBeLessThan(RetentionMapping.MaxPathSegments);
        mixed.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void An_object_held_by_a_root_itself_is_its_own_root_node()
    {
        var builder = new SyntheticGraphBuilder();
        var local = builder.Object("App.Local");
        builder.Root(local, RootKind.ThreadStack, "thread 0x1a2c");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var result = RetentionMapping.ObjectPaths(graph, RetentionSearch.Find(graph, reverse, local.Id, cancellationToken: Ct), null, null, true, s_index, Ct);

        var node = result.Nodes.ShouldHaveSingleItem();
        node.Root!.Kind.ShouldBe(RootKindDto.ThreadStack);
        result.RootNodeIds.ShouldBe([0]);
        result.UnreachableNote.ShouldContain("garbage");
        RetentionMapping.UnreachableNote(SnapshotKind.GcDump).ShouldContain("recorded root");
    }

    [Fact]
    public void Paths_that_branch_inside_a_run_of_one_type_never_show_a_holder_on_the_wrong_object()
    {
        // H1 confirmation: target <- a1 <- c (root) and target <- a1 <- a2 <- b (root), a1 and a2 of one type.
        var builder = new SyntheticGraphBuilder();
        var target = builder.Object("App.Target");
        var a1 = builder.Object("App.Node");
        var a2 = builder.Object("App.Node");
        var c = builder.Object("App.Cache");
        var b = builder.Object("App.Head");
        builder.Reference(a1, target).Reference(c, a1).Reference(a2, a1).Reference(b, a2);
        builder.Root(c, RootKind.StaticField, "App.s_cache").Root(b, RootKind.StaticField, "App.s_head");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var result = RetentionMapping.ObjectPaths(graph, RetentionSearch.Find(graph, reverse, target.Id, cancellationToken: Ct), null, null, true, s_index, Ct);

        // Each root's chain of shown nodes holds exactly what it shows.
        foreach (var rootId in result.RootNodeIds)
        {
            var chain = new List<RetentionNodeDto>();
            for (var id = rootId; id >= 0; id = result.Nodes[id].ChildId)
            {
                chain.Add(result.Nodes[id]);
            }

            var shown = chain.Select(n => (n.TypeName, n.Folded)).ToList();
            if (chain[0].TypeName == "App.Cache")
            {
                shown.ShouldBe([("App.Cache", 0), ("App.Node", 0), ("App.Target", 0)]);
            }
            else
            {
                shown.ShouldBe([("App.Head", 0), ("App.Node", 1), ("App.Target", 0)]);
            }
        }
    }

    [Fact]
    public void A_key_path_over_an_alternating_chain_keeps_its_ends_around_one_step()
    {
        var builder = new SyntheticGraphBuilder();
        var target = builder.Object("App.Target");
        var previous = target;
        for (var i = 0; i < 1_000; i++)
        {
            var link = builder.Object(i % 2 == 0 ? "App.Even" : "App.Odd");
            builder.Reference(link, previous);
            previous = link;
        }

        builder.Root(previous, RootKind.StaticField, "App.s_chain");
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var objectPaths = RetentionMapping.ObjectPaths(graph, RetentionSearch.Find(graph, reverse, target.Id, cancellationToken: Ct), null, null, true, s_index, Ct);
        var tree = RootPathTree.Build(graph, cancellationToken: Ct);
        var typePath = RetentionMapping.KeyPath(graph, KeyPaths.ForTypes(graph, tree, TypeGroups.MatchTypes(graph, "App.Target", null, out _), cancellationToken: Ct).Paths.ShouldHaveSingleItem());

        foreach (var steps in new[] { objectPaths.KeyPaths.ShouldHaveSingleItem().Steps, typePath.Steps })
        {
            steps.Count.ShouldBe(RetentionMapping.MaxKeyPathSteps);
            steps[RetentionMapping.MaxKeyPathSteps / 2].TypeName.ShouldBe("(937 steps not shown)");
        }
    }

    [Fact]
    public void Roots_whose_paths_would_pass_the_node_limit_are_left_out()
    {
        var builder = new SyntheticGraphBuilder();
        var target = builder.Object("App.Target");
        for (var r = 0; r < 50; r++)
        {
            var previous = target;
            for (var i = 0; i < 70; i++)
            {
                var link = builder.Object(i % 2 == 0 ? "App.Even" : "App.Odd");
                builder.Reference(link, previous);
                previous = link;
            }

            builder.Root(previous, RootKind.StrongHandle);
        }

        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);

        var result = RetentionMapping.ObjectPaths(graph, RetentionSearch.Find(graph, reverse, target.Id, maxRoots: 50, cancellationToken: Ct), null, null, true, s_index, Ct);

        result.Truncated.ShouldBeTrue();
        result.StoppedEarly.ShouldBeTrue();
        result.Nodes.Count.ShouldBeLessThanOrEqualTo(RetentionMapping.MaxNodes);
        result.RootNodeIds.Count.ShouldBe((RetentionMapping.MaxNodes - 1) / (RetentionMapping.MaxPathSegments - 1));
    }

    [Fact]
    public void A_type_key_path_names_its_steps_and_example()
    {
        var tree = RootPathTree.Build(_graph, cancellationToken: Ct);
        var set = KeyPaths.ForTypes(_graph, tree, TypeGroups.MatchTypes(_graph, "App.Value", null, out _), cancellationToken: Ct);

        var path = RetentionMapping.KeyPath(_graph, set.Paths.ShouldHaveSingleItem());

        path.Steps.Select(s => (s.TypeName, s.IsFramework)).ShouldBe([("App.Other", false), ("App.Key", false)]);
        (path.TargetType, path.Objects, path.Bytes, path.ExampleAddress).ShouldBe(("App.Value", 1L, 64L, _value.Address));
        path.Root.Description.ShouldBe("Static field App.Program.s_other");
    }

    public void Dispose()
    {
        _reverse.Dispose();
        _graph.Dispose();
    }

    /// <summary>Answers reference paths from a table; nothing else is read.</summary>
    private sealed class NamingReader(IReadOnlyDictionary<ulong, string> paths) : IObjectReader
    {
        public IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, int maxText, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ObjectValue? ReadField(ulong address, string path, int maxText = 1000) => throw new NotSupportedException();

        public string? ReadString(ulong address, int maxLength) => throw new NotSupportedException();

        public void ReadStrings(IReadOnlyList<ulong> addresses, int maxLength, Action<int, string?> onEach, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ReadStringPieces(IReadOnlyList<ulong> addresses, StringPieceHandler onPiece, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken) => paths;
    }
}
