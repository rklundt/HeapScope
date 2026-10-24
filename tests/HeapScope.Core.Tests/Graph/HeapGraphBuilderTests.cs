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

namespace HeapScope.Core.Tests.Graph;

public class HeapGraphBuilderTests
{
    [Fact]
    public void Empty_graph_has_no_objects_and_one_edge_start()
    {
        using var graph = new HeapGraphBuilder().Build(TestContext.Current.CancellationToken);

        graph.ObjectCount.ShouldBe(0);
        graph.EdgeCount.ShouldBe(0);
        graph.EdgeStarts.Length.ShouldBe(1);
        graph.TotalSize.ShouldBe(0UL);
        graph.Roots.Count.ShouldBe(0);
    }

    [Fact]
    public void Single_object_keeps_its_type_size_and_heap_kind()
    {
        var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("App.Thing", methodTable: 0x7ff0, baseSize: 24);
        builder.AddObject(0x1000, type, 24, HeapKind.Gen1, ObjectFlags.HasFinalizer);

        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.ObjectCount.ShouldBe(1);
        graph.Addresses[0].ShouldBe(0x1000UL);
        graph.Types.GetName(graph.TypeIndices[0]).ShouldBe("App.Thing");
        graph.GetSize(0).ShouldBe(24UL);
        graph.HeapKinds[0].ShouldBe(HeapKind.Gen1);
        graph.Flags[0].ShouldBe(ObjectFlags.HasFinalizer);
        graph.GetReferences(0).Length.ShouldBe(0);
    }

    [Fact]
    public void Self_reference_is_an_edge_to_itself()
    {
        var synthetic = new SyntheticGraphBuilder();
        var a = synthetic.Object("A");
        synthetic.Reference(a, a);

        using var graph = synthetic.Build();

        graph.GetReferences(0).ToArray().ShouldBe([0]);
    }

    [Fact]
    public void Unsorted_input_is_sorted_by_address_with_all_columns_permuted()
    {
        var builder = new HeapGraphBuilder();
        var big = builder.Types.GetOrAdd("Big");
        var small = builder.Types.GetOrAdd("Small");
        builder.AddObject(0x3000, big, 300, HeapKind.LargeObjectHeap);
        builder.AddObject(0x1000, small, 100, HeapKind.Gen0);
        builder.AddObject(0x2000, small, 200, HeapKind.Gen2, ObjectFlags.HasFinalizer);

        builder.FreezeObjects(TestContext.Current.CancellationToken);
        builder.AddReferences(builder.IndexOf(0x3000), [0x1000UL, 0x2000UL]);
        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.Addresses.Span.ToArray().ShouldBe([0x1000UL, 0x2000UL, 0x3000UL]);
        graph.Sizes.Span.ToArray().ShouldBe([100u, 200u, 300u]);
        graph.TypeIndices.Span.ToArray().ShouldBe([small, small, big]);
        graph.HeapKinds.Span.ToArray().ShouldBe([HeapKind.Gen0, HeapKind.Gen2, HeapKind.LargeObjectHeap]);
        graph.Flags[1].ShouldBe(ObjectFlags.HasFinalizer);
        graph.GetReferences(2).ToArray().ShouldBe([0, 1]);
    }

    [Fact]
    public void Duplicate_addresses_are_rejected_with_a_clear_message()
    {
        var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("T");
        builder.AddObject(0x2000, type, 24, HeapKind.Gen0);
        builder.AddObject(0x1000, type, 24, HeapKind.Gen0);
        builder.AddObject(0x2000, type, 24, HeapKind.Gen0);

        var ex = Should.Throw<InvalidOperationException>(() => builder.FreezeObjects(TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("Duplicate object address 0x2000");
    }

    [Fact]
    public void Repeated_address_in_order_is_also_rejected()
    {
        var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("T");
        builder.AddObject(0x1000, type, 24, HeapKind.Gen0);
        builder.AddObject(0x1000, type, 24, HeapKind.Gen0);

        Should.Throw<InvalidOperationException>(() => builder.FreezeObjects(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void References_to_non_objects_are_dropped_and_counted_and_null_is_ignored()
    {
        var synthetic = new SyntheticGraphBuilder();
        var a = synthetic.Object("A");
        var b = synthetic.Object("B");
        synthetic.Reference(a, b).DanglingReference(a, 0xDEAD_0000).DanglingReference(a, 0);

        using var graph = synthetic.Build();

        graph.GetReferences(a.Id).ToArray().ShouldBe([b.Id]);
        graph.DroppedReferenceCount.ShouldBe(1);
    }

    [Fact]
    public void References_must_be_added_in_strictly_increasing_source_order()
    {
        var builder = BuilderWithObjects(3);
        builder.AddReferences(2, [0x1000UL]);

        Should.Throw<InvalidOperationException>(() => builder.AddReferences(1, [0x1000UL]))
            .Message.ShouldContain("strictly increasing");
        Should.Throw<InvalidOperationException>(() => builder.AddReferences(2, [0x1000UL]));
    }

    [Fact]
    public void Objects_without_references_get_empty_rows()
    {
        var builder = BuilderWithObjects(4);
        builder.AddReferences(2, [0x1000UL, 0x1018UL]);

        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.GetReferences(0).Length.ShouldBe(0);
        graph.GetReferences(1).Length.ShouldBe(0);
        graph.GetReferences(2).ToArray().ShouldBe([0, 1]);
        graph.GetReferences(3).Length.ShouldBe(0);
        graph.EdgeCount.ShouldBe(2);
    }

    [Fact]
    public void Objects_of_4_GiB_or_more_round_trip_their_size()
    {
        const ulong FiveGiB = 5UL * 1024 * 1024 * 1024;
        var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("System.Byte[]", kind: TypeKind.Array);
        builder.AddObject(0x9000, type, FiveGiB, HeapKind.LargeObjectHeap);
        builder.AddObject(0x1000, type, 100, HeapKind.Gen0);
        builder.AddObject(0x5000, type, uint.MaxValue, HeapKind.LargeObjectHeap);

        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.GetSize(0).ShouldBe(100UL);
        graph.GetSize(1).ShouldBe((ulong)uint.MaxValue);
        graph.GetSize(2).ShouldBe(FiveGiB);
        graph.Sizes[2].ShouldBe(HeapGraph.LargeSizeMarker);
        graph.TotalSize.ShouldBe(100UL + uint.MaxValue + FiveGiB);
        graph.LargeSizes.Length.ShouldBe(2);
    }

    [Fact]
    public void Interior_roots_resolve_by_floor_search_within_the_object()
    {
        var synthetic = new SyntheticGraphBuilder();
        var a = synthetic.Object("A", size: 64);
        var b = synthetic.Object("B", size: 32);
        synthetic.RootAddress(a.Address + 40, RootKind.ThreadStack, RootFlags.Retaining | RootFlags.Interior, "thread 1");
        synthetic.RootAddress(b.Address + 8, RootKind.ThreadStack, RootFlags.Retaining, "thread 1");
        synthetic.RootAddress(b.Address + 32, RootKind.ThreadStack, RootFlags.Retaining | RootFlags.Interior);

        using var graph = synthetic.Build();

        graph.Roots.Count.ShouldBe(1);
        graph.Roots.GetRoots(a.Id)[0].Kind.ShouldBe(RootKind.ThreadStack);
        graph.DroppedRootCount.ShouldBe(2);
    }

    [Fact]
    public void Roots_that_resolve_to_nothing_are_dropped_and_counted()
    {
        var synthetic = new SyntheticGraphBuilder();
        synthetic.Object("A");
        synthetic.RootAddress(0x10, RootKind.StrongHandle, RootFlags.Retaining);
        synthetic.RootAddress(0xFFFF_FFFF, RootKind.PinnedHandle, RootFlags.Retaining | RootFlags.Pinned);

        using var graph = synthetic.Build();

        graph.Roots.Count.ShouldBe(0);
        graph.DroppedRootCount.ShouldBe(2);
    }

    [Fact]
    public void Roots_of_every_kind_are_kept_and_only_retaining_ones_retain()
    {
        var synthetic = new SyntheticGraphBuilder();
        var kinds = Enum.GetValues<RootKind>();
        var objects = kinds.Select(k => synthetic.Object("T" + k)).ToArray();
        for (var i = 0; i < kinds.Length; i++)
        {
            var flags = kinds[i] == RootKind.WeakHandle ? RootFlags.None : RootFlags.Retaining;
            synthetic.Root(objects[i], kinds[i], $"detail {kinds[i]}", flags);
        }

        using var graph = synthetic.Build();

        graph.Roots.Count.ShouldBe(kinds.Length);
        for (var i = 0; i < kinds.Length; i++)
        {
            var root = graph.Roots.GetRoots(objects[i].Id).ToArray().ShouldHaveSingleItem();
            root.Kind.ShouldBe(kinds[i]);
            graph.Strings[root.DetailId].ShouldBe($"detail {kinds[i]}");
            graph.Roots.IsRetained(objects[i].Id).ShouldBe(kinds[i] != RootKind.WeakHandle);
        }
    }

    [Fact]
    public void Ref_counted_handle_retains_only_when_flagged_strong()
    {
        var synthetic = new SyntheticGraphBuilder();
        var strong = synthetic.Object("Strong");
        var weak = synthetic.Object("ZeroCount");
        synthetic.Root(strong, RootKind.RefCountedHandle, flags: RootFlags.Retaining);
        synthetic.Root(weak, RootKind.RefCountedHandle, flags: RootFlags.None);

        using var graph = synthetic.Build();

        graph.Roots.IsRetained(strong.Id).ShouldBeTrue();
        graph.Roots.IsRetained(weak.Id).ShouldBeFalse();
    }

    [Fact]
    public void Roots_are_sorted_by_object_then_kind()
    {
        var synthetic = new SyntheticGraphBuilder();
        var a = synthetic.Object("A");
        var b = synthetic.Object("B");
        synthetic.Root(b, RootKind.StrongHandle).Root(a, RootKind.ThreadStack).Root(a, RootKind.StaticField);

        using var graph = synthetic.Build();

        var roots = graph.Roots.Roots.Span.ToArray();
        roots.Select(r => (r.ObjectIndex, r.Kind)).ShouldBe(
            [(a.Id, RootKind.StaticField), (a.Id, RootKind.ThreadStack), (b.Id, RootKind.StrongHandle)]);
        graph.Roots.GetRoots(a.Id).Length.ShouldBe(2);
    }

    [Fact]
    public void Dependent_handle_is_an_edge_from_primary_to_secondary_and_never_a_root()
    {
        var synthetic = new SyntheticGraphBuilder();
        var key = synthetic.Object("Key");
        var holder = synthetic.Object("Holder");
        var value = synthetic.Object("Value");
        var other = synthetic.Object("Other");
        synthetic.Reference(key, other);
        synthetic.DependentHandle(key, value);
        synthetic.DependentHandle(holder, value);

        using var graph = synthetic.Build();

        graph.GetReferences(key.Id).ToArray().ShouldBe([other.Id, value.Id]);
        graph.GetReferences(holder.Id).ToArray().ShouldBe([value.Id]);
        graph.Roots.Count.ShouldBe(0);
    }

    [Fact]
    public void Dependent_handles_after_the_reference_pass_started_are_rejected()
    {
        var builder = BuilderWithObjects(2);
        builder.AddReferences(0, [0x1018UL]);

        Should.Throw<InvalidOperationException>(() => builder.AddDependentHandle(0x1000, 0x1018));
    }

    [Fact]
    public void Dependent_handle_with_unknown_primary_is_dropped()
    {
        var builder = BuilderWithObjects(2);
        builder.AddDependentHandle(0x5555, 0x1000);
        builder.AddDependentHandle(0x1000, 0);

        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.EdgeCount.ShouldBe(0);
        graph.DroppedReferenceCount.ShouldBe(1);
    }

    [Fact]
    public void Object_operations_after_freeze_are_rejected()
    {
        var builder = BuilderWithObjects(1);

        Should.Throw<InvalidOperationException>(() => builder.AddObject(0x9000, 0, 24, HeapKind.Gen0));
    }

    [Fact]
    public void Invalid_object_arguments_are_rejected()
    {
        var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("T");

        Should.Throw<ArgumentOutOfRangeException>(() => builder.AddObject(0, type, 24, HeapKind.Gen0));
        Should.Throw<ArgumentOutOfRangeException>(() => builder.AddObject(0x1000, type + 1, 24, HeapKind.Gen0));
    }

    [Fact]
    public void Address_lookups_find_exact_and_containing_objects()
    {
        var synthetic = new SyntheticGraphBuilder();
        var a = synthetic.Object("A", size: 48);
        var b = synthetic.Object("B", size: 16);

        using var graph = synthetic.Build();

        graph.TryGetIndex(b.Address, out var exact).ShouldBeTrue();
        exact.ShouldBe(b.Id);
        graph.TryGetIndex(a.Address + 8, out _).ShouldBeFalse();
        graph.TryGetIndexContaining(a.Address + 47, out var containing).ShouldBeTrue();
        containing.ShouldBe(a.Id);
        graph.TryGetIndexContaining(b.Address + 16, out _).ShouldBeFalse();
        graph.TryGetIndexContaining(1, out _).ShouldBeFalse();
    }

    [Fact]
    public void Graph_owner_from_the_allocator_is_disposed_with_the_graph()
    {
        var allocator = new OwningAllocator();
        var builder = new HeapGraphBuilder(allocator);
        builder.AddObject(0x1000, builder.Types.GetOrAdd("T"), 24, HeapKind.Gen0);
        var graph = builder.Build(TestContext.Current.CancellationToken);

        allocator.Owner.Disposed.ShouldBeFalse();
        graph.Dispose();
        graph.Dispose();

        allocator.Owner.Disposed.ShouldBeTrue();
        allocator.AllocatedColumns.ShouldContain("Addresses");
        allocator.AllocatedColumns.ShouldContain("Edges");
    }

    [Fact]
    public void Column_bytes_follow_the_per_object_budget()
    {
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(ObjectCount: 10_000, AverageReferences: 3, Seed: 7), cancellationToken: TestContext.Current.CancellationToken);

        var perObject = (double)graph.ColumnBytes / graph.ObjectCount;
        perObject.ShouldBeInRange(30, 38);
    }

    private static HeapGraphBuilder BuilderWithObjects(int count)
    {
        var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("T");
        for (var i = 0; i < count; i++)
        {
            builder.AddObject(0x1000 + ((ulong)i * 0x18), type, 24, HeapKind.Gen0);
        }

        builder.FreezeObjects(TestContext.Current.CancellationToken);
        return builder;
    }

    private sealed class OwningAllocator : IColumnAllocator
    {
        public FlagDisposable Owner { get; } = new();

        public List<string> AllocatedColumns { get; } = [];

        public Memory<T> Allocate<T>(string columnName, int length)
            where T : unmanaged
        {
            AllocatedColumns.Add(columnName);
            return new T[length];
        }

        public IDisposable? DetachOwner() => Owner;
    }

    private sealed class FlagDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
