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

using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Loading;
using HeapScope.Core.Synthetic;
using HeapScope.Loaders.ClrMd;
using HeapScope.Storage;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Worker.Tests;

/// <summary>What a cache that cannot be used leads to, and what the snapshot says about it (sprint 3.10 review H6, M39).</summary>
public sealed class CacheRebuildTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-rebuild-").FullName;
    private readonly WorkerOptions _options;
    private readonly IndexCacheLocator _locator;
    private readonly string _dump;

    public CacheRebuildTests()
    {
        _options = new WorkerOptions("unused", null) { DataFolder = _folder };
        _locator = new IndexCacheLocator(_options.Folders.Cache);
        _dump = Path.Combine(_folder, "app.dmp");
        File.WriteAllBytes(_dump, [.. "MDMP"u8, 0x93, 0xA7, 0, 0, 1, 2, 3, 4]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SnapshotStore Store(ISnapshotLoader? loader = null) =>
        new(_options, _locator, IndexCacheSigner.None, NullLogger<SnapshotStore>.Instance) { LoaderForTests = _ => loader ?? new NoDacLoader() };

    private void WriteDamagedCache()
    {
        WriteCache(ClrMdDumpLoader.CurrentVersion);
        var cache = _locator.GetCachePath(_dump);
        var bytes = File.ReadAllBytes(cache);
        Array.Fill(bytes, (byte)0xFF, 16, 64);
        File.WriteAllBytes(cache, bytes);
    }

    private void WriteCache(int loaderVersion)
    {
        using var graph = SyntheticGraphs.Chain(10);
        var metadata = new IndexCacheMetadata(
            new SnapshotInfo(SnapshotKind.FullDump, _dump),
            SnapshotCapabilities.ObjectGraph,
            IndexCacheMetadata.CurrentWriterVersion,
            LoaderVersion: loaderVersion);
        IndexCacheWriter.Write(_locator.GetCachePath(_dump), IndexCacheSigner.None, graph, metadata, SnapshotFingerprint.Compute(_dump), cancellationToken: Ct);
    }

    [Fact]
    public async Task An_older_cache_whose_rebuild_lacks_the_DAC_says_what_is_missing()
    {
        // Opening the older cache instead is planned for sprint 3.11 (the 3.10 review M14).
        WriteCache(ClrMdDumpLoader.CurrentVersion - 1);
        using var store = Store();

        var missing = await Should.ThrowAsync<RuntimeComponentMissingException>(store.OpenAsync(new OpenSnapshotRequest(_dump), null, Ct));

        missing.Message.ShouldContain("No DAC here");
    }

    [Fact]
    public async Task An_older_cache_rebuilt_says_so()
    {
        WriteCache(ClrMdDumpLoader.CurrentVersion - 1);
        using var store = Store(new GraphLoader(_dump));

        var opened = await store.OpenAsync(new OpenSnapshotRequest(_dump), null, Ct);

        opened.FromCache.ShouldBeFalse();
        opened.Notes.ShouldContain(static n => n.StartsWith("The index cache was rebuilt: it was written by an older version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_damaged_cache_rebuilt_says_so()
    {
        WriteDamagedCache();
        using var store = Store(new GraphLoader(_dump));

        var opened = await store.OpenAsync(new OpenSnapshotRequest(_dump), null, Ct);

        opened.Notes.ShouldContain(static n => n.StartsWith("The index cache was rebuilt: ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cache_that_could_not_be_written_is_not_called_rebuilt(bool damaged)
    {
        // The note used to be added before the load, so a failed write showed "rebuilt" and "could not be written" together (M39).
        if (damaged)
        {
            WriteDamagedCache();
        }
        else
        {
            WriteCache(ClrMdDumpLoader.CurrentVersion - 1);
        }

        // A file where the cache's folder belongs, once the old cache has been read: the write fails on every system.
        var folder = Path.GetDirectoryName(_locator.GetCachePath(_dump))!;
        using var store = Store(new GraphLoader(_dump, () =>
        {
            Directory.Delete(folder, recursive: true);
            File.WriteAllBytes(folder, []);
        }));

        var opened = await store.OpenAsync(new OpenSnapshotRequest(_dump), null, Ct);

        opened.CachePath.ShouldBeNull();
        opened.Notes.ShouldContain(static n => n.StartsWith("The index cache could not be written", StringComparison.Ordinal));
        opened.Notes.ShouldContain(static n => n.StartsWith("The index cache was not used: ", StringComparison.Ordinal));
        opened.Notes.ShouldNotContain(static n => n.StartsWith("The index cache was rebuilt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reopen_after_a_worker_restart_does_not_quietly_take_an_older_cache()
    {
        // It could replace a current graph whose cache could not be written, and a reopen has no rebuild to report (3.10 review H6).
        WriteCache(ClrMdDumpLoader.CurrentVersion - 1);
        using var store = Store();

        var refused = await Should.ThrowAsync<SnapshotChangedException>(store.OpenAsync(new OpenSnapshotRequest(_dump, RequireCache: true), null, Ct));

        refused.Message.ShouldContain("out of date");
    }

    [Fact]
    public async Task An_explicit_rebuild_still_fails_and_says_what_is_missing()
    {
        WriteCache(ClrMdDumpLoader.CurrentVersion - 1);
        using var store = Store();

        await Should.ThrowAsync<RuntimeComponentMissingException>(store.OpenAsync(new OpenSnapshotRequest(_dump, Rebuild: true), null, Ct));
    }

    [Fact]
    public async Task With_no_cache_the_missing_DAC_is_the_answer()
    {
        using var store = Store();

        await Should.ThrowAsync<RuntimeComponentMissingException>(store.OpenAsync(new OpenSnapshotRequest(_dump), null, Ct));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A mapped cache can outlive the test by a moment; the temp folder is the system's to clean.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    /// <summary>A load that works, with an optional step while it runs.</summary>
    private sealed class GraphLoader(string dump, Action? during = null) : ISnapshotLoader
    {
        public SnapshotKind Kind => SnapshotKind.FullDump;

        public int Version => ClrMdDumpLoader.CurrentVersion;

        public LoadResult Load(string dumpPath, IProgress<LoadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            during?.Invoke();
            return new LoadResult(
                SyntheticGraphs.Chain(10),
                new SnapshotInfo(SnapshotKind.FullDump, dump),
                SnapshotCapabilities.ObjectGraph,
                new LoadDiagnostics([], 0, 0),
                new LoadTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        }
    }

    private sealed class NoDacLoader : ISnapshotLoader
    {
        public SnapshotKind Kind => SnapshotKind.FullDump;

        public int Version => ClrMdDumpLoader.CurrentVersion;

        public LoadResult Load(string dumpPath, IProgress<LoadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new RuntimeComponentMissingException("No DAC here.");
    }
}
