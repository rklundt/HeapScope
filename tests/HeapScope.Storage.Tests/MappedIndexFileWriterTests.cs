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

namespace HeapScope.Storage.Tests;

/// <summary>Tests that set <c>IndexCacheWriter.BeforeHeaderForTesting</c>, a static hook, run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class WriterHookCollection
{
    public const string Name = "Index writer test hook";
}

[Collection(WriterHookCollection.Name)]
public sealed class MappedIndexFileWriterTests : IDisposable
{
    // Two sections of the dominators sidecar with a semantics version: the writer's mechanics, not the sidecar's full
    // layout (which grew the tree's children in sprint 2.4).
    private static readonly IndexFileLayout s_dominators = new("dominators", new Dictionary<IndexSection, int>
    {
        [IndexSection.ImmediateDominators] = sizeof(int),
        [IndexSection.RetainedSizes] = sizeof(ulong),
    }, 7);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly string _snapshot;

    public MappedIndexFileWriterTests()
    {
        Directory.CreateDirectory(_directory);
        _snapshot = Path.Combine(_directory, "app.dmp");
        File.WriteAllBytes(_snapshot, Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 5)).ToArray());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Theory]
    [InlineData(MemoryMode.InMemory)]
    [InlineData(MemoryMode.Mapped)]
    public void Sections_filled_in_place_round_trip_with_their_semantics_version(MemoryMode mode)
    {
        var path = Write(semanticsVersion: 7, dominators: [-2, 0, 1, -1], retained: [100, 60, 20, 0]);

        var result = IndexCacheFile.Open(path, IndexCacheSigner.None, s_dominators, 0x42, Fingerprint(), mode, Ct);

        result.Success.ShouldBeTrue(result.Detail);
        using var file = result.File!;
        file.TryGetSection<int>(IndexSection.ImmediateDominators, out var dominators).ShouldBeTrue();
        file.TryGetSection<ulong>(IndexSection.RetainedSizes, out var retained).ShouldBeTrue();
        dominators.Span.ToArray().ShouldBe([-2, 0, 1, -1]);
        retained.Span.ToArray().ShouldBe([100UL, 60UL, 20UL, 0UL]);
        file.BaseIdentity.ShouldBe(0x42UL);
    }

    [Fact]
    public void The_file_equals_one_written_section_by_section()
    {
        var mapped = Write(semanticsVersion: 7, dominators: [-2, 0, 0], retained: [3, 1, 1]);
        var streamed = Path.Combine(_directory, "streamed.dom.hsidx");
        using (var writer = IndexCacheFileWriter.Create(streamed, IndexCacheSigner.None, cancellationToken: Ct))
        {
            writer.WriteSection<int>(IndexSection.ImmediateDominators, [-2, 0, 0]);
            writer.WriteSection<ulong>(IndexSection.RetainedSizes, [3, 1, 1]);
            writer.Commit(Fingerprint(), 0x42, semanticsVersion: 7);
        }

        File.ReadAllBytes(mapped).ShouldBe(File.ReadAllBytes(streamed));
    }

    [Fact]
    public void A_file_computed_under_other_rules_is_refused()
    {
        var path = Write(semanticsVersion: 6, dominators: [-2], retained: [8]);

        var result = IndexCacheFile.Open(path, IndexCacheSigner.None, s_dominators, 0x42, Fingerprint(), MemoryMode.InMemory, Ct);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(IndexCacheInvalidReason.VersionMismatch);
        result.Detail!.ShouldContain("rules version 6");
    }

    [Fact]
    public void Empty_sections_and_an_empty_file_are_valid()
    {
        var path = Write(semanticsVersion: 7, dominators: [], retained: []);

        var result = IndexCacheFile.Open(path, IndexCacheSigner.None, s_dominators, 0x42, Fingerprint(), MemoryMode.Mapped, Ct);

        result.Success.ShouldBeTrue(result.Detail);
        using var file = result.File!;
        file.TryGetSection<int>(IndexSection.ImmediateDominators, out var dominators).ShouldBeTrue();
        dominators.Length.ShouldBe(0);
    }

    [Fact]
    public void A_writer_that_never_commits_leaves_the_previous_file_and_nothing_else()
    {
        var path = Write(semanticsVersion: 7, dominators: [-2], retained: [8]);
        var before = File.ReadAllBytes(path);
        using (var writer = MappedIndexFileWriter.Create(path, IndexCacheSigner.None, Plan(1), Ct))
        {
            writer.GetSection<int>(IndexSection.ImmediateDominators)[0] = 5;
        }

        File.ReadAllBytes(path).ShouldBe(before);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Unplanned_or_mistyped_sections_and_use_after_commit_are_refused()
    {
        var path = Path.Combine(_directory, "misuse.dom.hsidx");
        using var writer = MappedIndexFileWriter.Create(path, IndexCacheSigner.None, Plan(2), Ct);

        Should.Throw<ArgumentException>(() => writer.GetSection<int>(IndexSection.InEdges));
        Should.Throw<ArgumentException>(() => writer.GetSection<long>(IndexSection.ImmediateDominators));
        writer.Commit(Fingerprint(), 1, 7);
        Should.Throw<InvalidOperationException>(() => writer.GetSection<int>(IndexSection.ImmediateDominators));
        Should.Throw<ArgumentException>(() => MappedIndexFileWriter.Create(path, IndexCacheSigner.None, [.. Plan(1), .. Plan(1)], Ct));
    }

    [Fact]
    public void A_write_that_fails_before_the_header_never_leaves_a_valid_file()
    {
        // Review M10: header last. The temporary file has every section and no header when the hook runs; a failure there
        // keeps the previous file and leaves no temporary file.
        var path = Write(semanticsVersion: 7, dominators: [-2], retained: [8]);
        var before = File.ReadAllBytes(path);
        byte[]? headerless = null;
        IndexCacheWriter.BeforeHeaderForTesting = temp =>
        {
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                headerless = new byte[8];
                stream.ReadExactly(headerless);
            }

            throw new IOException("disk full");
        };
        try
        {
            using var writer = MappedIndexFileWriter.Create(path, IndexCacheSigner.None, Plan(2), Ct);
            Should.Throw<IOException>(() => writer.Commit(Fingerprint(), 0x42, 7));
        }
        finally
        {
            IndexCacheWriter.BeforeHeaderForTesting = null;
        }

        headerless.ShouldBe(new byte[8], "no magic before the header is written");
        File.ReadAllBytes(path).ShouldBe(before);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Replacing_a_file_that_is_open_keeps_it_and_leaves_no_temporary_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows refuses to replace an open file; Linux and macOS allow it, and a mapping keeps the old file.");
        var path = Write(semanticsVersion: 7, dominators: [-2], retained: [8]);
        var opened = IndexCacheFile.Open(path, IndexCacheSigner.None, s_dominators, 0x42, Fingerprint(), MemoryMode.Mapped, Ct);
        using (opened.File)
        {
            using var writer = MappedIndexFileWriter.Create(path, IndexCacheSigner.None, Plan(1), Ct);

            Should.Throw<IndexCacheInUseException>(() => writer.Commit(Fingerprint(), 0x42, 7));
        }

        IndexCacheFile.Open(path, IndexCacheSigner.None, s_dominators, 0x42, Fingerprint(), MemoryMode.InMemory, Ct).Success.ShouldBeTrue();
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Base_caches_and_reverse_sidecars_keep_semantics_version_zero() =>
        (IndexFileLayout.Base.SemanticsVersion, IndexFileLayout.ReverseReferences.SemanticsVersion).ShouldBe((0, 0));

    private static PlannedSection[] Plan(int count) =>
    [
        new(IndexSection.ImmediateDominators, sizeof(int), count),
        new(IndexSection.RetainedSizes, sizeof(ulong), count),
    ];

    private string Write(int semanticsVersion, int[] dominators, ulong[] retained)
    {
        var path = Path.Combine(_directory, "app.dmp.dom.hsidx");
        using var writer = MappedIndexFileWriter.Create(path, IndexCacheSigner.None, Plan(dominators.Length), Ct);
        dominators.CopyTo(writer.GetSection<int>(IndexSection.ImmediateDominators));
        retained.CopyTo(writer.GetSection<ulong>(IndexSection.RetainedSizes));
        writer.Commit(Fingerprint(), 0x42, semanticsVersion);
        return path;
    }

    private SnapshotFingerprint Fingerprint() => SnapshotFingerprint.Compute(_snapshot);
}
