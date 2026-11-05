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

/// <summary>A loader update must not cost the user a dump whose DAC is gone: the older cache still opens (sprint 3.10 review M14).</summary>
public sealed class OlderCacheTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-older-").FullName;
    private readonly WorkerOptions _options;
    private readonly IndexCacheLocator _locator;
    private readonly string _dump;

    public OlderCacheTests()
    {
        _options = new WorkerOptions("unused", null) { DataFolder = _folder };
        _locator = new IndexCacheLocator(_options.Folders.Cache);
        _dump = Path.Combine(_folder, "app.dmp");
        File.WriteAllBytes(_dump, [.. "MDMP"u8, 0x93, 0xA7, 0, 0, 1, 2, 3, 4]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SnapshotStore Store() =>
        new(_options, _locator, IndexCacheSigner.None, NullLogger<SnapshotStore>.Instance) { LoaderForTests = static _ => new NoDacLoader() };

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
    public async Task An_older_cache_opens_with_a_note_when_its_rebuild_lacks_the_DAC()
    {
        WriteCache(ClrMdDumpLoader.CurrentVersion - 1);
        using var store = Store();

        var opened = await store.OpenAsync(new OpenSnapshotRequest(_dump), null, Ct);

        opened.FromCache.ShouldBeTrue();
        opened.ObjectCount.ShouldBe(10);
        opened.Notes.ShouldContain(static n => n.StartsWith("This snapshot uses an index cache from an older version", StringComparison.Ordinal) && n.Contains("No DAC here", StringComparison.Ordinal));
        opened.Notes.ShouldNotContain(static n => n.StartsWith("The index cache was rebuilt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reopen_after_a_worker_restart_keeps_the_older_cache()
    {
        WriteCache(ClrMdDumpLoader.CurrentVersion - 1);
        using var store = Store();

        var opened = await store.OpenAsync(new OpenSnapshotRequest(_dump, RequireCache: true), null, Ct);

        opened.FromCache.ShouldBeTrue();
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

    private sealed class NoDacLoader : ISnapshotLoader
    {
        public SnapshotKind Kind => SnapshotKind.FullDump;

        public int Version => ClrMdDumpLoader.CurrentVersion;

        public LoadResult Load(string dumpPath, IProgress<LoadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new RuntimeComponentMissingException("No DAC here.");
    }
}
