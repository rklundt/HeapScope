using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Core.Tests.Graph;

/// <summary>Loader-facing behavior of the builder: the reference pass, dependent-handle merging, faults, disposal.</summary>
public class HeapGraphBuilderRobustnessTests
{
    [Fact]
    public void Reference_pass_can_walk_frozen_addresses_after_unsorted_input()
    {
        using var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("Node");
        ulong[] addresses = [0x5000, 0x1000, 0x3000, 0x2000, 0x4000];
        foreach (var address in addresses)
        {
            builder.AddObject(address, type, 24, HeapKind.Gen0);
        }

        builder.FreezeObjects(TestContext.Current.CancellationToken);

        // Like a loader: walk objects in index order, each referencing the next higher address.
        var frozen = builder.FrozenAddresses.ToArray();
        frozen.ShouldBe([0x1000UL, 0x2000UL, 0x3000UL, 0x4000UL, 0x5000UL]);
        for (var i = 0; i + 1 < frozen.Length; i++)
        {
            builder.AddReferences(i, [frozen[i + 1]]);
        }

        using var graph = builder.Build(TestContext.Current.CancellationToken);
        for (var i = 0; i + 1 < graph.ObjectCount; i++)
        {
            graph.GetReferences(i).ToArray().ShouldBe([i + 1]);
        }
    }

    [Fact]
    public void Frozen_addresses_are_not_available_before_freeze()
    {
        using var builder = new HeapGraphBuilder();

        Should.Throw<InvalidOperationException>(() => builder.FrozenAddresses.Length);
    }

    [Fact]
    public void Dependent_edges_merge_into_skipped_trailing_and_out_of_order_rows()
    {
        using var builder = Objects(6);
        builder.AddDependentHandle(Address(3), Address(5));
        builder.AddDependentHandle(Address(0), Address(4));
        builder.AddDependentHandle(Address(3), Address(1));
        builder.AddDependentHandle(Address(5), Address(2));

        builder.AddReferences(1, [Address(2)]);
        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.GetReferences(0).ToArray().ShouldBe([4]);
        graph.GetReferences(1).ToArray().ShouldBe([2]);
        graph.GetReferences(3).ToArray().ShouldBe([1, 5]);
        graph.GetReferences(5).ToArray().ShouldBe([2]);
        graph.EdgeCount.ShouldBe(5);
    }

    [Fact]
    public void Dependent_edges_are_built_without_any_reference_call()
    {
        using var builder = Objects(3);
        builder.AddDependentHandle(Address(2), Address(0));

        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.GetReferences(2).ToArray().ShouldBe([0]);
    }

    [Fact]
    public void Dependent_handle_with_unknown_secondary_is_dropped_and_counted()
    {
        using var builder = Objects(2);
        builder.AddDependentHandle(Address(0), 0xDEAD);

        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.EdgeCount.ShouldBe(0);
        graph.DroppedReferenceCount.ShouldBe(1);
    }

    [Fact]
    public void Weak_handles_never_retain_even_when_flagged()
    {
        using var builder = Objects(2);
        builder.AddRoot(Address(0), RootKind.WeakHandle, RootFlags.Retaining);
        builder.AddRootIndex(1, RootKind.WeakHandle, RootFlags.Retaining);

        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.Roots.Count.ShouldBe(2);
        graph.Roots.IsRetained(0).ShouldBeFalse();
        graph.Roots.IsRetained(1).ShouldBeFalse();
    }

    [Fact]
    public void Null_root_addresses_are_ignored_not_dropped()
    {
        using var builder = Objects(1);
        builder.AddRoot(0, RootKind.WeakHandle, RootFlags.None);

        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.Roots.Count.ShouldBe(0);
        graph.DroppedRootCount.ShouldBe(0);
    }

    [Fact]
    public void Bad_target_index_leaves_no_partial_row_and_the_row_can_be_retried()
    {
        using var builder = Objects(3);
        builder.AddReferenceIndices(0, [1]);

        Should.Throw<ArgumentOutOfRangeException>(() => builder.AddReferenceIndices(1, [2, 99]));
        builder.AddReferenceIndices(1, [2]);
        using var graph = builder.Build(TestContext.Current.CancellationToken);

        graph.GetReferences(0).ToArray().ShouldBe([1]);
        graph.GetReferences(1).ToArray().ShouldBe([2]);
    }

    [Fact]
    public void A_failed_freeze_faults_the_builder()
    {
        using var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("T");
        builder.AddObject(0x2000, type, 24, HeapKind.Gen0);
        builder.AddObject(0x2000, type, 24, HeapKind.Gen0);
        Should.Throw<InvalidOperationException>(() => builder.FreezeObjects(TestContext.Current.CancellationToken));

        Should.Throw<InvalidOperationException>(() => builder.Build(TestContext.Current.CancellationToken))
            .Message.ShouldContain("failed earlier");
        Should.Throw<InvalidOperationException>(() => builder.AddObject(0x3000, type, 24, HeapKind.Gen0))
            .Message.ShouldContain("failed earlier");
    }

    [Fact]
    public void Disposing_an_unbuilt_builder_releases_allocator_memory()
    {
        var allocator = new OwningAllocator();
        var builder = new HeapGraphBuilder(allocator);
        builder.AddObject(0x1000, builder.Types.GetOrAdd("T"), 24, HeapKind.Gen0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => builder.FreezeObjects(cts.Token));
        builder.Dispose();
        builder.Dispose();

        allocator.Owner.Disposed.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => builder.Build(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Disposing_a_built_builder_leaves_the_graph_owner_alone()
    {
        var allocator = new OwningAllocator();
        var builder = new HeapGraphBuilder(allocator);
        builder.AddObject(0x1000, builder.Types.GetOrAdd("T"), 24, HeapKind.Gen0);
        using var graph = builder.Build(TestContext.Current.CancellationToken);

        builder.Dispose();

        allocator.Owner.Disposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1000)]
    public void Permute_in_place_matches_a_reference_permutation(int length)
    {
        var random = new Random(length);
        var permutation = Enumerable.Range(0, length).OrderBy(_ => random.Next()).ToArray();
        var column = Enumerable.Range(0, length).Select(i => (long)i * 7).ToArray();
        var expected = permutation.Select(p => column[p]).ToArray();
        var permutationCopy = permutation.ToArray();

        HeapGraphBuilder.PermuteInPlace(column.AsSpan(), permutation.AsSpan(), TestContext.Current.CancellationToken);

        column.ShouldBe(expected);
        permutation.ShouldBe(permutationCopy);
    }

    [Fact]
    public void Random_graph_by_address_equals_random_graph_by_index()
    {
        var options = new RandomGraphOptions(3_000, 3, TypeCount: 7, RootCount: 5, Seed: 3, AddInReverseOrder: true);
        using var byIndex = SyntheticGraphs.Random(options, cancellationToken: TestContext.Current.CancellationToken);
        using var byAddress = SyntheticGraphs.Random(options with { ReferencesByAddress = true }, cancellationToken: TestContext.Current.CancellationToken);

        byAddress.Edges.Span.SequenceEqual(byIndex.Edges.Span).ShouldBeTrue();
        byAddress.EdgeStarts.Span.SequenceEqual(byIndex.EdgeStarts.Span).ShouldBeTrue();
    }

    [Fact]
    public void Graph_snapshot_never_claims_service_backed_capabilities()
    {
        using var snapshot = new GraphSnapshot(new SnapshotInfo(SnapshotKind.FullDump, "cache"), SnapshotCapabilities.FullDump, SyntheticGraphs.Chain(1));

        snapshot.Capabilities.HasAll(SnapshotCapabilities.FieldValues).ShouldBeFalse();
        snapshot.Capabilities.HasAll(SnapshotCapabilities.StringContents).ShouldBeFalse();
        snapshot.Capabilities.HasAll(SnapshotCapabilities.ThreadStacks).ShouldBeFalse();
        snapshot.Capabilities.HasAll(SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.Generations).ShouldBeTrue();
    }

    private static ulong Address(int index) => 0x1000 + ((ulong)index * 0x18);

    private static HeapGraphBuilder Objects(int count)
    {
        var builder = new HeapGraphBuilder();
        var type = builder.Types.GetOrAdd("T");
        for (var i = 0; i < count; i++)
        {
            builder.AddObject(Address(i), type, 24, HeapKind.Gen0);
        }

        builder.FreezeObjects(TestContext.Current.CancellationToken);
        return builder;
    }

    private sealed class OwningAllocator : IColumnAllocator
    {
        public FlagDisposable Owner { get; } = new();

        public Memory<T> Allocate<T>(string columnName, int length)
            where T : unmanaged => new T[length];

        public IDisposable? DetachOwner() => Owner;
    }

    private sealed class FlagDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
