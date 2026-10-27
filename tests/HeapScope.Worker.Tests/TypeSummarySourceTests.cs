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
using HeapScope.Contracts;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>Grouping, filtering, sorting and formatting of type summary rows (sprint 1.3).</summary>
public sealed class TypeSummarySourceTests : IDisposable
{
    private readonly HeapGraph _graph;
    private readonly TypeAggregation _aggregation;

    public TypeSummarySourceTests()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Object("App.Model.Order", 100, HeapKind.Gen0, module: @"C:\app\App.dll", methodTable: 0x10);
        builder.Object("App.Model.Order", 100, HeapKind.Gen2, module: @"C:\app\App.dll", methodTable: 0x10);
        builder.Object("App.Model.Customer", 200, HeapKind.Gen2, module: @"C:\app\App.dll", methodTable: 0x20);
        builder.Object("App.Cache.Entry", 200, HeapKind.Gen1, module: @"C:\app\App.dll", methodTable: 0x30);
        builder.Object("System.Byte[]", 400, HeapKind.LargeObjectHeap, module: null, methodTable: 0x40);
        _graph = builder.Build();
        _aggregation = TypeAggregation.Compute(_graph, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Bytes", true, new[] { "System.Byte[]", "App.Cache.Entry", "App.Model.Customer", "App.Model.Order" })]
    [InlineData("Bytes", false, new[] { "App.Cache.Entry", "App.Model.Customer", "App.Model.Order", "System.Byte[]" })]
    [InlineData("Objects", true, new[] { "App.Model.Order", "App.Cache.Entry", "App.Model.Customer", "System.Byte[]" })]
    [InlineData("Name", false, new[] { "App.Cache.Entry", "App.Model.Customer", "App.Model.Order", "System.Byte[]" })]
    [InlineData("Name", true, new[] { "System.Byte[]", "App.Model.Order", "App.Model.Customer", "App.Cache.Entry" })]
    public void Rows_sort_by_the_column_and_break_ties_by_name_ascending(string sort, bool descending, string[] expected)
    {
        // Bytes: Order, Customer and Entry all have 200 bytes; Count: Entry, Customer and Byte[] all have one object.
        var source = Create(new TypeSummaryRequest(Guid.Empty, SortColumn: sort, Descending: descending));

        Names(source).ShouldBe(expected);
    }

    [Fact]
    public void Filtering_keeps_matching_rows_and_counts_the_rows_before_it()
    {
        var source = Create(new TypeSummaryRequest(Guid.Empty, Filter: "model"));

        Names(source).ShouldBe(["App.Model.Customer", "App.Model.Order"]);
        source.UnfilteredCount.ShouldBe(4);
    }

    [Fact]
    public void Namespaces_and_assemblies_group_rows_and_filter_on_their_own_names()
    {
        var namespaces = Create(new TypeSummaryRequest(Guid.Empty, TypeGrouping.Namespace, Filter: "app"));
        Names(namespaces).ShouldBe(["App.Model", "App.Cache"]);
        namespaces.UnfilteredCount.ShouldBe(3);

        var assemblies = Create(new TypeSummaryRequest(Guid.Empty, TypeGrouping.Assembly));
        Names(assemblies).ShouldBe(["App.dll", "(unknown assembly)"], "assemblies are named by file (2.3.5 review L2)");
        Row(assemblies, 0)[1].ShouldBe("4");
    }

    [Fact]
    public void Columns_add_bytes_per_heap_kind_only_with_generations()
    {
        var plain = Create(new TypeSummaryRequest(Guid.Empty), generations: false);
        plain.Columns.Select(c => c.Name).ShouldBe(["Name", "Objects", "Bytes", "Percent", "TypeName", "Module"]);
        plain.Columns[0].IsKey.ShouldBeTrue();

        var withKinds = Create(new TypeSummaryRequest(Guid.Empty, Filter: "Order"), generations: true);
        withKinds.Columns.Select(c => c.Name).ShouldBe(
            ["Name", "Objects", "Bytes", "Percent", "Gen0", "Gen1", "Gen2", "LargeObjectHeap", "PinnedObjectHeap", "Frozen", "TypeName", "Module"]);
        Row(withKinds, 0).ShouldBe(["App.Model.Order", "2", "200", "20", "100", "0", "100", "0", "0", "0", "App.Model.Order", "App.dll"]);
        Create(new TypeSummaryRequest(Guid.Empty, TypeGrouping.Namespace)).Columns.Select(c => c.Name).ShouldNotContain("TypeName");
    }

    [Fact]
    public void Unknown_grouping_or_sort_values_are_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Create(new TypeSummaryRequest(Guid.Empty, (TypeGrouping)7)));
        Should.Throw<ArgumentException>(() => Create(new TypeSummaryRequest(Guid.Empty, SortColumn: "Size"))).Message.ShouldContain("Objects");
        Should.Throw<ArgumentException>(() => Create(new TypeSummaryRequest(Guid.Empty, SortColumn: "Gen0"))).Message.ShouldContain("Percent");
    }

    [Fact]
    public void An_empty_graph_has_no_rows_and_no_division_by_zero()
    {
        using var empty = new SyntheticGraphBuilder().Build();
        var source = TypeSummarySource.Create(empty, TypeAggregation.Compute(empty, cancellationToken: TestContext.Current.CancellationToken), new TypeSummaryRequest(Guid.Empty), generations: true);

        source.Count.ShouldBe(0);
        source.UnfilteredCount.ShouldBe(0);
    }

    public void Dispose() => _graph.Dispose();

    [Fact]
    public void Filter_terms_combine_and_a_module_term_tests_the_module()
    {
        Names(Create(new TypeSummaryRequest(Guid.Empty, Filter: "m:app.dll -customer"))).ShouldBe(["App.Cache.Entry", "App.Model.Order"]);
        Names(Create(new TypeSummaryRequest(Guid.Empty, Filter: "-module:app"))).ShouldBe(["System.Byte[]"]);
        Names(Create(new TypeSummaryRequest(Guid.Empty, TypeGrouping.Namespace, Filter: "module:app"))).ShouldBeEmpty("namespace groups have no module");
    }

    [Fact]
    public void Inspection_columns_carry_their_hidden_and_value_flags_onto_the_wire()
    {
        // 2.7 review: the inspection source's flags, which exports rely on.
        HeapScope.Analysis.Inspections.InspectionColumn[] columns =
        [
            new(HeapScope.Analysis.Inspections.InspectionColumns.Value, HeapScope.Analysis.Inspections.InspectionColumnKind.Text, HasValues: true),
            new(InspectionColumns.Address, HeapScope.Analysis.Inspections.InspectionColumnKind.Address, IsKey: true),
            new(InspectionColumns.TypeName, HeapScope.Analysis.Inspections.InspectionColumnKind.Text),
            new(InspectionColumns.Module, HeapScope.Analysis.Inspections.InspectionColumnKind.Text),
            new(InspectionColumns.Scope, HeapScope.Analysis.Inspections.InspectionColumnKind.Text),
        ];
        var source = new InspectionSource(new HeapScope.Analysis.Inspections.RowTable<int>(columns, [], 0, (_, _) => { }));

        source.Columns.Select(c => (c.Name, c.IsHidden, c.HasValues)).ShouldBe(
        [
            (HeapScope.Analysis.Inspections.InspectionColumns.Value, false, true), (InspectionColumns.Address, false, false), (InspectionColumns.TypeName, true, false),
            (InspectionColumns.Module, true, false), (InspectionColumns.Scope, true, false),
        ]);
    }

    [Fact]
    public void Link_columns_are_flagged_hidden_and_no_column_carries_values()
    {
        var columns = Create(new TypeSummaryRequest(Guid.Empty)).Columns;

        columns.Where(c => c.IsHidden).Select(c => c.Name).ShouldBe([TypeSummaryColumns.TypeName, TypeSummaryColumns.Module]);
        columns.ShouldAllBe(c => !c.HasValues);
    }

    private TypeSummarySource Create(TypeSummaryRequest request, bool generations = false) =>
        TypeSummarySource.Create(_graph, _aggregation, request, generations);

    private static string?[] Row(TypeSummarySource source, int index)
    {
        var cells = new string?[source.Columns.Count];
        source.FormatRow(index, cells);
        return cells;
    }

    private static List<string?> Names(TypeSummarySource source) => [.. Enumerable.Range(0, source.Count).Select(i => Row(source, i)[0])];
}
