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

using System.Globalization;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>Sprint 2.1: the referrers of an object and of a type as result sets.</summary>
public sealed class ReferrersSourceTests : IDisposable
{
    private readonly HeapGraph _graph;
    private readonly ReverseReferences _reverse;
    private readonly SyntheticObject _item;
    private readonly SyntheticObject _cache;
    private readonly SyntheticObject _list;

    public ReferrersSourceTests()
    {
        var builder = new SyntheticGraphBuilder();
        _cache = builder.Object("App.Cache", 100);
        _list = builder.Object("App.List", 64);
        _item = builder.Object("App.Item", 32);
        var other = builder.Object("App.Item", 32);
        builder.Reference(_cache, _item).Reference(_list, _item).Reference(_list, _item).Reference(_list, other);
        _graph = builder.Build();
        _reverse = ReverseReferences.Build(_graph, cancellationToken: Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_objects_referrers_are_listed_once_with_their_fields()
    {
        var reader = new NamingReader(new Dictionary<ulong, string> { [_item.Address] = "_items[3]" });

        var source = ReferrersSource.Create(_graph, _reverse, reader, _item.Id, "no reader", null, Ct);

        source.Columns.Select(c => c.Name).ShouldBe([ReferrerColumns.Field, ReferrerColumns.Address, ReferrerColumns.Type, ReferrerColumns.Bytes, ReferrerColumns.References]);
        source.Count.ShouldBe(2);
        source.ReferenceCount.ShouldBe(3);
        source.FieldsNote.ShouldBeNull();
        reader.MaxElements.ShouldBe(ReferrersSource.MaxNamedElements);
        Format(source, 0).ShouldBe(["_items[3]", Hex(_cache.Address), "App.Cache", "100", "1"]);
        Format(source, 1).ShouldBe(["_items[3]", Hex(_list.Address), "App.List", "64", "2"]);
    }

    [Fact]
    public void Without_a_reader_the_fields_are_empty_and_explained()
    {
        var source = ReferrersSource.Create(_graph, _reverse, null, _item.Id, "Field values are not stored in a gcdump.", null, Ct);

        source.FieldsNote.ShouldBe("Field values are not stored in a gcdump.");
        Format(source, 0)[0].ShouldBe(string.Empty);
    }

    [Fact]
    public void An_object_nobody_references_has_no_rows_and_no_note()
    {
        var source = ReferrersSource.Create(_graph, _reverse, null, _cache.Id, "no reader", null, Ct);

        source.Count.ShouldBe(0);
        source.FieldsNote.ShouldBeNull();
    }

    [Fact]
    public void Fields_are_not_named_for_an_object_with_too_many_referrers()
    {
        var builder = new SyntheticGraphBuilder();
        var shared = builder.Object("System.String");
        for (var i = 0; i <= ReferrersSource.MaxNamedReferrers; i++)
        {
            builder.Reference(builder.Object("App.Holder"), shared);
        }

        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var reader = new NamingReader(new Dictionary<ulong, string>());

        var source = ReferrersSource.Create(graph, reverse, reader, shared.Id, "no reader", null, Ct);

        source.Count.ShouldBe(ReferrersSource.MaxNamedReferrers + 1);
        source.FieldsNote!.ShouldContain("up to 1,000 referrers");
        reader.MaxElements.ShouldBe(0, "no referrer was read");
    }

    [Fact]
    public void Naming_stops_when_the_querys_array_element_budget_is_spent()
    {
        var builder = new SyntheticGraphBuilder();
        var shared = builder.Object("System.String");
        for (var i = 0; i < 30; i++)
        {
            builder.Reference(builder.Array("App.Holder[]", ReferrersSource.MaxNamedElements, 8, references: true), shared);
        }

        using var graph = builder.Build();
        using var reverse = ReverseReferences.Build(graph, cancellationToken: Ct);
        var reader = new NamingReader(new Dictionary<ulong, string> { [shared.Address] = "[7]" });

        var source = ReferrersSource.Create(graph, reverse, reader, shared.Id, "no reader", null, Ct);

        var named = (int)(ReferrersSource.MaxNamedElementsPerQuery / ReferrersSource.MaxNamedElements);
        source.FieldsNote!.ShouldStartWith($"Fields are named for {named} of 30 referrers");
        Format(source, named - 1)[0].ShouldBe("[7]");
        Format(source, named)[0].ShouldBe(string.Empty);
    }

    [Fact]
    public void A_types_referrers_are_grouped_by_type_and_sorted()
    {
        var source = TypeReferrersSource.Create(_graph, _reverse, new TypeReferrersRequest(Guid.Empty, "App.Item", null), Ct);

        source.MatchedTypes.ShouldBe(1);
        source.TargetObjects.ShouldBe(2);
        source.ReferenceCount.ShouldBe(4);
        Rows(source).ShouldBe(
        [
            ["App.List", string.Empty, "1", "3", "2", "64"],
            ["App.Cache", string.Empty, "1", "1", "1", "100"],
        ]);

        var byBytes = TypeReferrersSource.Create(_graph, _reverse, new TypeReferrersRequest(Guid.Empty, "App.Item", null, TypeReferrerColumns.Bytes), Ct);
        Rows(byBytes)[0][0].ShouldBe("App.Cache");
    }

    [Fact]
    public void An_unknown_type_has_no_rows_and_an_unknown_sort_column_is_refused()
    {
        var none = TypeReferrersSource.Create(_graph, _reverse, new TypeReferrersRequest(Guid.Empty, "App.None", null), Ct);

        none.MatchedTypes.ShouldBe(0);
        none.Count.ShouldBe(0);
        Should.Throw<ArgumentException>(() => TypeReferrersSource.Create(_graph, _reverse, new TypeReferrersRequest(Guid.Empty, "App.Item", null, "Nope"), Ct))
            .Message.ShouldContain("Sort by one of");
    }

    public void Dispose()
    {
        _reverse.Dispose();
        _graph.Dispose();
    }

    private static string Hex(ulong address) => string.Create(CultureInfo.InvariantCulture, $"0x{address:x}");

    private static List<string?[]> Rows(IResultSource source) => [.. Enumerable.Range(0, source.Count).Select(i => Format(source, i))];

    private static string?[] Format(IResultSource source, int index)
    {
        var cells = new string?[source.Columns.Count];
        source.FormatRow(index, cells);
        return cells;
    }

    /// <summary>Answers reference paths from a table; nothing else is read.</summary>
    private sealed class NamingReader(IReadOnlyDictionary<ulong, string> paths) : IObjectReader
    {
        public int MaxElements { get; private set; }

        public IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, int maxText, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ObjectValue? ReadField(ulong address, string path, int maxText = 1000) => throw new NotSupportedException();

        public string? ReadString(ulong address, int maxLength) => throw new NotSupportedException();

        public void ReadStrings(IReadOnlyList<ulong> addresses, int maxLength, Action<int, string?> onEach, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ReadStringPieces(IReadOnlyList<ulong> addresses, StringPieceHandler onPiece, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken)
        {
            MaxElements = maxElements;
            return paths;
        }
    }
}
