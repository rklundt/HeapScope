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

using HeapScope.Analysis.Inspections;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Analysis.Tests;

/// <summary>The simple inspections (sprint 1.6) on synthetic graphs whose answers are known by construction.</summary>
public sealed class InspectionTests
{
    private static readonly SnapshotInfo s_info = new(SnapshotKind.FullDump, "synthetic.dmp", FreeBytes: [0, 100, 200, 3_000, 400, 0, 0]);

    [Fact]
    public void Every_inspection_has_a_unique_name_a_cause_a_fix_and_requirements()
    {
        InspectionCatalog.All.Select(i => i.Name).ShouldBeUnique();
        InspectionCatalog.All.ShouldAllBe(i => i.Title.Length > 0 && i.Cause.Length > 0 && i.Fix.Length > 0 && i.Requires != SnapshotCapabilities.None);
        InspectionCatalog.Find("DUPLICATE-STRINGS").ShouldNotBeNull().Requires.ShouldBe(SnapshotCapabilities.StringContents);
        InspectionCatalog.Find("empty-collections")!.Requires.ShouldBe(SnapshotCapabilities.FieldValues);
        InspectionCatalog.Find("nothing").ShouldBeNull();
    }

    [Fact]
    public void Large_object_heap_contents_are_totalled_per_type_key_and_open_in_the_loh_scope()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Object("App.Buffer", 100_000, HeapKind.LargeObjectHeap, module: @"C:\app\App.dll");
        builder.Object("App.Buffer", 200_000, HeapKind.LargeObjectHeap, module: @"C:\app\App.dll");
        builder.Object("App.Buffer", 100, HeapKind.Gen0, module: @"C:\app\App.dll");
        builder.Object("App.Table", 90_000, HeapKind.LargeObjectHeap, module: @"C:\app\App.dll");
        using var graph = builder.Build();

        var result = Run("loh", graph);

        (result.Count, result.Bytes, result.WastedBytes).ShouldBe((3L, 390_000L, (long?)null));
        Rows(result).ShouldBe(
        [
            ["App.Buffer", "2", "300000", "App.Buffer", "App.dll", InspectionScopes.LargeObjectHeap],
            ["App.Table", "1", "90000", "App.Table", "App.dll", InspectionScopes.LargeObjectHeap],
        ]);
    }

    [Fact]
    public void Large_arrays_start_at_the_loh_threshold_largest_first()
    {
        var builder = new SyntheticGraphBuilder();
        var small = builder.Array("System.Byte[]", 84_975, 1);
        var exact = builder.Array("System.Byte[]", 84_976, 1);
        var big = builder.Array("System.Int64[]", 20_000, 8);
        using var graph = builder.Build();

        var result = Run("large-arrays", graph);

        graph.GetSize(small.Id).ShouldBe(84_999UL);
        result.Count.ShouldBe(2);
        Rows(result).Select(r => (r[0], r[2])).ShouldBe([(Hex(big.Address), "20000"), (Hex(exact.Address), "84976")]);
    }

    [Fact]
    public void Sparse_arrays_are_reference_arrays_of_16_or_more_slots_at_least_half_null()
    {
        var builder = new SyntheticGraphBuilder();
        var target = builder.Object("App.Node", 24);
        var sparse = builder.Array("App.Node[]", 100, 8, references: true);
        var half = builder.Array("App.Node[]", 16, 8, references: true);
        var dense = builder.Array("App.Node[]", 100, 8, references: true);
        var shortArray = builder.Array("App.Node[]", 15, 8, references: true);
        builder.Array("System.Int64[]", 100, 8);
        for (var i = 0; i < 10; i++)
        {
            builder.Reference(sparse, target);
        }

        for (var i = 0; i < 8; i++)
        {
            builder.Reference(half, target);
        }

        for (var i = 0; i < 60; i++)
        {
            builder.Reference(dense, target);
        }

        using var graph = builder.Build();

        var result = Run("sparse-arrays", graph);

        result.Count.ShouldBe(2, "a long[] has no references but no null slots either; a 15-slot array is too short");
        result.WastedBytes.ShouldBe((90 + 8) * 8L);
        Rows(result).Select(r => (r[0], r[2], r[3], r[4])).ShouldBe([(Hex(sparse.Address), "100", "90", "720"), (Hex(half.Address), "16", "8", "64")]);
        Rows(result).ShouldNotContain(r => r[0] == Hex(shortArray.Address));
    }

    [Fact]
    public void Finalizable_objects_are_those_registered_and_those_ready_for_the_finalizer_by_type()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Object("App.Handle", 32, flags: ObjectFlags.HasFinalizer);
        builder.Object("App.Handle", 32, flags: ObjectFlags.HasFinalizer);
        builder.Object("App.Handle", 32);
        var dead = builder.Object("App.Socket", 48);
        builder.Root(dead, RootKind.FinalizerQueue, flags: RootFlags.None);
        using var graph = builder.Build();

        var result = Run("finalizable", graph);

        (result.Count, result.Bytes).ShouldBe((3L, 112L));
        Rows(result).ShouldBe(
        [
            ["App.Socket", "1", "1", "48", "App.Socket", string.Empty, InspectionScopes.Finalizable],
            ["App.Handle", "2", "0", "64", "App.Handle", string.Empty, InspectionScopes.Finalizable],
        ], "objects waiting for the finalizer thread come first");
    }

    [Fact]
    public void Fragmentation_shows_free_space_next_to_live_bytes_and_counts_each_pinned_object_once()
    {
        var builder = new SyntheticGraphBuilder();
        var pinned = builder.Object("System.Byte[]", 1_000, HeapKind.Gen2);
        builder.Object("System.Byte[]", 7_000, HeapKind.Gen2);
        builder.Object("System.Byte[]", 500, HeapKind.PinnedObjectHeap);
        builder.Root(pinned, RootKind.PinnedHandle).Root(pinned, RootKind.AsyncPinnedHandle);
        using var graph = builder.Build();

        var result = Run("fragmentation", graph);

        (result.Count, result.Bytes, result.WastedBytes, result.Note).ShouldBe((2L, 1_500L, (long?)3_700, null));
        var gen2 = Rows(result).Single(r => r[0] == "Gen2");
        gen2.ShouldBe(["Gen2", "8000", "3000", "27.27", "1", "1000"]);

        var unknown = Run("fragmentation", graph, s_info with { FreeBytes = null });
        unknown.WastedBytes.ShouldBeNull();
        unknown.Note.ShouldNotBeNull().ShouldContain("Rebuild");
        Rows(unknown).Single(r => r[0] == "Gen2")[2].ShouldBeNull();
    }

    [Fact]
    public void Empty_and_oversized_collections_come_from_their_count_and_backing_array()
    {
        const string ListType = "System.Collections.Generic.List<App.Item>";
        const string DictionaryType = "System.Collections.Generic.Dictionary<System.Int32, App.Item>";
        var builder = new SyntheticGraphBuilder();
        var empty = builder.Object(ListType, 32);
        var emptyItems = builder.Array("App.Item[]", 100, 8, references: true);
        var oversized = builder.Object(ListType, 32);
        var oversizedItems = builder.Array("App.Item[]", 1_000, 8, references: true);
        var right = builder.Object(ListType, 32);
        var rightItems = builder.Array("App.Item[]", 4, 8, references: true);
        var dictionary = builder.Object(DictionaryType, 80);
        var entries = builder.Array("Entry[]", 1_000, 24);
        var buckets = builder.Array("System.Int32[]", 1_000, 4);
        builder.Object("System.Collections.Generic.SortedList<App.Item>", 48);
        var drained = builder.Object(DictionaryType, 80);
        var drainedEntries = builder.Array("Entry[]", 100, 24);
        var queue = builder.Object("System.Collections.Generic.Queue<App.Item>", 40);
        var queueItems = builder.Array("App.Item[]", 32, 8, references: true);
        builder.Object("System.Collections.Generic.List<App.Item>[]", 24 + 8, typeKind: TypeKind.Array | TypeKind.ReferenceArray, baseSize: 24, componentSize: 8);
        using var graph = builder.Build();
        var reader = new FakeReader();
        reader.Collection(empty, "_size", 0, "_items", emptyItems, 100);
        reader.Collection(oversized, "_size", 10, "_items", oversizedItems, 1_000);
        reader.Collection(right, "_size", 4, "_items", rightItems, 4);
        reader.Collection(dictionary, "_count", 12, "_entries", entries, 1_000);
        reader.Fields[(dictionary.Address, "_freeCount")] = Integer(2);
        reader.Fields[(dictionary.Address, "_buckets")] = ArrayValue(buckets, 1_000);
        reader.Collection(drained, "_count", 50, "_entries", drainedEntries, 100);
        reader.Fields[(drained.Address, "_freeCount")] = Integer(50);
        reader.Collection(queue, "_size", 0, "_array", queueItems, 32);

        var emptyResult = Run("empty-collections", graph, reader: reader);
        var oversizedResult = Run("oversized-collections", graph, reader: reader);

        Rows(emptyResult).Select(r => r[0]).ShouldBe([Hex(drained.Address), Hex(empty.Address), Hex(queue.Address)], "most wasted first; emptied by removals counts as empty; arrays of collections are not collections");
        Rows(emptyResult)[1].ShouldBe([Hex(empty.Address), ListType, "0", "100", "824", "856"]);
        Rows(oversizedResult).Select(r => (r[0], r[2], r[3], r[4])).ShouldBe(
        [
            (Hex(dictionary.Address), "10", "1000", ((1_000 - 10) * 24).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (Hex(oversized.Address), "10", "1000", ((1_000 - 10) * 8).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);
        oversizedResult.WastedBytes.ShouldBe((990 * 24) + (990 * 8));
    }

    [Fact]
    public void Duplicate_strings_group_equal_text_and_waste_every_copy_after_the_first()
    {
        var builder = new SyntheticGraphBuilder();
        var texts = new Dictionary<ulong, string>();
        SyntheticObject String(string text)
        {
            var obj = builder.Object("System.String", 22 + (2 * (ulong)text.Length), module: "System.Private.CoreLib.dll", typeKind: TypeKind.String);
            texts[obj.Address] = text;
            return obj;
        }

        var first = String("alpha");
        String("alpha");
        String("alpha");
        String("bravo");
        String("bravo");
        String("delta");
        String(new string('x', 150));
        String(new string('x', 150));
        using var graph = builder.Build();

        var result = Run("duplicate-strings", graph, reader: new FakeReader { Texts = texts });

        result.Count.ShouldBe(3);
        var rows = Rows(result);
        rows.Select(r => (r[0], r[1])).ShouldBe([(new string('x', 100) + "...", "2"), ("alpha", "3"), ("bravo", "2")]);
        rows.Single(r => r[0] == "alpha").ShouldBe(["alpha", "3", "32", "64", Hex(first.Address), "System.String", "System.Private.CoreLib.dll", InspectionScopes.SameValue]);
        result.WastedBytes.ShouldBe(322 + (2 * 32) + 32);
    }

    [Fact]
    public void Cancellation_stops_an_inspection()
    {
        var builder = new SyntheticGraphBuilder();
        builder.Array("System.Byte[]", 100_000, 1);
        using var graph = builder.Build();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Should.Throw<OperationCanceledException>(() => InspectionCatalog.Find("large-arrays")!.Run(new InspectionContext(graph, s_info, null, null, cancel.Token)));
    }

    private static InspectionResult Run(string name, HeapGraph graph, SnapshotInfo? info = null, IObjectReader? reader = null) =>
        InspectionCatalog.Find(name)!.Run(new InspectionContext(graph, info ?? s_info, reader, null, TestContext.Current.CancellationToken));

    private static List<string?[]> Rows(InspectionResult result) =>
        [.. Enumerable.Range(0, result.Rows.Count).Select(i =>
        {
            var cells = new string?[result.Rows.Columns.Count];
            result.Rows.FormatRow(i, cells);
            return cells;
        })];

    private static string Hex(ulong address) => $"0x{address:x}";

    private static ObjectValue Integer(long value) => new(ObjectValueKind.Integer) { Integer = value };

    private static ObjectValue ArrayValue(SyntheticObject array, int length) => new(ObjectValueKind.Array) { Address = array.Address, Dimensions = [length] };

    /// <summary>Answers fields from a table and strings from a dictionary; nothing else is read.</summary>
    private sealed class FakeReader : IObjectReader
    {
        public Dictionary<(ulong Address, string Path), ObjectValue> Fields { get; } = [];

        public Dictionary<ulong, string> Texts { get; init; } = [];

        public void Collection(SyntheticObject collection, string countField, long count, string itemsField, SyntheticObject items, int capacity)
        {
            Fields[(collection.Address, countField)] = Integer(count);
            Fields[(collection.Address, itemsField)] = ArrayValue(items, capacity);
        }

        public IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, int maxText, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ObjectValue? ReadField(ulong address, string path, int maxText = 1000) => Fields.GetValueOrDefault((address, path));

        public string? ReadString(ulong address, int maxLength) => Texts.GetValueOrDefault(address);

        public void ReadStrings(IReadOnlyList<ulong> addresses, int maxLength, Action<int, string?> onEach, CancellationToken cancellationToken)
        {
            for (var i = 0; i < addresses.Count; i++)
            {
                onEach(i, Texts.TryGetValue(addresses[i], out var text) && text.Length > maxLength ? text[..maxLength] : Texts.GetValueOrDefault(addresses[i]));
            }
        }

        public IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ReadStringPieces(IReadOnlyList<ulong> addresses, StringPieceHandler onPiece, CancellationToken cancellationToken)
        {
            // Small pieces, so the streaming path is exercised across piece boundaries.
            for (var i = 0; i < addresses.Count; i++)
            {
                if (!Texts.TryGetValue(addresses[i], out var text))
                {
                    onPiece(i, -1, [], true);
                    continue;
                }

                if (text.Length == 0)
                {
                    onPiece(i, 0, [], true);
                    continue;
                }

                for (var offset = 0; offset < text.Length; offset += 7)
                {
                    var count = Math.Min(7, text.Length - offset);
                    onPiece(i, text.Length, text.AsSpan(offset, count), offset + count >= text.Length);
                }
            }
        }
    }
}
