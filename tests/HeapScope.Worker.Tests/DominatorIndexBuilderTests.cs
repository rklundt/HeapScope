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

using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Storage;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Worker.Tests;

/// <summary>Sprint 2.3 (review M9): the dominators opened from or computed into their sidecar, and the memory check.</summary>
public sealed class DominatorIndexBuilderTests : IDisposable
{
    private static readonly SnapshotFingerprint s_fingerprint = new(1, 2, 3);
    private static readonly MemoryBudget s_roomy = new(TotalPhysicalBytes: 64L << 30, AvailableBytes: 32L << 30);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly HeapGraph _graph = SyntheticGraphs.Random(new RandomGraphOptions(3_000, 3, TypeCount: 10, RootCount: 20, Seed: 5));
    private readonly ReverseReferences _reverse;
    private readonly Dominators _expected;

    public DominatorIndexBuilderTests()
    {
        Directory.CreateDirectory(_folder);
        _reverse = ReverseReferences.Build(_graph, cancellationToken: Ct);
        _expected = Dominators.Compute(_graph, _reverse, cancellationToken: Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Sidecar => Path.Combine(_folder, "app.dmp.dom.hsidx");

    [Fact]
    public void Without_an_index_cache_they_are_computed_in_memory_and_not_saved()
    {
        var builder = Builder();
        var target = Target(sidecar: null, MemoryMode.Mapped);

        builder.TryOpen(_graph, target, Ct).ShouldBeNull();
        var index = builder.Compute(_graph, _reverse, target, null, Ct);

        using var _ = index.Dominators;
        (index.FromSidecar, index.Saved, index.Mode).ShouldBe((false, false, MemoryMode.InMemory));
        AssertExpected(index);
    }

    [Theory]
    [InlineData(MemoryMode.InMemory)]
    [InlineData(MemoryMode.Mapped)]
    public void Computed_into_the_sidecar_they_are_opened_in_the_mode_and_reopened_next_time(MemoryMode mode)
    {
        var builder = Builder();
        var target = Target(Sidecar, mode, forced: mode);
        var progress = new List<JobProgress>();

        var computed = builder.Compute(_graph, _reverse, target, new InlineProgress(progress.Add), Ct);
        var reopened = builder.TryOpen(_graph, target, Ct).ShouldNotBeNull();

        using (computed.Dominators)
        using (reopened.Dominators)
        {
            (computed.FromSidecar, computed.Saved, computed.Mode).ShouldBe((false, true, mode));
            (reopened.FromSidecar, reopened.Mode, reopened.Reachable).ShouldBe((true, mode, computed.Reachable));
            AssertExpected(computed);
            AssertExpected(reopened);
        }

        progress.Select(p => p.Phase).ShouldContain("Finding immediate dominators");
        progress.Select(p => p.Phase).ShouldContain("Saving retained sizes");
    }

    [Fact]
    public void A_sidecar_computed_under_other_rules_is_not_used()
    {
        using (var writer = MappedIndexFileWriter.Create(Sidecar, IndexCacheSigner.None, [new(IndexSection.ImmediateDominators, 4, _graph.ObjectCount), new(IndexSection.RetainedSizes, 8, _graph.ObjectCount)], Ct))
        {
            writer.Commit(s_fingerprint, 0x42, DominatorTree.SemanticsVersion + 1);
        }

        var builder = Builder();
        var target = Target(Sidecar, MemoryMode.InMemory);

        builder.TryOpen(_graph, target, Ct).ShouldBeNull();
        var rebuilt = builder.Compute(_graph, _reverse, target, null, Ct);
        using (rebuilt.Dominators)
        {
            rebuilt.Saved.ShouldBeTrue("written over the stale sidecar");
        }

        using var reopened = builder.TryOpen(_graph, target, Ct).ShouldNotBeNull().Dominators;
        reopened.ImmediateDominators.Span.ToArray().ShouldBe(_expected.ImmediateDominators.Span.ToArray());
    }

    [Fact]
    public void A_sidecar_that_cannot_be_written_falls_back_to_memory_and_leaves_no_temporary_file()
    {
        // A folder where the sidecar would go: the rename onto it fails.
        Directory.CreateDirectory(Sidecar);
        var builder = Builder();

        var index = builder.Compute(_graph, _reverse, Target(Sidecar, MemoryMode.Mapped), null, Ct);

        using var _ = index.Dominators;
        (index.FromSidecar, index.Saved, index.Mode).ShouldBe((false, false, MemoryMode.InMemory));
        AssertExpected(index);
        Directory.EnumerateFiles(_folder, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Too_little_memory_is_refused_before_anything_is_computed()
    {
        var tiny = new MemoryReservations(() => new MemoryBudget(TotalPhysicalBytes: 10_000, AvailableBytes: 10_000), () => 0);
        var builder = new DominatorIndexBuilder(NullLogger.Instance, tiny, IndexCacheSigner.None);

        var refused = Should.Throw<InsufficientMemoryException>(() => builder.Compute(_graph, _reverse, Target(Sidecar, MemoryMode.Mapped), null, Ct));

        refused.Message.ShouldStartWith("Computing retained sizes needs about");
        File.Exists(Sidecar).ShouldBeFalse();
        tiny.Reserved.ShouldBe(0L);
    }

    [Fact]
    public void Reservations_count_against_each_other_until_released()
    {
        // Review M2: two computations at once cannot both pass against the same reading.
        var reservations = new MemoryReservations(() => new MemoryBudget(TotalPhysicalBytes: 4_000, AvailableBytes: 2_000), () => 500);

        var first = reservations.Reserve(1_000, "First", MemoryMode.InMemory, hasCache: true);
        Should.Throw<InsufficientMemoryException>(() => reservations.Reserve(1_000, "Second", MemoryMode.InMemory, hasCache: true));
        first.Dispose();
        first.Dispose();
        using var second = reservations.Reserve(1_000, "Second", MemoryMode.InMemory, hasCache: true);

        reservations.Reserved.ShouldBe(1_000L);
    }

    [Theory]
    [InlineData(1_000L, 0L, MemoryMode.InMemory, true, null)]
    [InlineData(1_200L, 900L, MemoryMode.InMemory, true, "open this one mapped")]
    [InlineData(1_600L, 0L, MemoryMode.InMemory, true, "open this one mapped")]
    [InlineData(1_600L, 0L, MemoryMode.Mapped, true, "other applications")]
    [InlineData(1_600L, 0L, MemoryMode.InMemory, false, "other applications")]
    public void A_refusal_says_what_is_needed_and_a_next_step_that_fits_the_snapshot(long needed, long committed, MemoryMode mode, bool hasCache, string? next)
    {
        // A 4,000 byte machine: the share is 2,000; 1,500 bytes are available. Only an in-memory snapshot with an index
        // cache can be reopened mapped (review M11).
        var budget = new MemoryBudget(TotalPhysicalBytes: 4_000, AvailableBytes: 1_500);

        var refusal = MemoryReservations.Refusal(needed, "Computing retained sizes", budget, committed, mode, hasCache);

        if (next is null)
        {
            refusal.ShouldBeNull();
        }
        else
        {
            refusal.ShouldNotBeNull().ShouldStartWith("Computing retained sizes needs about");
            refusal.ShouldContain(next);
        }
    }

    [Fact]
    public void Retained_sizes_carry_a_note_for_incomplete_snapshots_and_a_gcdumps_unrooted_survivors()
    {
        // Review M6: both can overstate retained sizes, so the front ends say so beside them.
        var index = new DominatorIndex(_expected, FromSidecar: false, MemoryMode.InMemory, Saved: true, Reachable: 90, Seconds: 1, UnreachableBytes: 3 * 1024 * 1024);

        var full = Rpc.WorkerService.Info(index, Core.SnapshotKind.FullDump, 100, completeGraph: true);
        var gcdump = Rpc.WorkerService.Info(index, Core.SnapshotKind.GcDump, 100, completeGraph: true);
        var truncated = Rpc.WorkerService.Info(index, Core.SnapshotKind.FullDump, 100, completeGraph: false);

        full.Note.ShouldBeNull("garbage in a full dump retains nothing by definition");
        gcdump.Note.ShouldNotBeNull().ShouldContain("10 objects (3.0 MB) of this gcdump");
        (truncated.CompleteGraph, truncated.Note).ShouldBe((false, "This snapshot is incomplete, so some references are missing and retained sizes may be overstated."));
    }

    public void Dispose()
    {
        _expected.Dispose();
        _reverse.Dispose();
        _graph.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DominatorIndexBuilder Builder() => new(NullLogger.Instance, new MemoryReservations(() => s_roomy, () => 0), IndexCacheSigner.None);

    private static DominatorTarget Target(string? sidecar, MemoryMode graphMode, MemoryMode? forced = null) =>
        new(sidecar, 0x42, s_fingerprint, graphMode, forced);

    private void AssertExpected(DominatorIndex index)
    {
        var unreachable = Enumerable.Range(0, _graph.ObjectCount).Where(i => _expected.ImmediateDominator(i) == DominatorTree.Unreachable);
        index.UnreachableBytes.ShouldBe((ulong)unreachable.Sum(i => (long)_graph.GetSize(i)));
        index.Dominators.ImmediateDominators.Span.ToArray().ShouldBe(_expected.ImmediateDominators.Span.ToArray());
        index.Dominators.RetainedSizes.Span.ToArray().ShouldBe(_expected.RetainedSizes.Span.ToArray());
        index.Reachable.ShouldBe(_expected.ImmediateDominators.Span.ToArray().LongCount(d => d != DominatorTree.Unreachable));
    }

    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }
}
