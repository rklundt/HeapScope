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

/// <summary>Sprint 2.2: dependent-handle pairs in the index cache (format version 2).</summary>
public sealed class DependentHandleCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly string _snapshot;
    private readonly IndexCacheMetadata _metadata;

    public DependentHandleCacheTests()
    {
        Directory.CreateDirectory(_directory);
        _snapshot = Path.Combine(_directory, "app.dmp");
        File.WriteAllBytes(_snapshot, [1, 2, 3]);
        _metadata = new IndexCacheMetadata(
            new SnapshotInfo(SnapshotKind.FullDump, _snapshot, "App", 42, "10.0.13", 8, DateTimeOffset.UnixEpoch),
            SnapshotCapabilities.FullDump,
            IndexCacheMetadata.CurrentWriterVersion);
    }

    [Theory]
    [InlineData(MemoryMode.InMemory)]
    [InlineData(MemoryMode.Mapped)]
    public void Dependent_handles_survive_a_round_trip(MemoryMode mode)
    {
        var builder = new SyntheticGraphBuilder();
        var key = builder.Object("App.Key");
        var value = builder.Object("App.Value");
        builder.DependentHandle(key, value);
        using var graph = builder.Build();
        var cache = Path.Combine(_directory, "app.dmp.hsidx");
        IndexCacheWriter.Write(cache, graph, _metadata, SnapshotFingerprint.Compute(_snapshot), cancellationToken: TestContext.Current.CancellationToken);

        var opened = IndexCacheReader.Open(cache, SnapshotFingerprint.Compute(_snapshot), mode, TestContext.Current.CancellationToken);

        opened.Success.ShouldBeTrue(opened.Detail);
        using var copy = opened.Graph!;
        copy.DependentHandles.Span.ToArray().ShouldBe([new DependentHandle(key.Id, value.Id)]);
        copy.IsDependentHandleEdge(key.Id, value.Id).ShouldBeTrue();
    }

    [Fact]
    public void A_version_1_cache_is_refused_so_it_is_rebuilt()
    {
        using var graph = SyntheticGraphs.Chain(3);
        var cache = Path.Combine(_directory, "old.hsidx");
        IndexCacheWriter.Write(cache, graph, _metadata, SnapshotFingerprint.Compute(_snapshot), cancellationToken: TestContext.Current.CancellationToken);
        using (var stream = new FileStream(cache, FileMode.Open, FileAccess.Write))
        {
            // FormatVersion follows the 8-byte magic.
            stream.Position = 8;
            stream.Write(BitConverter.GetBytes(1));
        }

        var opened = IndexCacheReader.Open(cache, SnapshotFingerprint.Compute(_snapshot), MemoryMode.InMemory, TestContext.Current.CancellationToken);

        opened.Reason.ShouldBe(IndexCacheInvalidReason.VersionMismatch);
        IndexCacheFormat.Version.ShouldBe(2);
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
}
