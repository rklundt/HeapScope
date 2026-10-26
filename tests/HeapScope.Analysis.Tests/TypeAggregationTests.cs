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

namespace HeapScope.Analysis.Tests;

public sealed class TypeAggregationTests
{
    [Fact]
    public void Totals_per_type_and_heap_kind_equal_the_sum_over_all_objects()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Object("App.Order", 40, HeapKind.Gen0);
        builder.Object("App.Order", 40, HeapKind.Gen2);
        builder.Object("System.Byte[]", 100_000, HeapKind.LargeObjectHeap);
        builder.Object("System.String", 30, HeapKind.Frozen);
        using var graph = builder.Build();

        var aggregation = TypeAggregation.Compute(graph, cancellationToken: TestContext.Current.CancellationToken);

        aggregation.TotalObjects.ShouldBe(4);
        aggregation.TotalBytes.ShouldBe(graph.TotalSize);
        var order = TypeIndex(graph, "App.Order");
        aggregation.Counts[order].ShouldBe(2);
        aggregation.Bytes[order].ShouldBe(80UL);
        aggregation.BytesIn(order, HeapKind.Gen0).ShouldBe(40UL);
        aggregation.BytesIn(order, HeapKind.Gen2).ShouldBe(40UL);
        aggregation.KindBytes[(int)HeapKind.LargeObjectHeap].ShouldBe(100_000UL);
        aggregation.KindObjects[(int)HeapKind.Frozen].ShouldBe(1);
    }

    [Fact]
    public void The_parallel_pass_over_millions_of_objects_matches_a_plain_count()
    {
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(3_500_000, 1, TypeCount: 50), cancellationToken: TestContext.Current.CancellationToken);

        var aggregation = TypeAggregation.Compute(graph, cancellationToken: TestContext.Current.CancellationToken);

        var expected = new long[graph.Types.Count];
        foreach (var type in graph.TypeIndices.Span)
        {
            expected[type]++;
        }

        aggregation.Counts.ShouldBe(expected);
        aggregation.TotalBytes.ShouldBe(graph.TotalSize);
        aggregation.KindObjects.Sum().ShouldBe(graph.ObjectCount);
    }

    [Fact]
    public void Cancellation_stops_the_pass()
    {
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(2_000_000, 0), cancellationToken: TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Should.Throw<OperationCanceledException>(() => TypeAggregation.Compute(graph, cancellationToken: canceled.Token));
    }

    [Fact]
    public void Rows_group_by_namespace_and_merge_their_totals()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Object("App.Model.Order", 40);
        builder.Object("App.Model.Customer", 60);
        builder.Object("App.Order", 10);
        using var graph = builder.Build();
        var aggregation = TypeAggregation.Compute(graph, cancellationToken: TestContext.Current.CancellationToken);

        var rows = TypeGroups.Build(graph, aggregation, GroupKey.Namespace).ToDictionary(r => r.Name);

        rows["App.Model"].Count.ShouldBe(2);
        rows["App.Model"].Bytes.ShouldBe(100UL);
        rows["App"].Bytes.ShouldBe(10UL);
    }

    [Fact]
    public void Objects_of_4_GiB_or_more_and_unknown_heap_kinds_are_counted_exactly()
    {
        const ulong huge = (5UL << 30) + 24;
        var builder = new SyntheticGraphBuilder();
        builder.Object("System.Byte[]", huge, HeapKind.LargeObjectHeap);
        builder.Object("System.Byte[]", 1000, HeapKind.LargeObjectHeap);
        builder.Object("App.Lost", 40, HeapKind.Unknown);
        using var graph = builder.Build();

        var aggregation = TypeAggregation.Compute(graph, cancellationToken: TestContext.Current.CancellationToken);

        var bytes = TypeIndex(graph, "System.Byte[]");
        aggregation.Bytes[bytes].ShouldBe(huge + 1000);
        aggregation.BytesIn(bytes, HeapKind.LargeObjectHeap).ShouldBe(huge + 1000);
        aggregation.KindBytes[(int)HeapKind.Unknown].ShouldBe(40UL);
        aggregation.TotalBytes.ShouldBe(huge + 1040);
        aggregation.TotalBytes.ShouldBe(graph.TotalSize);
    }

    [Fact]
    public void Distinct_types_that_share_a_name_keep_their_own_rows()
    {
        // A plugin loaded into two load contexts: two method tables, one name, one module path; plus a same-named
        // type from another module.
        var builder = new SyntheticGraphBuilder();
        builder.Object("Plugin.Cache", 40, module: @"C:\app\Plugin.dll", methodTable: 0x1000);
        builder.Object("Plugin.Cache", 40, module: @"C:\app\Plugin.dll", methodTable: 0x1000);
        builder.Object("Plugin.Cache", 40, module: @"C:\app\Plugin.dll", methodTable: 0x2000);
        builder.Object("Plugin.Cache", 40, module: @"C:\other\Fork.dll", methodTable: 0x3000);
        builder.Object("App.Order", 10, module: @"C:\app\App.dll", methodTable: 0x4000);
        using var graph = builder.Build();
        var aggregation = TypeAggregation.Compute(graph, cancellationToken: TestContext.Current.CancellationToken);

        var rows = TypeGroups.Build(graph, aggregation, GroupKey.Type).ToDictionary(r => r.Name);

        rows.Keys.Order(StringComparer.Ordinal).ShouldBe(
            ["App.Order", "Plugin.Cache [Fork.dll]", "Plugin.Cache [Plugin.dll] #1", "Plugin.Cache [Plugin.dll] #2"]);
        rows["Plugin.Cache [Plugin.dll] #1"].Count.ShouldBe(2);
        rows["Plugin.Cache [Plugin.dll] #2"].Count.ShouldBe(1);
        TypeGroups.Build(graph, aggregation, GroupKey.Assembly).ToDictionary(r => r.Name)["Plugin.dll"].Count.ShouldBe(3);
    }

    [Fact]
    public void Assembly_rows_are_keyed_by_file_name_as_the_open_instances_link_matches_them()
    {
        // 2.3.5 review L2: one key for a row and for its link; paths recorded on another system or in another case merge.
        var builder = new SyntheticGraphBuilder();
        builder.Object("App.Order", 10, module: @"C:\app\App.dll", methodTable: 0x1000);
        builder.Object("App.Customer", 10, module: "/opt/app/app.DLL", methodTable: 0x2000);
        using var graph = builder.Build();
        var aggregation = TypeAggregation.Compute(graph, cancellationToken: TestContext.Current.CancellationToken);

        var row = TypeGroups.Build(graph, aggregation, GroupKey.Assembly).ShouldHaveSingleItem();

        row.Name.ShouldBe("App.dll");
        row.Count.ShouldBe(2);
        TypeGroups.MatchTypes(graph, "App.Customer", row.Name, out var matched).Count(m => m).ShouldBe(1);
        matched.ShouldBe(1);
    }

    [Theory]
    [InlineData("System.String", "System")]
    [InlineData("System.Collections.Generic.Dictionary<System.Int32, Fixtures.Leaks.StaticCacheEntry>+Entry[]", "System.Collections.Generic")]
    [InlineData("Fixtures.Leaks.Outer+Inner", "Fixtures.Leaks")]
    [InlineData("System.Byte[]", "System")]
    [InlineData("NoNamespace", "(global)")]
    [InlineData("<>f__AnonymousType0<System.Int32>", "(global)")]
    public void Namespace_of_a_type_name_ignores_generics_nesting_and_arrays(string typeName, string expected)
    {
        TypeGroups.NamespaceOf(typeName).ShouldBe(expected);
    }

    private static int TypeIndex(HeapGraph graph, string name)
    {
        for (var t = 0; t < graph.Types.Count; t++)
        {
            if (graph.Types.GetName(t) == name)
            {
                return t;
            }
        }

        throw new InvalidOperationException(name);
    }
}
