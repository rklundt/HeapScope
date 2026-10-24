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

using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;

namespace HeapScope.Storage.Tests;

/// <summary>Sprint 2.1: the incremental section writer, the cache identity and sidecars opened through <see cref="IndexCacheFile"/>.</summary>
public sealed class SidecarTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly string _snapshot;
    private readonly IndexCacheMetadata _metadata;

    public SidecarTests()
    {
        Directory.CreateDirectory(_directory);
        _snapshot = Path.Combine(_directory, "app.dmp");
        File.WriteAllBytes(_snapshot, Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 3)).ToArray());
        _metadata = new IndexCacheMetadata(
            new SnapshotInfo(SnapshotKind.FullDump, _snapshot, "App", 42, "10.0.13", 8, new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)),
            SnapshotCapabilities.FullDump,
            IndexCacheMetadata.CurrentWriterVersion);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Write_returns_the_identity_the_reader_reports()
    {
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(1_000, 2, TypeCount: 5, RootCount: 3, Seed: 4), cancellationToken: TestContext.Current.CancellationToken);
        var cache = Path.Combine(_directory, "app.dmp.hsidx");

        var identity = IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);

        var opened = IndexCacheReader.Open(cache, Fingerprint(), MemoryMode.InMemory, TestContext.Current.CancellationToken);
        opened.Success.ShouldBeTrue(opened.Detail);
        using var _ = opened.Graph!;
        identity.ShouldNotBe(0UL);
        opened.Identity.ShouldBe(identity);
    }

    [Theory]
    [InlineData(MemoryMode.InMemory)]
    [InlineData(MemoryMode.Mapped)]
    public void A_sidecar_round_trips_with_its_base_identity(MemoryMode mode)
    {
        var path = WriteSidecar(baseIdentity: 0x1234, starts: [0, 2, 3, 3], edges: [1, 2, 0]);

        var result = IndexCacheFile.Open(path, IndexFileLayout.ReverseReferences, 0x1234, Fingerprint(), mode, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.Detail);
        using var file = result.File!;
        file.Mode.ShouldBe(mode);
        file.BaseIdentity.ShouldBe(0x1234UL);
        file.Identity.ShouldNotBe(0UL);
        file.TryGetSection<int>(IndexSection.InEdgeStarts, out var starts).ShouldBeTrue();
        file.TryGetSection<int>(IndexSection.InEdges, out var edges).ShouldBeTrue();
        starts.Span.ToArray().ShouldBe([0, 2, 3, 3]);
        edges.Span.ToArray().ShouldBe([1, 2, 0]);
        file.TryGetSection<long>(IndexSection.InEdges, out _).ShouldBeFalse("the element size differs");
        file.TryGetSection<int>(IndexSection.Edges, out _).ShouldBeFalse("not a section of this file");
    }

    [Theory]
    [InlineData(MemoryMode.InMemory)]
    [InlineData(MemoryMode.Mapped)]
    public void Sections_of_every_element_size_round_trip(MemoryMode mode)
    {
        // Review M1: in memory, sections are arrays of their element size, so a section holds 2^31 - 1 elements.
        var layout = new IndexFileLayout("test", new Dictionary<IndexSection, int>
        {
            [IndexSection.HeapKinds] = 1,
            [IndexSection.TypeKinds] = 2,
            [IndexSection.InEdges] = 4,
            [IndexSection.Addresses] = 8,
            [IndexSection.Roots] = 12,
        });
        var path = Path.Combine(_directory, "sizes.hsidx");
        using (var writer = IndexCacheFileWriter.Create(path, cancellationToken: TestContext.Current.CancellationToken))
        {
            writer.WriteSection<byte>(IndexSection.HeapKinds, [1, 2, 3]);
            writer.WriteSection<ushort>(IndexSection.TypeKinds, [7, 65535]);
            writer.WriteSection<int>(IndexSection.InEdges, [-1, 5]);
            writer.WriteSection<ulong>(IndexSection.Addresses, [ulong.MaxValue, 0x1000]);
            writer.WriteSection<GcRoot>(IndexSection.Roots, [new GcRoot(4, RootKind.StrongHandle, RootFlags.Retaining, 9)]);
            writer.Commit(Fingerprint(), baseIdentity: 3);
        }

        var result = IndexCacheFile.Open(path, layout, 3, Fingerprint(), mode, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.Detail);
        using var file = result.File!;
        file.TryGetSection<byte>(IndexSection.HeapKinds, out var bytes).ShouldBeTrue();
        file.TryGetSection<ushort>(IndexSection.TypeKinds, out var shorts).ShouldBeTrue();
        file.TryGetSection<int>(IndexSection.InEdges, out var ints).ShouldBeTrue();
        file.TryGetSection<ulong>(IndexSection.Addresses, out var longs).ShouldBeTrue();
        file.TryGetSection<GcRoot>(IndexSection.Roots, out var roots).ShouldBeTrue();
        bytes.Span.ToArray().ShouldBe([(byte)1, (byte)2, (byte)3]);
        shorts.Span.ToArray().ShouldBe([(ushort)7, (ushort)65535]);
        ints.Span.ToArray().ShouldBe([-1, 5]);
        longs.Span.ToArray().ShouldBe([ulong.MaxValue, 0x1000UL]);
        roots.Span[0].ShouldBe(new GcRoot(4, RootKind.StrongHandle, RootFlags.Retaining, 9));
        using var pin = ints.Memory.Pin();
    }

    [Fact]
    public void A_sidecar_of_another_base_cache_is_refused()
    {
        var path = WriteSidecar(baseIdentity: 0x1234, starts: [0, 0], edges: []);

        var result = IndexCacheFile.Open(path, IndexFileLayout.ReverseReferences, 0x9999, Fingerprint(), MemoryMode.InMemory, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(IndexCacheInvalidReason.BaseMismatch);
        result.Detail!.ShouldContain("another version");
    }

    [Fact]
    public void A_sidecar_from_another_snapshot_is_refused()
    {
        var path = WriteSidecar(baseIdentity: 1, starts: [0, 0], edges: []);

        var result = IndexCacheFile.Open(path, IndexFileLayout.ReverseReferences, 1, new SnapshotFingerprint(1, 2, 3), MemoryMode.InMemory, TestContext.Current.CancellationToken);

        result.Reason.ShouldBe(IndexCacheInvalidReason.FingerprintMismatch);
    }

    [Fact]
    public void A_missing_or_foreign_section_is_malformed()
    {
        var missing = Path.Combine(_directory, "missing.rev.hsidx");
        using (var writer = IndexCacheFileWriter.Create(missing, cancellationToken: TestContext.Current.CancellationToken))
        {
            writer.WriteSection<int>(IndexSection.InEdgeStarts, [0]);
            writer.Commit(Fingerprint(), baseIdentity: 1);
        }

        var foreign = Path.Combine(_directory, "foreign.rev.hsidx");
        using (var writer = IndexCacheFileWriter.Create(foreign, cancellationToken: TestContext.Current.CancellationToken))
        {
            writer.WriteSection<int>(IndexSection.InEdgeStarts, [0]);
            writer.WriteSection<int>(IndexSection.InEdges, []);
            writer.WriteSection<int>(IndexSection.Edges, []);
            writer.Commit(Fingerprint(), baseIdentity: 1);
        }

        var a = IndexCacheFile.Open(missing, IndexFileLayout.ReverseReferences, 1, null, MemoryMode.InMemory, TestContext.Current.CancellationToken);
        var b = IndexCacheFile.Open(foreign, IndexFileLayout.ReverseReferences, 1, null, MemoryMode.Mapped, TestContext.Current.CancellationToken);

        a.Reason.ShouldBe(IndexCacheInvalidReason.Malformed);
        a.Detail!.ShouldContain("InEdges is missing");
        b.Reason.ShouldBe(IndexCacheInvalidReason.Malformed);
    }

    [Theory]
    [InlineData(MemoryMode.InMemory)]
    [InlineData(MemoryMode.Mapped)]
    public void A_corrupted_sidecar_section_is_a_checksum_mismatch(MemoryMode mode)
    {
        var path = WriteSidecar(baseIdentity: 7, starts: [0, 1, 2], edges: [1, 0]);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            // The second section starts on the third page; flip a byte of its data.
            stream.Position = (2 * IndexCacheFormat.PageSize) + 1;
            stream.WriteByte(0x5A);
        }

        var result = IndexCacheFile.Open(path, IndexFileLayout.ReverseReferences, 7, Fingerprint(), mode, TestContext.Current.CancellationToken);

        result.Reason.ShouldBe(IndexCacheInvalidReason.ChecksumMismatch);
    }

    [Fact]
    public void A_base_cache_and_a_sidecar_cannot_be_opened_as_each_other()
    {
        using var graph = SyntheticGraphs.Chain(10);
        var cache = Path.Combine(_directory, "app.dmp.hsidx");
        var identity = IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);
        var sidecar = WriteSidecar(identity, starts: [0, 0], edges: []);

        var baseAsSidecar = IndexCacheFile.Open(cache, IndexFileLayout.ReverseReferences, identity, Fingerprint(), MemoryMode.InMemory, TestContext.Current.CancellationToken);
        var sidecarAsBase = IndexCacheReader.Open(sidecar, Fingerprint(), MemoryMode.InMemory, TestContext.Current.CancellationToken);

        baseAsSidecar.Success.ShouldBeFalse();
        sidecarAsBase.Success.ShouldBeFalse();
    }

    [Fact]
    public void A_base_cache_opens_through_the_generic_api_too()
    {
        using var graph = SyntheticGraphs.Chain(10);
        var cache = Path.Combine(_directory, "app.dmp.hsidx");
        var identity = IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);

        var result = IndexCacheFile.Open(cache, IndexFileLayout.Base, 0, Fingerprint(), MemoryMode.Mapped, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.Detail);
        using var file = result.File!;
        file.Identity.ShouldBe(identity);
        file.TryGetSection<ulong>(IndexSection.Addresses, out var addresses).ShouldBeTrue();
        addresses.Span.ToArray().ShouldBe(graph.Addresses.Span.ToArray());
    }

    [Fact]
    public void An_uncommitted_writer_leaves_nothing_behind()
    {
        var path = Path.Combine(_directory, "aborted.rev.hsidx");
        using (var writer = IndexCacheFileWriter.Create(path, cancellationToken: TestContext.Current.CancellationToken))
        {
            writer.WriteSection<int>(IndexSection.InEdgeStarts, [0, 1]);
            Should.Throw<ArgumentException>(() => writer.WriteSection<int>(IndexSection.InEdgeStarts, [0]));
        }

        Directory.EnumerateFiles(_directory, "aborted*").ShouldBeEmpty();
    }

    [Fact]
    public void Sidecars_sit_next_to_their_base_cache() =>
        IndexCacheLocator.GetSidecarPath(@"C:\dumps\app.dmp.hsidx", "rev").ShouldBe(@"C:\dumps\app.dmp.rev.hsidx");

    private string WriteSidecar(ulong baseIdentity, int[] starts, int[] edges)
    {
        var path = Path.Combine(_directory, $"app.dmp.{Guid.NewGuid():N}.rev.hsidx");
        using var writer = IndexCacheFileWriter.Create(path, cancellationToken: TestContext.Current.CancellationToken);
        writer.WriteSection<int>(IndexSection.InEdgeStarts, starts);
        writer.WriteSection<int>(IndexSection.InEdges, edges);
        writer.Commit(Fingerprint(), baseIdentity);
        return path;
    }

    private SnapshotFingerprint Fingerprint() => SnapshotFingerprint.Compute(_snapshot);
}
