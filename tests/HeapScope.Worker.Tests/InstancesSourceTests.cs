using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>A type's objects and an object's references from synthetic graphs (sprint 1.5).</summary>
public sealed class InstancesSourceTests : IDisposable
{
    private readonly HeapGraph _graph;

    public InstancesSourceTests()
    {
        var builder = new SyntheticGraphBuilder();
        var holder = builder.Object("App.Holder", 24, module: "App.dll", methodTable: 0x10);
        var a = builder.Object("Plugin.Cache", 100, HeapKind.Gen2, module: @"C:\app\Plugin.dll", methodTable: 0x20);
        var b = builder.Object("Plugin.Cache", 300, HeapKind.Gen0, module: @"C:\app\Plugin.dll", methodTable: 0x30);
        builder.Object("Plugin.Cache", 50, module: @"C:\fork\Fork.dll", methodTable: 0x40);
        builder.Reference(holder, b).Reference(holder, a).Reference(holder, b);
        _graph = builder.Build();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Instances_cover_every_loaded_copy_of_the_keyed_type_and_nothing_else()
    {
        var source = Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "Plugin.dll"));

        source.Count.ShouldBe(2);
        (source.TotalObjects, source.TotalBytes, source.MatchedTypes).ShouldBe((2L, 400L, 2));
        Rows(source).Select(r => r[2]).ShouldBe(["300", "100"], "largest first by default");
        Rows(source)[0][3].ShouldBe("Gen0");
        Rows(source)[0][4].ShouldBeNull("not a string or an array");
        Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "fork.DLL")).Count.ShouldBe(1, "module file names ignore case");
        Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", null)).Count.ShouldBe(0);
    }

    [Fact]
    public void An_address_prefix_filters_and_any_column_sorts()
    {
        var all = Rows(Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "Plugin.dll", SortColumn: InstanceColumns.Address, Descending: false)));
        all.Select(r => r[0]).ShouldBeInOrder(SortDirection.Ascending, StringComparer.Ordinal);

        var exact = Rows(Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "Plugin.dll", Filter: all[1][0])));
        exact.ShouldHaveSingleItem()[0].ShouldBe(all[1][0]);
        Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "Plugin.dll", Filter: "0xffffff")).Count.ShouldBe(0);
    }

    [Fact]
    public void Invalid_filters_and_sort_columns_are_refused_with_a_reason()
    {
        Should.Throw<ArgumentException>(() => Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "Plugin.dll", Filter: "/[unclosed/"))).Message.ShouldContain("regular expression");
        Should.Throw<ArgumentException>(() => Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "Plugin.dll", Filter: "0xZZ"))).Message.ShouldContain("hexadecimal");
        Should.Throw<ArgumentException>(() => Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "Plugin.dll", SortColumn: "Size"))).Message.ShouldContain("Generation");
    }

    [Fact]
    public void Value_filters_without_a_reader_match_nothing()
    {
        Create(new InstancesRequest(Guid.Empty, "Plugin.Cache", "Plugin.dll", Filter: "anything")).Count.ShouldBe(0);
    }

    [Fact]
    public void References_keep_the_graph_order_and_are_numbered_without_a_reader()
    {
        _graph.TryGetIndex(SyntheticGraphBuilder.BaseAddress, out var holder).ShouldBeTrue();

        var source = ReferencesSource.Create(_graph, null, holder, Ct);

        var rows = Enumerable.Range(0, source.Count).Select(i => Format(source, i)).ToList();
        rows.Count.ShouldBe(_graph.GetReferences(holder).Length);
        rows[0][0].ShouldBe("#1");
        rows.ShouldAllBe(r => r[2] == "Plugin.Cache");
        source.Columns.Single(c => c.IsKey).Name.ShouldBe(ReferenceColumns.Address);
    }

    [Fact]
    public void References_are_named_by_the_reader_and_every_edge_to_one_target_shows_its_names()
    {
        _graph.TryGetIndex(SyntheticGraphBuilder.BaseAddress, out var holder).ShouldBeTrue();
        var targets = _graph.GetReferences(holder).ToArray().Select(t => _graph.Addresses[t]).Distinct().ToList();
        var reader = new NamingReader(new Dictionary<ulong, string>
        {
            [targets[0]] = "Items[0], Items[2]",
            [targets[1]] = "Other",
        });

        var source = ReferencesSource.Create(_graph, reader, holder, Ct);

        var rows = Enumerable.Range(0, source.Count).Select(i => Format(source, i)).ToList();
        rows.Select(r => r[0]).ShouldBe(["Items[0], Items[2]", "Other", "Items[0], Items[2]"], "one row per edge, in the graph's order");
        reader.MaxElements.ShouldBe(ReferencesSource.MaxNamedElements);
    }

    public void Dispose() => _graph.Dispose();

    /// <summary>Answers reference paths from a table; nothing else is read.</summary>
    private sealed class NamingReader(IReadOnlyDictionary<ulong, string> paths) : IObjectReader
    {
        public int MaxElements { get; private set; }

        public IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, CancellationToken cancellationToken) => throw new NotSupportedException();

        public string? ReadString(ulong address, int maxLength) => throw new NotSupportedException();

        public IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken)
        {
            MaxElements = maxElements;
            return paths;
        }
    }

    private InstancesSource Create(InstancesRequest request) => InstancesSource.Create(_graph, null, request, Ct);

    private static List<string?[]> Rows(InstancesSource source) => [.. Enumerable.Range(0, source.Count).Select(i => Format(source, i))];

    private static string?[] Format(IResultSource source, int index)
    {
        var cells = new string?[source.Columns.Count];
        source.FormatRow(index, cells);
        return cells;
    }
}
