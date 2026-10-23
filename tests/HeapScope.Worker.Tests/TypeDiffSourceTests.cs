using HeapScope.Analysis;
using HeapScope.Contracts;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>Sorting, filtering, totals and formatting of snapshot diff rows (sprint 1.4).</summary>
public sealed class TypeDiffSourceTests : IDisposable
{
    private readonly HeapGraph _baseline;
    private readonly HeapGraph _current;
    private readonly TypeAggregation _baselineAggregation;
    private readonly TypeAggregation _currentAggregation;

    public TypeDiffSourceTests()
    {
        // Order: +3 objects, +120 bytes. Cache.Entry: +1 object, +500 bytes. Old: gone, -100 bytes. Fresh: new, +16 bytes.
        var before = new SyntheticGraphBuilder();
        Add(before, "App.Order", 40, 2);
        Add(before, "App.Cache.Entry", 500, 1);
        Add(before, "App.Old", 100, 1);
        var after = new SyntheticGraphBuilder();
        Add(after, "App.Order", 40, 5);
        Add(after, "App.Cache.Entry", 500, 2);
        Add(after, "App.Fresh", 16, 1);
        _baseline = before.Build();
        _current = after.Build();
        _baselineAggregation = TypeAggregation.Compute(_baseline, cancellationToken: TestContext.Current.CancellationToken);
        _currentAggregation = TypeAggregation.Compute(_current, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null, true, new[] { "App.Cache.Entry", "App.Order", "App.Fresh", "App.Old" })]
    [InlineData(TypeDiffColumns.ObjectsDelta, true, new[] { "App.Order", "App.Cache.Entry", "App.Fresh", "App.Old" })]
    [InlineData(TypeDiffColumns.BytesDelta, false, new[] { "App.Old", "App.Fresh", "App.Order", "App.Cache.Entry" })]
    [InlineData(TypeDiffColumns.Name, false, new[] { "App.Cache.Entry", "App.Fresh", "App.Old", "App.Order" })]
    [InlineData(TypeDiffColumns.Change, true, new[] { "App.Fresh", "App.Old", "App.Cache.Entry", "App.Order" })]
    public void Rows_sort_by_any_column_by_name(string? column, bool descending, string[] expected)
    {
        var source = Create(new TypeDiffRequest(Guid.Empty, Guid.Empty, SortColumn: column, Descending: descending));

        Names(source).ShouldBe(expected);
    }

    [Fact]
    public void Rows_format_both_sides_signed_deltas_and_the_change()
    {
        var source = Create(new TypeDiffRequest(Guid.Empty, Guid.Empty, SortColumn: TypeDiffColumns.Name, Descending: false));

        source.Columns.Select(c => c.Name).ShouldBe(
            ["Name", "Change", "BaselineObjects", "CurrentObjects", "ObjectsDelta", "BaselineBytes", "CurrentBytes", "BytesDelta", "BaselineTypes", "CurrentTypes"]);
        source.Columns.Single(c => c.IsKey).Name.ShouldBe("Name");
        source.Columns.Where(c => c.Kind == ResultColumnKind.SignedInteger).Select(c => c.Name).ShouldBe(["ObjectsDelta", "BytesDelta"]);
        Row(source, 2).ShouldBe(["App.Old", "gone", "1", "0", "-1", "100", "0", "-100", "1", "0"]);
        Row(source, 1).ShouldBe(["App.Fresh", "new", "0", "1", "1", "0", "16", "16", "0", "1"]);
    }

    [Fact]
    public void Totals_count_new_and_gone_rows_before_the_filter_and_sum_the_matching_rows()
    {
        var source = Create(new TypeDiffRequest(Guid.Empty, Guid.Empty, Filter: "order"));

        Names(source).ShouldBe(["App.Order"]);
        source.UnfilteredCount.ShouldBe(4);
        source.NewRows.ShouldBe(1);
        source.GoneRows.ShouldBe(1);
        source.MatchingBaselineBytes.ShouldBe(80);
        source.MatchingBytesDelta.ShouldBe(120);

        var all = Create(new TypeDiffRequest(Guid.Empty, Guid.Empty));
        all.MatchingBytesDelta.ShouldBe(120 + 500 - 100 + 16);
    }

    [Fact]
    public void An_unknown_sort_column_or_grouping_is_refused_and_names_the_columns()
    {
        Should.Throw<ArgumentException>(() => Create(new TypeDiffRequest(Guid.Empty, Guid.Empty, SortColumn: "Size")))
            .Message.ShouldContain("BytesDelta");
        Should.Throw<ArgumentOutOfRangeException>(() => Create(new TypeDiffRequest(Guid.Empty, Guid.Empty, (TypeGrouping)9)));
    }

    public void Dispose()
    {
        _baseline.Dispose();
        _current.Dispose();
    }

    private static void Add(SyntheticGraphBuilder builder, string type, ulong size, int count)
    {
        for (var i = 0; i < count; i++)
        {
            builder.Object(type, size, module: "App.dll");
        }
    }

    private TypeDiffSource Create(TypeDiffRequest request) =>
        TypeDiffSource.Create(_baseline, _baselineAggregation, _current, _currentAggregation, request);

    private static string?[] Row(TypeDiffSource source, int index)
    {
        var cells = new string?[source.Columns.Count];
        source.FormatRow(index, cells);
        return cells;
    }

    private static List<string?> Names(TypeDiffSource source) => [.. Enumerable.Range(0, source.Count).Select(i => Row(source, i)[0])];
}
