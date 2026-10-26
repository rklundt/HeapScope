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
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>Sprint 2.3: retained bytes columns of type summaries and instances.</summary>
public sealed class RetainedSourcesTests : IDisposable
{
    private readonly HeapGraph _graph;
    private readonly ReverseReferences _reverse;
    private readonly Dominators _dominators;

    public RetainedSourcesTests()
    {
        // An App.Model.Order list (two orders, the first holding the second) and a Lib.Buffer of the second order, rooted by
        // App.Cache; one more order is garbage.
        var builder = new SyntheticGraphBuilder();
        var cache = builder.Object("App.Cache", 24, module: "App.dll");
        var first = builder.Object("App.Model.Order", 100, module: "App.dll");
        var second = builder.Object("App.Model.Order", 100, module: "App.dll");
        var buffer = builder.Object("Lib.Buffer", 1000, module: "Lib.dll");
        builder.Object("App.Model.Order", 100, module: "App.dll");
        builder.Reference(cache, first).Reference(first, second).Reference(second, buffer).Root(cache);
        _graph = builder.Build();
        _reverse = ReverseReferences.Build(_graph, cancellationToken: Ct);
        _dominators = Dominators.Compute(_graph, _reverse, cancellationToken: Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_type_summary_adds_retained_bytes_per_type_and_sorts_by_them()
    {
        var source = Summary(GroupKey.Type, new TypeSummaryRequest(Guid.Empty, SortColumn: TypeSummaryColumns.Retained));

        source.Columns.Select(c => c.Name).ShouldBe(["Name", "Objects", "Bytes", "Percent", "Retained", "TypeName", "Module"]);
        var rows = Rows(source).ToDictionary(r => r[0]!, r => r[4]);
        rows["App.Cache"].ShouldBe("1224");
        rows["App.Model.Order"].ShouldBe("1200", "the first order's retained size; the second is inside it and the garbage counts nothing");
        rows["Lib.Buffer"].ShouldBe("1000");
        Rows(source)[0][0].ShouldBe("App.Cache", "largest retained first");
    }

    [Fact]
    public void The_trees_keys_are_the_ones_clients_link_with()
    {
        var view = DominatorTreeRows.Build(_graph, _dominators, -1, new HashSet<string>(), DominatorGrouping.Type, 100, Ct);
        var cache = _graph.Addresses[0];

        view.Rows[0].Key.ShouldBe(DominatorTreeKeys.TypeGroup(DominatorTreeKeys.Root, "App.Cache", "App.dll"));
        DominatorTreeRows.ObjectKey(cache).ShouldBe(DominatorTreeKeys.Object(cache));
        DominatorTreeRows.RootKey.ShouldBe(DominatorTreeKeys.Root);
    }

    [Fact]
    public void What_a_set_retains_is_listed_by_type_largest_first_with_the_keys_that_open_instances()
    {
        var set = TypeGroups.MatchTypes(_graph, "App.Model.Order", "App.dll", out _);
        var source = RetainedBySetSource.Create(_graph, TypeAggregation.Compute(_graph, cancellationToken: Ct), SetRetention.Compute(_graph, _dominators, set, cancellationToken: Ct));

        source.Columns.Select(c => (c.Name, c.Bound)).ShouldAllBe(c => c.Bound == ValueBound.Exact);
        Rows(source).Select(r => (r[0], r[1], r[2], r[3], r[4], r[5])).ShouldBe(
        [
            ("Lib.Buffer", "1", "1000", "83.33", "Lib.Buffer", "Lib.dll"),
            ("App.Model.Order", "2", "200", "16.67", "App.Model.Order", "App.dll"),
        ]);
        source.Describe(1).ShouldBe(["Lib.Buffer 1 objects 1000 bytes"]);
    }

    [Fact]
    public void Namespaces_and_assemblies_get_their_own_lower_bounds()
    {
        var byNamespace = Rows(Summary(GroupKey.Namespace, new TypeSummaryRequest(Guid.Empty, TypeGrouping.Namespace))).ToDictionary(r => r[0]!, r => r[4]);
        var byAssembly = Rows(Summary(GroupKey.Assembly, new TypeSummaryRequest(Guid.Empty, TypeGrouping.Assembly))).ToDictionary(r => r[0]!, r => r[4]);

        (byNamespace["App"], byNamespace["App.Model"], byNamespace["Lib"]).ShouldBe(("1224", "1200", "1000"));
        (byAssembly["App.dll"], byAssembly["Lib.dll"]).ShouldBe(("1224", "1000"));
    }

    [Fact]
    public void Without_retained_sizes_the_summary_has_no_such_column_or_sort()
    {
        var aggregation = TypeAggregation.Compute(_graph, cancellationToken: Ct);
        var source = TypeSummarySource.Create(_graph, aggregation, new TypeSummaryRequest(Guid.Empty), generations: false);

        source.Columns.ShouldNotContain(c => c.Name == TypeSummaryColumns.Retained);
        Should.Throw<ArgumentException>(() => TypeSummarySource.Create(_graph, aggregation, new TypeSummaryRequest(Guid.Empty, SortColumn: TypeSummaryColumns.Retained), false));
    }

    [Fact]
    public void Instances_show_each_objects_retained_bytes_and_nothing_for_garbage()
    {
        var request = new InstancesRequest(Guid.Empty, "App.Model.Order", "App.dll", SortColumn: InstanceColumns.Retained, IncludeRetained: true);

        var source = InstancesSource.Create(_graph, null, request, Ct, dominators: _dominators);

        source.Columns[^1].Name.ShouldBe(InstanceColumns.Retained);
        Rows(source).Select(r => r[5]).ShouldBe(["1200", "1100", null]);
        Should.Throw<ArgumentException>(() => InstancesSource.Create(_graph, null, request, Ct));
    }

    [Fact]
    public void Retained_sizes_above_four_gigabytes_still_sort_exactly()
    {
        var builder = new SyntheticGraphBuilder();
        var owner = builder.Object("App.Owner");
        var big = builder.Object("App.Owner");
        var huge = builder.Object("App.Blob", 6UL << 30);
        var small = builder.Object("App.Blob", (5UL << 30) + 1);
        builder.Reference(big, huge).Reference(owner, small).Root(owner).Root(big);
        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        using var dominators = Dominators.Compute(graph, reverse, cancellationToken: Ct);

        var source = InstancesSource.Create(graph, null, new InstancesRequest(Guid.Empty, "App.Owner", null, SortColumn: InstanceColumns.Retained, IncludeRetained: true), Ct, dominators: dominators);

        Rows(source).Select(r => r[0]).ShouldBe([$"0x{graph.Addresses[1]:x}", $"0x{graph.Addresses[0]:x}"]);
    }

    [Fact]
    public void Requests_are_checked_before_any_dominators_are_computed()
    {
        // Review M7: a typo costs nothing, and a type with no objects needs no retained column.
        var ok = new InstancesRequest(Guid.Empty, "App.Model.Order", "App.dll", SortColumn: InstanceColumns.Retained, IncludeRetained: true);

        InstancesSource.Validate(_graph, ok).ShouldBeTrue();
        InstancesSource.Validate(_graph, ok with { TypeName = "App.Nothing" }).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => InstancesSource.Validate(_graph, ok with { Filter = "/(/" }));
        Should.Throw<ArgumentException>(() => InstancesSource.Validate(_graph, ok with { IncludeRetained = false }));
        TypeSummarySource.Validate(new TypeSummaryRequest(Guid.Empty, SortColumn: TypeSummaryColumns.Retained, IncludeRetained: true), generations: false);
        Should.Throw<ArgumentException>(() => TypeSummarySource.Validate(new TypeSummaryRequest(Guid.Empty, SortColumn: "Gen0", IncludeRetained: true), generations: false));
    }

    public void Dispose()
    {
        _dominators.Dispose();
        _reverse.Dispose();
        _graph.Dispose();
    }

    private TypeSummarySource Summary(GroupKey grouping, TypeSummaryRequest request)
    {
        var aggregation = TypeAggregation.Compute(_graph, cancellationToken: Ct);
        var groups = TypeGroups.GroupOfType(_graph, grouping, out var names);
        var retained = GroupRetention.Compute(_graph, _dominators, groups, names.Length, cancellationToken: Ct);
        return TypeSummarySource.Create(_graph, aggregation, request with { IncludeRetained = true }, generations: false, retained);
    }

    private static List<string?[]> Rows(IResultSource source) =>
    [
        .. Enumerable.Range(0, source.Count).Select(i =>
        {
            var cells = new string?[source.Columns.Count];
            source.FormatRow(i, cells);
            return cells;
        }),
    ];
}
