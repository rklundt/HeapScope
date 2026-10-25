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
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Storage;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Worker.Tests;

/// <summary>Sprint 2.1 review M6: every path of the reverse index (build, sidecar, mapping, failures) and the shared computation.</summary>
public sealed class ReverseIndexBuilderTests : IDisposable
{
    private static readonly SnapshotFingerprint s_fingerprint = new(1, 2, 3);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly HeapGraph _graph = SyntheticGraphs.Random(new RandomGraphOptions(2_000, 3, TypeCount: 10, RootCount: 5, Seed: 3));
    private readonly ReverseIndexBuilder _builder = new(NullLogger.Instance);

    public ReverseIndexBuilderTests() => Directory.CreateDirectory(_folder);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Sidecar => Path.Combine(_folder, "app.dmp.rev.hsidx");

    [Fact]
    public void Without_an_index_cache_the_index_is_built_in_memory_and_not_saved()
    {
        var index = _builder.OpenOrBuild(_graph, new ReverseIndexTarget(null, 0, s_fingerprint, MemoryMode.InMemory), null, Ct);

        using var _ = index.References;
        (index.FromSidecar, index.Mode, index.SidecarWritten).ShouldBe((false, MemoryMode.InMemory, false));
        AssertMatchesGraph(index.References);
    }

    [Fact]
    public void A_built_index_is_saved_and_the_next_open_reads_it_back()
    {
        var target = new ReverseIndexTarget(Sidecar, 0x42, s_fingerprint, MemoryMode.InMemory);
        var progress = new List<JobProgress>();

        var built = _builder.OpenOrBuild(_graph, target, new InlineProgress(progress.Add), Ct);
        var reopened = _builder.OpenOrBuild(_graph, target, null, Ct);

        using (built.References)
        using (reopened.References)
        {
            (built.FromSidecar, built.SidecarWritten).ShouldBe((false, true));
            (reopened.FromSidecar, reopened.Mode).ShouldBe((true, MemoryMode.InMemory));
            AssertMatchesGraph(reopened.References);
        }

        progress.ShouldContain(p => p.Phase == "Indexing incoming references" && p.Total == _graph.ObjectCount);
    }

    [Fact]
    public void A_mapped_index_is_mapped_from_the_sidecar_it_just_wrote()
    {
        var index = _builder.OpenOrBuild(_graph, new ReverseIndexTarget(Sidecar, 0x42, s_fingerprint, MemoryMode.Mapped), null, Ct);

        using var _ = index.References;
        (index.FromSidecar, index.Mode, index.SidecarWritten).ShouldBe((false, MemoryMode.Mapped, true));
        AssertMatchesGraph(index.References);
        Should.Throw<IOException>(() => File.Delete(Sidecar), "the mapping holds the sidecar open");
    }

    [Fact]
    public void The_sidecar_built_in_place_equals_one_written_from_managed_arrays()
    {
        // Sprint 2.3 (2.1 review M2): the index goes straight into the mapped file; the file is the one 2.1 wrote.
        _builder.OpenOrBuild(_graph, new ReverseIndexTarget(Sidecar, 0x42, s_fingerprint, MemoryMode.InMemory), null, Ct).References.Dispose();
        using var expected = ReverseReferences.Build(_graph, cancellationToken: Ct);
        var streamed = Path.Combine(_folder, "streamed.rev.hsidx");
        using (var writer = IndexCacheFileWriter.Create(streamed, cancellationToken: Ct))
        {
            writer.WriteSection(IndexSection.InEdgeStarts, expected.InEdgeStarts.Span);
            writer.WriteSection(IndexSection.InEdges, expected.InEdges.Span);
            writer.Commit(s_fingerprint, 0x42);
        }

        File.ReadAllBytes(Sidecar).ShouldBe(File.ReadAllBytes(streamed));
    }

    [Fact]
    public void A_sidecar_that_cannot_be_written_leaves_the_index_in_memory()
    {
        // A folder where the sidecar would go: the rename onto it fails.
        Directory.CreateDirectory(Sidecar);

        var index = _builder.OpenOrBuild(_graph, new ReverseIndexTarget(Sidecar, 0x42, s_fingerprint, MemoryMode.Mapped), null, Ct);

        using var _ = index.References;
        (index.FromSidecar, index.Mode, index.SidecarWritten).ShouldBe((false, MemoryMode.InMemory, false));
        AssertMatchesGraph(index.References);
        Directory.EnumerateFiles(_folder, "*.tmp").ShouldBeEmpty("the temporary file is removed");
    }

    [Theory]
    [InlineData(0x43UL, 0L)]
    [InlineData(0x42UL, 4097L)]
    public void A_stale_or_damaged_sidecar_is_rebuilt_and_rewritten(ulong writtenFor, long damageAt)
    {
        _builder.OpenOrBuild(_graph, new ReverseIndexTarget(Sidecar, writtenFor, s_fingerprint, MemoryMode.InMemory), null, Ct).References.Dispose();
        if (damageAt > 0)
        {
            using var stream = new FileStream(Sidecar, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = damageAt;
            stream.WriteByte(0x7F);
        }

        var target = new ReverseIndexTarget(Sidecar, 0x42, s_fingerprint, MemoryMode.InMemory);
        var rebuilt = _builder.OpenOrBuild(_graph, target, null, Ct);
        var reopened = _builder.OpenOrBuild(_graph, target, null, Ct);

        using (rebuilt.References)
        using (reopened.References)
        {
            rebuilt.FromSidecar.ShouldBeFalse();
            reopened.FromSidecar.ShouldBeTrue("the rebuilt index was written over the bad sidecar");
            AssertMatchesGraph(reopened.References);
        }
    }

    [Theory]
    [InlineData(100L, MemoryMode.InMemory, null, true, 1000L, MemoryMode.InMemory)]
    [InlineData(100L, MemoryMode.InMemory, null, false, 1000L, MemoryMode.InMemory)]
    [InlineData(100L, MemoryMode.Mapped, null, true, 1000L, MemoryMode.Mapped)]
    [InlineData(100L, MemoryMode.InMemory, MemoryMode.Mapped, true, 1000L, MemoryMode.Mapped)]
    [InlineData(100L, MemoryMode.InMemory, MemoryMode.Mapped, false, 1000L, MemoryMode.InMemory)]
    [InlineData(100L, MemoryMode.Mapped, MemoryMode.InMemory, true, 1000L, MemoryMode.InMemory)]
    [InlineData(100L, MemoryMode.InMemory, null, true, 1_950L, MemoryMode.Mapped)]
    [InlineData(600L, MemoryMode.InMemory, null, true, 0L, MemoryMode.Mapped)]
    public void The_mode_follows_the_graph_the_forced_mode_and_what_is_left_of_the_budget(
        long indexBytes, MemoryMode graphMode, MemoryMode? forced, bool hasSidecar, long committed, MemoryMode expected)
    {
        // 4,000 bytes of RAM: the worker's share is 2,000; 500 are available on the machine.
        var budget = new MemoryBudget(TotalPhysicalBytes: 4_000, AvailableBytes: 500);

        ReverseIndexBuilder.ChooseMode(indexBytes, graphMode, forced, hasSidecar, budget, committed).ShouldBe(expected);
    }

    [Fact]
    public async Task One_computation_is_shared_by_every_caller_and_its_progress_reaches_each_waiter()
    {
        var derived = new DerivedData();
        var started = 0;
        var gate = new TaskCompletionSource<ReverseIndex>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ReverseIndex> Compute(CancellationToken _)
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        }

        var first = new List<JobProgress>();
        var late = new List<JobProgress>();
        using (derived.ReverseProgress.Subscribe(new InlineProgress(first.Add)))
        {
            var a = derived.GetOrStartReverseReferences(Compute);
            derived.ReverseProgress.Report(new JobProgress("Indexing incoming references", 1, 4));
            using (derived.ReverseProgress.Subscribe(new InlineProgress(late.Add)))
            {
                var b = derived.GetOrStartReverseReferences(Compute);
                derived.ReverseProgress.Report(new JobProgress("Indexing incoming references", 2, 4));
                b.ShouldBeSameAs(a);
            }

            derived.ReverseProgress.Report(new JobProgress("Indexing incoming references", 3, 4));
            gate.SetResult(new ReverseIndex(ReverseReferences.Build(_graph, cancellationToken: Ct), false, MemoryMode.InMemory, false));
            (await a).References.Dispose();
        }

        started.ShouldBe(1);
        first.Select(p => p.Completed).ShouldBe([1L, 2L, 3L]);
        late.Select(p => p.Completed).ShouldBe([1L, 2L], "the latest report on subscribing, then until it stopped waiting");
    }

    [Fact]
    public async Task The_root_path_tree_is_built_once_for_every_caller()
    {
        // Review M4: the 2.2 slot shares like the 2.1 one.
        var derived = new DerivedData();
        var builds = 0;
        var gate = new TaskCompletionSource<HeapScope.Analysis.Retention.RootPathTree>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HeapScope.Analysis.Retention.RootPathTree> Compute(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            return gate.Task;
        }

        var reports = new List<JobProgress>();
        using (derived.RootPathProgress.Subscribe(new InlineProgress(reports.Add)))
        {
            var a = derived.GetOrStartRootPaths(Compute);
            var b = derived.GetOrStartRootPaths(Compute);
            derived.RootPathProgress.Report(new JobProgress("Finding every object's shortest path from a root", 1, 2));
            gate.SetResult(HeapScope.Analysis.Retention.RootPathTree.Build(_graph, cancellationToken: Ct));
            (await a).ShouldBeSameAs(await b);
        }

        builds.ShouldBe(1);
        reports.ShouldHaveSingleItem();
        derived.Close();
        _ = derived.GetOrStartRootPaths(Compute);
        builds.ShouldBe(2, "closing drops the tree, so the next caller builds again");
    }

    public void Dispose()
    {
        _graph.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void AssertMatchesGraph(ReverseReferences reverse)
    {
        using var expected = ReverseReferences.Build(_graph, cancellationToken: Ct);
        reverse.InEdgeStarts.Span.SequenceEqual(expected.InEdgeStarts.Span).ShouldBeTrue();
        reverse.InEdges.Span.SequenceEqual(expected.InEdges.Span).ShouldBeTrue();
    }

    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }
}
