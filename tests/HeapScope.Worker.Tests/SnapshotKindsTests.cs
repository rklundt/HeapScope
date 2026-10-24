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
using HeapScope.Core.Synthetic;
using HeapScope.Loaders.ClrMd;
using HeapScope.Loaders.GcDump;
using HeapScope.Storage;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Tests;

/// <summary>What the worker does per snapshot kind lives in one place (sprint 1.9).</summary>
public sealed class SnapshotKindsTests : IDisposable
{
    private readonly SnapshotKinds _kinds = new(new WorkerOptions("pipe", null));
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-kinds-").FullName;

    [Fact]
    public void Each_kind_has_its_loader_and_the_loader_s_version()
    {
        var full = _kinds.For(SnapshotKind.FullDump);
        var gcdump = _kinds.For(SnapshotKind.GcDump);

        full.CreateLoader().ShouldBeOfType<ClrMdDumpLoader>().Kind.ShouldBe(SnapshotKind.FullDump);
        gcdump.CreateLoader().ShouldBeOfType<GcDumpLoader>().Kind.ShouldBe(SnapshotKind.GcDump);
        full.LoaderVersion.ShouldBe(full.CreateLoader().Version);
        gcdump.LoaderVersion.ShouldBe(gcdump.CreateLoader().Version);
    }

    [Fact]
    public void A_full_dump_gets_a_reader_and_a_gcdump_only_its_graph()
    {
        var full = _kinds.For(SnapshotKind.FullDump).Open(new SnapshotInfo(SnapshotKind.FullDump, "a.dmp"), SnapshotCapabilities.FullDump, SyntheticGraphs.Chain(3), "a.dmp", () => { });
        var gcdump = _kinds.For(SnapshotKind.GcDump).Open(new SnapshotInfo(SnapshotKind.GcDump, "a.gcdump"), SnapshotCapabilities.ObjectGraph, SyntheticGraphs.Chain(3), "a.gcdump", () => { });
        using (full)
        using (gcdump)
        {
            full.ShouldBeOfType<DumpSnapshot>().TryGetService<IObjectReader>(out _).ShouldBeTrue();
            gcdump.ShouldBeOfType<GraphSnapshot>().TryGetService<IObjectReader>(out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(SnapshotKind.FullDump, SnapshotCapabilities.FullDump, new string[0])]
    [InlineData(SnapshotKind.FullDump, SnapshotCapabilities.ObjectGraph, new[] { "Parts of the managed heap could not be read" })]
    [InlineData(SnapshotKind.GcDump, SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.CompleteGraph, new[] { "A gcdump holds" })]
    [InlineData(SnapshotKind.GcDump, SnapshotCapabilities.ObjectGraph, new[] { "A gcdump holds", "This gcdump is sampled or incomplete" })]
    public void Each_kind_adds_its_own_notes(SnapshotKind kind, SnapshotCapabilities capabilities, string[] starts)
    {
        var notes = new List<string>();

        _kinds.For(kind).AddNotes(Metadata(kind, capabilities), notes);

        notes.Count.ShouldBe(starts.Length);
        for (var i = 0; i < starts.Length; i++)
        {
            notes[i].ShouldStartWith(starts[i]);
        }
    }

    [Fact]
    public void Missing_facts_and_cross_kind_names_are_explained_per_kind()
    {
        SnapshotKinds.Describe(SnapshotKind.FullDump).ExplainMissingFact("Sparse arrays").ShouldContain("Rebuild");
        SnapshotKinds.Describe(SnapshotKind.GcDump).ExplainMissingFact("Sparse arrays").ShouldContain("a gcdump does not record");

        SnapshotKinds.Describe(SnapshotKind.FullDump).NamesWhenComparedWith(SnapshotKind.GcDump)!("A<B>+C[]").ShouldBe("C<B>[]");
        SnapshotKinds.Describe(SnapshotKind.FullDump).NamesWhenComparedWith(SnapshotKind.FullDump).ShouldBeNull();
        SnapshotKinds.Describe(SnapshotKind.GcDump).NamesWhenComparedWith(SnapshotKind.FullDump).ShouldBeNull();
    }

    [Fact]
    public void An_unknown_kind_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => _kinds.For((SnapshotKind)99));

    [Fact]
    public void A_file_of_neither_kind_is_refused_by_its_content()
    {
        var path = Path.Combine(_folder, "notes.dmp");
        File.WriteAllText(path, "not a snapshot");

        Should.Throw<SnapshotFormatException>(() => _kinds.ForFile(path)).Message.ShouldContain("neither a full memory dump nor a gcdump");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static IndexCacheMetadata Metadata(SnapshotKind kind, SnapshotCapabilities capabilities) =>
        new(new SnapshotInfo(kind, "x"), capabilities, "test");
}
