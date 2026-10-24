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
using HeapScope.Storage;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Snapshots;

/// <summary>The reverse references index of each snapshot (sprint 2.1, ARCHITECTURE.md 6.4 and 7).</summary>
internal sealed partial class SnapshotStore
{
    /// <summary>File name part of the reverse references sidecar (<c>app.dmp.rev.hsidx</c>).</summary>
    public const string ReverseSidecarKind = "rev";

    /// <summary>
    /// The reverse references index of a loaded snapshot, opened from its sidecar or built on first use
    /// (<see cref="ReverseIndexBuilder"/>) and kept until the snapshot closes. The computation is shared and holds its own
    /// lease, so a caller that cancels only stops waiting; every waiting caller gets its progress. Callers hold a lease on
    /// the snapshot while they read the index: it is attached to the graph and released with it. Returns null when the
    /// snapshot is not loaded or closes meanwhile.
    /// </summary>
    public async Task<ReverseIndex?> GetReverseReferencesAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            return null;
        }

        using var subscription = entry.Derived.ReverseProgress.Subscribe(progress);
        var task = entry.Derived.GetOrStartReverseReferences(lifetime => Task.Run(() => OpenOrBuildReverse(entry, lifetime), lifetime));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Closed before or during the computation.
            return null;
        }
    }

    private ReverseIndex OpenOrBuildReverse(Entry entry, CancellationToken cancellationToken)
    {
        using var lease = entry.Snapshot.AcquireLease();
        var graph = entry.Snapshot.Graph;
        var stopwatch = Stopwatch.StartNew();
        var throttled = new Rpc.ThrottledProgress<JobProgress>(entry.Derived.ReverseProgress);
        var sidecar = entry.Index.CachePath is { } cachePath && entry.Index.Identity != 0
            ? IndexCacheLocator.GetSidecarPath(cachePath, ReverseSidecarKind)
            : null;
        long committed;
        using (var process = Process.GetCurrentProcess())
        {
            committed = process.PrivateMemorySize64;
        }

        var mode = ReverseIndexBuilder.ChooseMode(
            ReverseIndexBuilder.IndexBytes(graph),
            entry.Index.Mode,
            entry.Index.Forced,
            sidecar is not null,
            MemoryBudget.Current(),
            committed);
        var index = _reverseBuilder.OpenOrBuild(graph, new ReverseIndexTarget(sidecar, entry.Index.Identity, entry.Index.Fingerprint, mode), throttled, cancellationToken);
        if (!graph.Attach(index.References))
        {
            index.References.Dispose();
            throw new ObjectDisposedException(nameof(SnapshotStore));
        }

        throttled.Flush();
        long peakCommit;
        using (var process = Process.GetCurrentProcess())
        {
            peakCommit = process.PeakPagedMemorySize64;
        }

        logger.LogInformation(
            "Reverse references of snapshot {Id} {How} in {Seconds:N2} s ({Mode}, {Megabytes:N0} MB); worker peak commit so far {PeakMb:N0} MB",
            entry.Descriptor.SnapshotId,
            index.FromSidecar ? "opened from the sidecar" : "built",
            stopwatch.Elapsed.TotalSeconds,
            index.Mode,
            index.References.ColumnBytes / (1024 * 1024),
            peakCommit / (1024 * 1024));
        return index;
    }

    /// <summary>Where a snapshot's index cache is and what it is; optional indices name it (sprint 2.1).</summary>
    /// <param name="CachePath">The index cache the snapshot was opened from or wrote; null when none was written.</param>
    /// <param name="Identity">The cache's identity; 0 when none was written.</param>
    /// <param name="Forced">The memory mode the open request forced, if any; optional indices follow it too.</param>
    private sealed record IndexState(string? CachePath, ulong Identity, SnapshotFingerprint Fingerprint, MemoryMode Mode, MemoryMode? Forced);
}
