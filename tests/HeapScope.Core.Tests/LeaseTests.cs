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

    [Fact]
    public void A_dump_snapshot_offers_its_reader_and_releases_it_with_the_graph_after_the_last_lease()
    {
        var owner = new FlagOwner();
        var reader = new FakeReader();
        var all = SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.FieldValues | SnapshotCapabilities.StringContents | SnapshotCapabilities.ThreadStacks;
        var snapshot = new DumpSnapshot(new SnapshotInfo(SnapshotKind.FullDump, "app.dmp"), all, GraphWithOwner(owner), reader);

        snapshot.Capabilities.ShouldBe(SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.FieldValues | SnapshotCapabilities.StringContents);
        snapshot.TryGetService<IObjectReader>(out var service).ShouldBeTrue();
        service.ShouldBeSameAs(reader);
        snapshot.TryGetService<IDisposable>(out _).ShouldBeTrue("the reader itself is the only service");
        var lease = snapshot.AcquireLease();
        snapshot.Dispose();
        reader.Disposed.ShouldBeFalse();
        lease.Dispose();
        (reader.Disposed, owner.DisposeCount).ShouldBe((true, 1));
    }

    private sealed class FakeReader : IObjectReader, IDisposable
    {
        public bool Disposed { get; private set; }

        public IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, int maxText, CancellationToken cancellationToken) => [];

        public ObjectValue? ReadField(ulong address, string path, int maxText = 1000) => null;

        public string? ReadString(ulong address, int maxLength) => null;

        public void ReadStrings(IReadOnlyList<ulong> addresses, int maxLength, Action<int, string?> onEach, CancellationToken cancellationToken)
        {
        }

        public void ReadStringPieces(IReadOnlyList<ulong> addresses, StringPieceHandler onPiece, CancellationToken cancellationToken)
        {
        }

        public IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken) =>
            new Dictionary<ulong, string>();

        public void Dispose() => Disposed = true;
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
