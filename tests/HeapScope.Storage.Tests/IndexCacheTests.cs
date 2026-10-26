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

/// <summary>Sprint 0.5 acceptance criteria for the index cache.</summary>
[Collection(WriterHookCollection.Name)]
public sealed class IndexCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly string _snapshot;
    private readonly IndexCacheMetadata _metadata;

    public IndexCacheTests()
    {
        Directory.CreateDirectory(_directory);
        _snapshot = Path.Combine(_directory, "app.dmp");
        File.WriteAllBytes(_snapshot, Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)(i * 7)).ToArray());
        _metadata = new IndexCacheMetadata(
            new SnapshotInfo(SnapshotKind.FullDump, _snapshot, "App", 42, "10.0.13", 8, new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)),
            SnapshotCapabilities.FullDump,
            IndexCacheMetadata.CurrentWriterVersion);
    }

    public static TheoryData<string, MemoryMode> Graphs()
    {
        var data = new TheoryData<string, MemoryMode>();
        foreach (var name in new[] { "empty", "rich", "random" })
        {
            data.Add(name, MemoryMode.InMemory);
            data.Add(name, MemoryMode.Mapped);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Graphs))]
    public void Every_column_type_root_and_string_survives_a_round_trip(string name, MemoryMode mode)
    {
        using var original = CreateGraph(name);
        var cache = Path.Combine(_directory, name + ".hsidx");
        IndexCacheWriter.Write(cache, original, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);

        var result = IndexCacheReader.Open(cache, Fingerprint(), mode, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.Detail);
        result.Mode.ShouldBe(mode);
        using var copy = result.Graph!;
        AssertSame(original, copy);
        result.Metadata.ShouldBe(_metadata);
    }

    [Fact]
    public void Free_space_per_heap_kind_survives_a_round_trip_and_an_older_cache_has_none()
    {
        using var graph = CreateGraph("rich");
        var cache = Path.Combine(_directory, "free.hsidx");
        var metadata = _metadata with { Info = _metadata.Info with { FreeBytes = [0, 10, 20, 3000, 40, 0, 0] } };
        IndexCacheWriter.Write(cache, graph, metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);

        var result = IndexCacheReader.Open(cache, Fingerprint(), MemoryMode.InMemory, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.Detail);
        using var copy = result.Graph!;
        result.Metadata!.Info.FreeBytes.ShouldBe([0L, 10L, 20L, 3000L, 40L, 0L, 0L]);
        _metadata.Info.FreeBytes.ShouldBeNull("metadata written without it (before 1.6) reads as unknown");
    }

    [Fact]
    public void Writing_twice_produces_identical_bytes()
    {
        using var graph = CreateGraph("rich");
        var first = Path.Combine(_directory, "a.hsidx");
        var second = Path.Combine(_directory, "b.hsidx");

        IndexCacheWriter.Write(first, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);
        IndexCacheWriter.Write(second, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);

        File.ReadAllBytes(first).ShouldBe(File.ReadAllBytes(second));
    }

    [Fact]
    public void Missing_cache_is_reported()
    {
        var result = IndexCacheReader.Open(Path.Combine(_directory, "none.hsidx"), null, MemoryMode.InMemory, TestContext.Current.CancellationToken);

        result.Reason.ShouldBe(IndexCacheInvalidReason.Missing);
    }

    [Fact]
    public void Changed_snapshot_size_timestamp_or_content_invalidates_the_cache()
    {
        var cache = WriteRich();
        var original = Fingerprint();

        File.SetLastWriteTimeUtc(_snapshot, File.GetLastWriteTimeUtc(_snapshot).AddMinutes(1));
        Reason(cache, Fingerprint()).ShouldBe(IndexCacheInvalidReason.FingerprintMismatch);

        Reason(cache, original with { Length = original.Length + 1 }).ShouldBe(IndexCacheInvalidReason.FingerprintMismatch);
        Reason(cache, original with { ContentHash = original.ContentHash ^ 1 }).ShouldBe(IndexCacheInvalidReason.FingerprintMismatch);

        // Same size and timestamp, different content in the first MiB.
        var time = File.GetLastWriteTimeUtc(_snapshot);
        using (var stream = new FileStream(_snapshot, FileMode.Open, FileAccess.Write))
        {
            stream.WriteByte(0xEE);
        }

        File.SetLastWriteTimeUtc(_snapshot, time);
        var changed = Fingerprint();
        changed.Length.ShouldBe(original.Length);
        Reason(cache, changed).ShouldBe(IndexCacheInvalidReason.FingerprintMismatch);
    }

    [Fact]
    public void Different_format_version_is_reported_as_such()
    {
        var cache = WriteRich();
        Patch(cache, offset: 8, BitConverter.GetBytes(IndexCacheFormat.Version + 1));

        var result = IndexCacheReader.Open(cache, null, MemoryMode.InMemory, TestContext.Current.CancellationToken);

        result.Reason.ShouldBe(IndexCacheInvalidReason.VersionMismatch);
        result.Detail!.ShouldContain("version");
    }

    [Fact]
    public void Damaged_header_is_detected()
    {
        var cache = WriteRich();
        Patch(cache, offset: 30, [0x55]);

        Reason(cache, null).ShouldBe(IndexCacheInvalidReason.HeaderCorrupted);
    }

    [Theory]
    [InlineData(MemoryMode.InMemory)]
    [InlineData(MemoryMode.Mapped)]
    public void Corruption_in_any_section_is_detected_and_named(MemoryMode mode)
    {
        var cache = WriteRich();
        var pristine = File.ReadAllBytes(cache);
        foreach (var (section, offset, length) in SectionsOf(cache).Where(s => s.Length > 0))
        {
            File.WriteAllBytes(cache, pristine);
            Patch(cache, offset + (length / 2), [(byte)(pristine[offset + (length / 2)] ^ 0xFF)]);

            var result = IndexCacheReader.Open(cache, null, mode, TestContext.Current.CancellationToken);

            result.Reason.ShouldBe(IndexCacheInvalidReason.ChecksumMismatch, section.ToString());
            result.Detail!.ShouldContain(section.ToString());
        }
    }

    [Fact]
    public void Truncated_cache_is_detected()
    {
        var cache = WriteRich();
        using (var stream = new FileStream(cache, FileMode.Open))
        {
            stream.SetLength(stream.Length - 10);
        }

        Reason(cache, null).ShouldBe(IndexCacheInvalidReason.Truncated);

        using (var stream = new FileStream(cache, FileMode.Open))
        {
            stream.SetLength(100);
        }

        Reason(cache, null).ShouldBe(IndexCacheInvalidReason.Truncated);
    }

    [Fact]
    public void A_write_interrupted_before_the_header_never_opens()
    {
        using var graph = CreateGraph("rich");
        var cache = Path.Combine(_directory, "crash.hsidx");
        var snapshotOfTemp = Path.Combine(_directory, "temp-copy.hsidx");
        IndexCacheWriter.BeforeHeaderForTesting = temp => File.Copy(temp, snapshotOfTemp);
        try
        {
            IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            IndexCacheWriter.BeforeHeaderForTesting = null;
        }

        // The state a crash before the header would leave: every section written, no header.
        Reason(snapshotOfTemp, null).ShouldBe(IndexCacheInvalidReason.BadMagic);
        Reason(cache, Fingerprint()).ShouldBe(IndexCacheInvalidReason.None);
    }

    [Fact]
    public void Canceled_write_leaves_no_cache_and_no_temporary_file()
    {
        using var graph = CreateGraph("random");
        var cache = Path.Combine(_directory, "canceled.hsidx");
        using var cts = new CancellationTokenSource();

        Should.Throw<OperationCanceledException>(() =>
            IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), new InlineProgress(_ => cts.Cancel()), cts.Token));

        File.Exists(cache).ShouldBeFalse();
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Failed_rewrite_keeps_the_previous_cache()
    {
        var cache = WriteRich();
        var before = File.ReadAllBytes(cache);
        using var graph = CreateGraph("random");
        IndexCacheWriter.BeforeHeaderForTesting = _ => throw new IOException("disk full");
        try
        {
            Should.Throw<IOException>(() => IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            IndexCacheWriter.BeforeHeaderForTesting = null;
        }

        File.ReadAllBytes(cache).ShouldBe(before);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Disposing_a_mapped_graph_releases_the_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to delete or replace an open file; Linux and macOS allow it, and a mapping keeps the old file.");
        var cache = WriteRich();
        var result = IndexCacheReader.Open(cache, null, MemoryMode.Mapped, TestContext.Current.CancellationToken);
        Should.Throw<IOException>(() => File.Delete(cache));

        result.Graph!.Dispose();

        Should.NotThrow(() => File.Delete(cache));
    }

    [Fact]
    public void Rewriting_a_cache_that_is_open_fails_clearly_and_keeps_the_old_cache()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to delete or replace an open file; Linux and macOS allow it, and a mapping keeps the old file.");
        var cache = WriteRich();
        var before = File.ReadAllBytes(cache);
        var open = IndexCacheReader.Open(cache, null, MemoryMode.Mapped, TestContext.Current.CancellationToken);
        using var graph = CreateGraph("random");
        try
        {
            Should.Throw<IndexCacheInUseException>(() =>
                IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            open.Graph!.Dispose();
        }

        File.ReadAllBytes(cache).ShouldBe(before);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Stale_temporary_files_of_a_crashed_writer_are_removed()
    {
        var stale = Path.Combine(_directory, "rich.hsidx.0123456789abcdef.tmp");
        var fresh = Path.Combine(_directory, "rich.hsidx.fedcba9876543210.tmp");
        File.WriteAllText(stale, "partial");
        File.WriteAllText(fresh, "another writer");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - IndexCacheWriter.StaleTemporaryAge - TimeSpan.FromMinutes(1));

        WriteRich();

        File.Exists(stale).ShouldBeFalse();
        File.Exists(fresh).ShouldBeTrue("a recent temporary file may belong to a concurrent writer");
    }

    [Fact]
    public void Snapshot_lease_keeps_a_mapped_file_open_until_released()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to delete or replace an open file; Linux and macOS allow it, and a mapping keeps the old file.");
        var cache = WriteRich();
        var open = IndexCacheReader.Open(cache, null, MemoryMode.Mapped, TestContext.Current.CancellationToken);
        var snapshot = new GraphSnapshot(_metadata.Info, _metadata.Capabilities, open.Graph!);
        var lease = snapshot.AcquireLease();

        snapshot.Dispose();
        Should.Throw<IOException>(() => File.Delete(cache), "a result set or query still holds a lease");

        lease.Dispose();
        Should.NotThrow(() => File.Delete(cache));
    }

    [Fact]
    public void Identity_is_stable_per_cache_content()
    {
        var cache = WriteRich();
        var first = IndexCacheReader.Open(cache, null, MemoryMode.InMemory, TestContext.Current.CancellationToken);
        var second = IndexCacheReader.Open(cache, null, MemoryMode.Mapped, TestContext.Current.CancellationToken);
        first.Graph!.Dispose();
        second.Graph!.Dispose();

        first.Identity.ShouldNotBe(0UL);
        second.Identity.ShouldBe(first.Identity);
    }

    [Fact]
    public void In_memory_size_comes_from_the_directory()
    {
        using var graph = CreateGraph("random");
        var cache = Path.Combine(_directory, "size.hsidx");
        IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);

        IndexCacheReader.GetInMemoryBytes(cache)!.Value.ShouldBeGreaterThanOrEqualTo(graph.ColumnBytes);
        IndexCacheReader.GetInMemoryBytes(Path.Combine(_directory, "none.hsidx")).ShouldBeNull();
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

    private static HeapGraph CreateGraph(string name) => name switch
    {
        "empty" => new HeapGraphBuilder().Build(),
        "random" => SyntheticGraphs.Random(new RandomGraphOptions(20_000, 3, TypeCount: 50, RootCount: 30, Seed: 9)),
        _ => CreateRichGraph(),
    };

    /// <summary>Every column kind populated: large sizes, every root kind with details, flags, modules, drops.</summary>
    private static HeapGraph CreateRichGraph()
    {
        var builder = new HeapGraphBuilder();
        var array = builder.Types.GetOrAdd("System.Byte[]", 0x1000, 24, 1, "System.Private.CoreLib.dll", TypeKind.Array);
        var text = builder.Types.GetOrAdd("System.String", 0x2000, 22, 2, "System.Private.CoreLib.dll", TypeKind.String);
        var node = builder.Types.GetOrAdd("App.Node été", 0x3000, 32, 0, "App.dll");
        builder.AddObject(0x10_0000, node, 32, HeapKind.Gen0, ObjectFlags.HasFinalizer);
        builder.AddObject(0x20_0000, array, 6UL * 1024 * 1024 * 1024, HeapKind.LargeObjectHeap);
        builder.AddObject(0x08_0000, text, 40, HeapKind.Frozen);
        builder.AddObject(0x30_0000, node, 32, HeapKind.PinnedObjectHeap);
        builder.FreezeObjects();
        builder.AddDependentHandle(0x10_0000, 0x30_0000);
        builder.AddReferences(1, [0x20_0000UL, 0xDEAD, 0x08_0000]);
        var kinds = Enum.GetValues<RootKind>();
        for (var i = 0; i < kinds.Length; i++)
        {
            builder.AddRootIndex(i % 4, kinds[i], RootFlags.Retaining, $"detail {i}");
        }

        return builder.Build();
    }

    private static void AssertSame(HeapGraph expected, HeapGraph actual)
    {
        actual.Addresses.Span.SequenceEqual(expected.Addresses.Span).ShouldBeTrue();
        actual.TypeIndices.Span.SequenceEqual(expected.TypeIndices.Span).ShouldBeTrue();
        actual.Sizes.Span.SequenceEqual(expected.Sizes.Span).ShouldBeTrue();
        actual.HeapKinds.Span.SequenceEqual(expected.HeapKinds.Span).ShouldBeTrue();
        actual.Flags.Span.SequenceEqual(expected.Flags.Span).ShouldBeTrue();
        actual.EdgeStarts.Span.SequenceEqual(expected.EdgeStarts.Span).ShouldBeTrue();
        actual.Edges.Span.SequenceEqual(expected.Edges.Span).ShouldBeTrue();
        actual.LargeSizes.Span.SequenceEqual(expected.LargeSizes.Span).ShouldBeTrue();
        actual.Roots.Roots.Span.SequenceEqual(expected.Roots.Roots.Span).ShouldBeTrue();
        actual.Strings.Strings.ShouldBe(expected.Strings.Strings);
        actual.Types.Count.ShouldBe(expected.Types.Count);
        for (var t = 0; t < expected.Types.Count; t++)
        {
            actual.Types.GetName(t).ShouldBe(expected.Types.GetName(t));
            actual.Types.GetModule(t).ShouldBe(expected.Types.GetModule(t));
            actual.Types.MethodTables[t].ShouldBe(expected.Types.MethodTables[t]);
            actual.Types.BaseSizes[t].ShouldBe(expected.Types.BaseSizes[t]);
            actual.Types.ComponentSizes[t].ShouldBe(expected.Types.ComponentSizes[t]);
            actual.Types.Kinds[t].ShouldBe(expected.Types.Kinds[t]);
        }

        actual.TotalSize.ShouldBe(expected.TotalSize);
        actual.DroppedReferenceCount.ShouldBe(expected.DroppedReferenceCount);
        actual.DroppedRootCount.ShouldBe(expected.DroppedRootCount);
    }

    private SnapshotFingerprint Fingerprint() => SnapshotFingerprint.Compute(_snapshot);

    private string WriteRich()
    {
        using var graph = CreateGraph("rich");
        var cache = Path.Combine(_directory, "rich.hsidx");
        IndexCacheWriter.Write(cache, graph, _metadata, Fingerprint(), cancellationToken: TestContext.Current.CancellationToken);
        return cache;
    }

    private static IndexCacheInvalidReason Reason(string cache, SnapshotFingerprint? expected)
    {
        var result = IndexCacheReader.Open(cache, expected, MemoryMode.InMemory, TestContext.Current.CancellationToken);
        result.Graph?.Dispose();
        return result.Reason;
    }

    private static void Patch(string path, long offset, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.Position = offset;
        stream.Write(bytes);
    }

    /// <summary>Reads the section directory (64 byte header, then 40 byte entries) for corruption tests.</summary>
    private static List<(IndexSection Section, long Offset, long Length)> SectionsOf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var count = BitConverter.ToInt32(bytes, 16);
        var sections = new List<(IndexSection, long, long)>();
        for (var i = 0; i < count; i++)
        {
            var at = 64 + (i * 40);
            sections.Add(((IndexSection)BitConverter.ToInt32(bytes, at), BitConverter.ToInt64(bytes, at + 8), BitConverter.ToInt64(bytes, at + 16)));
        }

        return sections;
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
