using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Core.Tests;

public class LeaseTests
{
    [Fact]
    public void Dispose_returns_at_once_and_releases_the_graph_when_the_last_lease_is_returned()
    {
        var owner = new FlagOwner();
        var snapshot = new GraphSnapshot(new SnapshotInfo(SnapshotKind.GcDump, "s"), SnapshotCapabilities.ObjectGraph, GraphWithOwner(owner));
        var first = snapshot.AcquireLease();
        var second = snapshot.AcquireLease();

        snapshot.Dispose();
        snapshot.Dispose();

        owner.DisposeCount.ShouldBe(0, "leases are still held");
        Should.Throw<ObjectDisposedException>(() => snapshot.AcquireLease());
        first.Dispose();
        first.Dispose();
        owner.DisposeCount.ShouldBe(0);
        second.Dispose();
        owner.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public void Dispose_without_leases_releases_at_once()
    {
        var owner = new FlagOwner();
        var snapshot = new GraphSnapshot(new SnapshotInfo(SnapshotKind.GcDump, "s"), SnapshotCapabilities.ObjectGraph, GraphWithOwner(owner));

        snapshot.Dispose();

        owner.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public void Concurrent_dispose_and_release_release_exactly_once()
    {
        for (var round = 0; round < 200; round++)
        {
            var owner = new FlagOwner();
            var snapshot = new GraphSnapshot(new SnapshotInfo(SnapshotKind.GcDump, "s"), SnapshotCapabilities.ObjectGraph, GraphWithOwner(owner));
            var leases = Enumerable.Range(0, 8).Select(_ => snapshot.AcquireLease()).ToArray();

            Parallel.Invoke(
                [snapshot.Dispose, snapshot.Dispose, .. leases.Select(l => (Action)l.Dispose)]);

            owner.DisposeCount.ShouldBe(1);
        }
    }

    private static HeapGraph GraphWithOwner(FlagOwner owner)
    {
        var builder = new HeapGraphBuilder(new OwningAllocator(owner));
        builder.AddObject(0x1000, builder.Types.GetOrAdd("T"), 24, HeapKind.Gen0);
        return builder.Build(TestContext.Current.CancellationToken);
    }

    private sealed class OwningAllocator(FlagOwner owner) : IColumnAllocator
    {
        public Memory<T> Allocate<T>(string columnName, int length)
            where T : unmanaged => new T[length];

        public IDisposable? DetachOwner() => owner;
    }

    private sealed class FlagOwner : IDisposable
    {
        private int _count;

        public int DisposeCount => Volatile.Read(ref _count);

        public void Dispose() => Interlocked.Increment(ref _count);
    }
}
