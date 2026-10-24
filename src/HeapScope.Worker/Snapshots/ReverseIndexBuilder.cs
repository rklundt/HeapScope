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
using HeapScope.Contracts;
using HeapScope.Core.Graph;
using HeapScope.Storage;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Snapshots;

/// <summary>A snapshot's reverse references index and how it was obtained (sprint 2.1).</summary>
/// <param name="FromSidecar">Opened from the sidecar next to the index cache rather than built.</param>
/// <param name="SidecarWritten">The index is saved in its sidecar (opened from it, or built and written), so the next open reuses it.</param>
internal sealed record ReverseIndex(ReverseReferences References, bool FromSidecar, MemoryMode Mode, bool SidecarWritten);

/// <summary>Where a snapshot's reverse index is kept and in which memory mode (sprint 2.1).</summary>
/// <param name="Sidecar">The sidecar path; null when the snapshot has no index cache (the index then stays in memory).</param>
/// <param name="BaseIdentity">The identity of the index cache the sidecar extends.</param>
internal sealed record ReverseIndexTarget(string? Sidecar, ulong BaseIdentity, SnapshotFingerprint Fingerprint, MemoryMode Mode);

/// <summary>
/// Opens a snapshot's reverse references index from its sidecar or builds it from the graph (sprint 2.1, ARCHITECTURE.md
/// 6.4 and 7). A built index is written as a sidecar when there is one to write; to go mapped, the sidecar is mapped first
/// and the built arrays are dropped only once that worked (review M2), and the collection that returns them is neither
/// aggressive nor compacting, so no live graph array is copied and other snapshots' queries pause briefly.
/// </summary>
internal sealed class ReverseIndexBuilder(ILogger logger)
{
    private const string BuildPhase = "Indexing incoming references";

    /// <summary>
    /// The memory mode for an index of <paramref name="indexBytes"/> (review M3): a forced mode wins; without a sidecar it
    /// can only be held in memory; next to a mapped graph it is mapped; next to an in-memory graph it is held in memory
    /// only when it fits both what the machine has available and what is left of the worker's share of physical memory
    /// after what the worker already commits.
    /// </summary>
    public static MemoryMode ChooseMode(long indexBytes, MemoryMode graphMode, MemoryMode? forced, bool hasSidecar, MemoryBudget budget, long workerCommittedBytes)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!hasSidecar)
        {
            return MemoryMode.InMemory;
        }

        if (forced is { } mode)
        {
            return mode;
        }

        if (graphMode == MemoryMode.Mapped)
        {
            return MemoryMode.Mapped;
        }

        var share = (long)(budget.TotalPhysicalBytes * budget.Fraction) - workerCommittedBytes;
        return indexBytes <= Math.Min(share, budget.AvailableBytes) ? MemoryMode.InMemory : MemoryMode.Mapped;
    }

    /// <summary>The index's size: 4 bytes per object (plus one) and 4 per reference.</summary>
    public static long IndexBytes(HeapGraph graph) => (4L * (graph.ObjectCount + 1)) + (4L * graph.EdgeCount);

    /// <summary>Opens or builds the index of <paramref name="graph"/>; the caller attaches it to the graph.</summary>
    public ReverseIndex OpenOrBuild(HeapGraph graph, ReverseIndexTarget target, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Sidecar is { } sidecar)
        {
            progress?.Report(new JobProgress("Opening the incoming references index", 0, null));
            if (TryOpen(sidecar, target, graph, target.Mode, cancellationToken) is { } opened)
            {
                return new ReverseIndex(opened, FromSidecar: true, target.Mode, SidecarWritten: true);
            }
        }

        progress?.Report(new JobProgress(BuildPhase, 0, graph.ObjectCount));
        ReverseReferences? built = ReverseReferences.Build(graph, new PhaseProgress(progress, graph.ObjectCount), cancellationToken);
        if (target.Sidecar is null || !TryWrite(target.Sidecar, target, built, cancellationToken))
        {
            return new ReverseIndex(built, FromSidecar: false, MemoryMode.InMemory, SidecarWritten: false);
        }

        if (target.Mode == MemoryMode.InMemory)
        {
            return new ReverseIndex(built, FromSidecar: false, MemoryMode.InMemory, SidecarWritten: true);
        }

        progress?.Report(new JobProgress("Mapping the incoming references index", 0, null));
        if (TryOpen(target.Sidecar, target, graph, MemoryMode.Mapped, cancellationToken) is not { } mapped)
        {
            logger.LogWarning("Reverse references sidecar {Path} could not be mapped after writing it; the index stays in memory", target.Sidecar);
            return new ReverseIndex(built, FromSidecar: false, MemoryMode.InMemory, SidecarWritten: true);
        }

        // Return the built arrays to the OS (ADR-015), without compacting: compaction would copy any in-memory graph's
        // arrays, and with regions the freed large regions are decommitted anyway.
        var stopwatch = Stopwatch.StartNew();
        built = null;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        logger.LogInformation("Reverse references built arrays released in {Milliseconds:N0} ms", stopwatch.Elapsed.TotalMilliseconds);
        return new ReverseIndex(mapped, FromSidecar: false, MemoryMode.Mapped, SidecarWritten: true);
    }

    /// <summary>The index from its sidecar, or null when the sidecar is missing, stale or damaged (it is then rebuilt).</summary>
    private ReverseReferences? TryOpen(string sidecar, ReverseIndexTarget target, HeapGraph graph, MemoryMode mode, CancellationToken cancellationToken)
    {
        var opened = IndexCacheFile.Open(sidecar, IndexFileLayout.ReverseReferences, target.BaseIdentity, target.Fingerprint, mode, cancellationToken);
        if (!opened.Success)
        {
            if (opened.Reason != IndexCacheInvalidReason.Missing)
            {
                logger.LogInformation("Reverse references sidecar {Path} not used: {Reason} ({Detail})", sidecar, opened.Reason, opened.Detail);
            }

            return null;
        }

        var file = opened.File!;
        try
        {
            if (file.TryGetSection<int>(IndexSection.InEdgeStarts, out var starts) && file.TryGetSection<int>(IndexSection.InEdges, out var edges))
            {
                return ReverseReferences.FromColumns(graph, starts, edges, file);
            }
        }
        catch (ArgumentException ex)
        {
            logger.LogInformation("Reverse references sidecar {Path} not used: {Detail}", sidecar, ex.Message);
        }

        file.Dispose();
        return null;
    }

    private bool TryWrite(string sidecar, ReverseIndexTarget target, ReverseReferences reverse, CancellationToken cancellationToken)
    {
        try
        {
            using var writer = IndexCacheFileWriter.Create(sidecar, cancellationToken: cancellationToken);
            writer.WriteSection(IndexSection.InEdgeStarts, reverse.InEdgeStarts.Span);
            writer.WriteSection(IndexSection.InEdges, reverse.InEdges.Span);
            writer.Commit(target.Fingerprint, target.BaseIdentity);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index stays in memory and is built again on the next open.
            logger.LogWarning(ex, "Reverse references sidecar {Path} could not be written", sidecar);
            return false;
        }
    }

    private sealed class PhaseProgress(IProgress<JobProgress>? target, long total) : IProgress<long>
    {
        public void Report(long value) => target?.Report(new JobProgress(BuildPhase, value, total));
    }
}
