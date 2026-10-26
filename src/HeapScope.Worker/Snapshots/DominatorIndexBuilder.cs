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

using System.Diagnostics;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core.Graph;
using HeapScope.Storage;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Snapshots;

/// <summary>A snapshot's dominators and how they were obtained (sprint 2.3).</summary>
/// <param name="FromSidecar">Opened from the sidecar rather than computed.</param>
/// <param name="Saved">Saved in the sidecar (opened from it, or computed into it), so the next open reuses them.</param>
/// <param name="Reachable">Objects a root reaches.</param>
/// <param name="Seconds">Time to open or compute them, the reverse references index included.</param>
/// <param name="UnreachableBytes">Bytes of the objects no root reaches (for a gcdump, survivors whose root it did not record).</param>
internal sealed record DominatorIndex(Dominators Dominators, bool FromSidecar, MemoryMode Mode, bool Saved, long Reachable, double Seconds, ulong UnreachableBytes = 0)
{
    /// <summary>This index with its reachable count and the bytes no root reaches, counted in one pass over the dominators.</summary>
    public DominatorIndex WithUnreachable(HeapGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        long reachable = 0;
        ulong unreachableBytes = 0;
        var idom = Dominators.ImmediateDominators.Span;
        var sizes = graph.Sizes.Span;
        for (var i = 0; i < idom.Length; i++)
        {
            if (idom[i] != DominatorTree.Unreachable)
            {
                reachable++;
            }
            else
            {
                unreachableBytes += sizes[i] == HeapGraph.LargeSizeMarker ? graph.GetSize(i) : sizes[i];
            }
        }

        return this with { Reachable = reachable, UnreachableBytes = unreachableBytes };
    }
}

/// <summary>Where a snapshot's dominators are kept, and the snapshot's memory mode (sprint 2.3).</summary>
/// <param name="Sidecar">The sidecar path; null when the snapshot has no index cache (the dominators then stay in memory).</param>
/// <param name="BaseIdentity">The identity of the index cache the sidecar extends.</param>
/// <param name="GraphMode">The graph's memory mode.</param>
/// <param name="Forced">The memory mode the open request forced, if any.</param>
internal sealed record DominatorTarget(string? Sidecar, ulong BaseIdentity, SnapshotFingerprint Fingerprint, MemoryMode GraphMode, MemoryMode? Forced);

/// <summary>
/// Opens a snapshot's dominators from their sidecar or computes them (sprint 2.3, ARCHITECTURE.md 6.4; split from the
/// snapshot store for its tests, review M9). With a sidecar to write they are computed straight into it (decision 3), the
/// working arrays are returned to the OS, and the file is opened in the mode chosen then, with the memory committed at
/// that moment; when the write fails, a sidecar another process just wrote is used, and only then are they computed in
/// managed arrays. Every computation reserves its memory first (decision 4, review M1), and every exit collects the
/// working arrays (review M3).
/// </summary>
internal sealed class DominatorIndexBuilder(ILogger logger, MemoryReservations reservations)
{
    /// <summary>The dominators sidecar's layout under this build's rules (decision 2).</summary>
    public static IndexFileLayout Layout { get; } = IndexFileLayout.ForDominators(DominatorTree.SemanticsVersion);

    private const string What = "Computing retained sizes";

    /// <summary>What computing needs before anything is allocated: the working arrays, and the outputs when managed.</summary>
    public static long NeededBytes(int objectCount, bool managedOutputs) =>
        ((long)DominatorTree.WorkingBytesPerObject * objectCount) + (managedOutputs ? (long)DominatorTree.KeptBytesPerObject * objectCount : 0);

    /// <summary>The mode for the kept columns: as the reverse index chooses (review M3 of 2.1), with the memory committed now.</summary>
    public static MemoryMode ChooseMode(int objectCount, DominatorTarget target, MemoryBudget budget, long committedBytes) =>
        ReverseIndexBuilder.ChooseMode((long)DominatorTree.KeptBytesPerObject * objectCount, target.GraphMode, target.Forced, target.Sidecar is not null, budget, committedBytes);

    /// <summary>The dominators from their sidecar, or null when it is missing, stale, from older rules or damaged.</summary>
    public DominatorIndex? TryOpen(HeapGraph graph, DominatorTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Sidecar is null)
        {
            return null;
        }

        var mode = ChooseMode(graph.ObjectCount, target, MemoryBudget.Current(), CommittedBytes() + reservations.Reserved);
        return TryOpenFile(graph, target, mode, cancellationToken) is { } opened
            ? new DominatorIndex(opened, FromSidecar: true, mode, Saved: true, 0, 0).WithUnreachable(graph)
            : null;
    }

    /// <summary>
    /// Computes the dominators of <paramref name="graph"/> from its reverse references, straight into the sidecar when there
    /// is one. Throws <see cref="InsufficientMemoryException"/> before allocating anything when they would not fit.
    /// </summary>
    public DominatorIndex Compute(HeapGraph graph, ReverseReferences reverse, DominatorTarget target, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(reverse);
        ArgumentNullException.ThrowIfNull(target);
        var phases = new DominatorPhases(progress);
        var hasCache = target.Sidecar is not null;
        if (target.Sidecar is { } sidecar)
        {
            var written = false;
            var reachable = 0;
            using (reservations.Reserve(NeededBytes(graph.ObjectCount, managedOutputs: false), What, target.GraphMode, hasCache))
            {
                try
                {
                    written = TryComputeIntoSidecar(sidecar, target, graph, reverse, phases, cancellationToken, out reachable);
                }
                finally
                {
                    MemoryReservations.Collect();
                }
            }

            // The working arrays are gone: choose the kept columns' mode with what the worker commits now.
            var mode = ChooseMode(graph.ObjectCount, target, MemoryBudget.Current(), CommittedBytes() + reservations.Reserved);
            if (written)
            {
                if (TryOpenFile(graph, target, mode, cancellationToken) is { } computed)
                {
                    return new DominatorIndex(computed, FromSidecar: false, mode, Saved: true, reachable, 0).WithUnreachable(graph);
                }

                logger.LogWarning("Dominators sidecar {Path} could not be opened after writing it; they are computed in memory", sidecar);
            }
            else if (TryOpenFile(graph, target, mode, cancellationToken) is { } other)
            {
                // Another process (the UI and the CLI on one dump) wrote the same sidecar meanwhile (review M1).
                logger.LogInformation("Dominators sidecar {Path} could not be written, but another process wrote it; using that one", sidecar);
                return new DominatorIndex(other, FromSidecar: true, mode, Saved: true, 0, 0).WithUnreachable(graph);
            }
        }

        // No sidecar, or it failed: managed outputs, checked again with them counted (review M1).
        using (reservations.Reserve(NeededBytes(graph.ObjectCount, managedOutputs: true), What, target.GraphMode, hasCache))
        {
            try
            {
                var idom = GC.AllocateUninitializedArray<int>(graph.ObjectCount);
                var retained = GC.AllocateUninitializedArray<ulong>(graph.ObjectCount);
                var reachable = DominatorTree.Compute(graph, reverse, idom, retained, phases, cancellationToken);
                var starts = new int[graph.ObjectCount + 2];
                var children = new int[graph.ObjectCount];
                phases.Children();
                DominatorTree.BuildChildren(idom, retained, starts, children, cancellationToken);
                var dominators = Dominators.FromColumns(graph, Column<int>.FromArray(idom), Column<ulong>.FromArray(retained), Column<int>.FromArray(starts), Column<int>.FromArray(children), owner: null);
                return new DominatorIndex(dominators, FromSidecar: false, MemoryMode.InMemory, Saved: false, reachable, 0).WithUnreachable(graph);
            }
            finally
            {
                MemoryReservations.Collect();
            }
        }
    }

    /// <summary>Computes the dominators straight into their sidecar file; false when the file cannot be written.</summary>
    private bool TryComputeIntoSidecar(string sidecar, DominatorTarget target, HeapGraph graph, ReverseReferences reverse, DominatorPhases phases, CancellationToken cancellationToken, out int reachable)
    {
        reachable = 0;
        try
        {
            using var writer = MappedIndexFileWriter.Create(
                sidecar,
                [
                    new(IndexSection.ImmediateDominators, sizeof(int), graph.ObjectCount),
                    new(IndexSection.RetainedSizes, sizeof(ulong), graph.ObjectCount),
                    new(IndexSection.DominatorChildStarts, sizeof(int), graph.ObjectCount + 2),
                    new(IndexSection.DominatorChildren, sizeof(int), graph.ObjectCount),
                ],
                cancellationToken);
            var idom = writer.GetSection<int>(IndexSection.ImmediateDominators);
            var retained = writer.GetSection<ulong>(IndexSection.RetainedSizes);
            reachable = DominatorTree.Compute(graph, reverse, idom, retained, phases, cancellationToken);

            // The tree's children (sprint 2.4, ADR-027), sorted by retained size, so expanding a node is a slice read.
            phases.Children();
            DominatorTree.BuildChildren(idom, retained, writer.GetSection<int>(IndexSection.DominatorChildStarts), writer.GetSection<int>(IndexSection.DominatorChildren), cancellationToken);
            phases.Saving();
            writer.Commit(target.Fingerprint, target.BaseIdentity, Layout.SemanticsVersion);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Dominators sidecar {Path} could not be written", sidecar);
            return false;
        }
    }

    private Dominators? TryOpenFile(HeapGraph graph, DominatorTarget target, MemoryMode mode, CancellationToken cancellationToken)
    {
        var sidecar = target.Sidecar!;
        var opened = IndexCacheFile.Open(sidecar, Layout, target.BaseIdentity, target.Fingerprint, mode, cancellationToken);
        if (!opened.Success)
        {
            if (opened.Reason != IndexCacheInvalidReason.Missing)
            {
                logger.LogInformation("Dominators sidecar {Path} not used: {Reason} ({Detail})", sidecar, opened.Reason, opened.Detail);
            }

            return null;
        }

        var file = opened.File!;
        try
        {
            if (file.TryGetSection<int>(IndexSection.ImmediateDominators, out var idom) && file.TryGetSection<ulong>(IndexSection.RetainedSizes, out var retained)
                && file.TryGetSection<int>(IndexSection.DominatorChildStarts, out var starts) && file.TryGetSection<int>(IndexSection.DominatorChildren, out var children))
            {
                return Dominators.FromColumns(graph, idom, retained, starts, children, file);
            }
        }
        catch (ArgumentException ex)
        {
            logger.LogInformation("Dominators sidecar {Path} not used: {Detail}", sidecar, ex.Message);
        }

        file.Dispose();
        return null;
    }

    private static long CommittedBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /// <summary>Dominator phases as job progress.</summary>
    private sealed class DominatorPhases(IProgress<JobProgress>? target) : IProgress<DominatorProgress>
    {
        public void Report(DominatorProgress value) => target?.Report(new JobProgress(
            value.Phase switch
            {
                DominatorPhase.Numbering => "Numbering objects from the roots",
                DominatorPhase.Dominators => "Finding immediate dominators",
                _ => "Summing retained sizes",
            },
            value.Done,
            value.Total));

        public void Children() => target?.Report(new JobProgress("Ordering the dominator tree", 0, null));

        public void Saving() => target?.Report(new JobProgress("Saving retained sizes", 0, null));
    }
}
