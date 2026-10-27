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
using HeapScope.Analysis;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Storage;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Snapshots;

internal sealed partial class SnapshotStore
{
    /// <summary>File name part of the dominators sidecar (<c>app.dmp.dom.hsidx</c>).</summary>
    public const string DominatorSidecarKind = "dom";

    /// <summary>
    /// The dominators of a loaded snapshot (sprint 2.3, ARCHITECTURE.md 6.4): opened from their sidecar, or computed on first
    /// use after the reverse references index (<see cref="DominatorIndexBuilder"/>), and kept until the snapshot closes.
    /// Shared like the other derived data: one computation, its progress to every waiter, its own lease. Returns null when
    /// the snapshot is not loaded or closes meanwhile.
    /// </summary>
    /// <exception cref="InsufficientMemoryException">The working arrays would not fit the worker's budget (decision 4).</exception>
    public async Task<DominatorIndex?> GetDominatorsAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            return null;
        }

        using var subscription = entry.Derived.DominatorProgress.Subscribe(progress);
        using var waiter = entry.Derived.JoinDominators(lifetime => Task.Run(() => OpenOrComputeDominatorsAsync(entry, lifetime), lifetime));
        try
        {
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Closed before or during the computation.
            return null;
        }
    }

    /// <summary>
    /// The lower bound of the retained bytes of every group of <paramref name="grouping"/> (decision 6), numbered as
    /// <see cref="TypeGroups.GroupOfType"/> numbers them; computed once per grouping from the dominators and kept with the
    /// snapshot. Returns null when the snapshot is not loaded or closes meanwhile.
    /// </summary>
    /// <summary>
    /// Reserves memory for a large computation on a snapshot's graph that has no slot of its own (sprint 2.4: set
    /// retention, per request), against the same worker-wide budget; null when the snapshot is not open. Dispose it, then
    /// <see cref="MemoryReservations.Collect"/>, when the computation ends.
    /// </summary>
    public IDisposable? Reserve(Guid snapshotId, long bytes, string what) =>
        _snapshots.TryGetValue(snapshotId, out var entry) ? _reservations.Reserve(bytes, what, entry.Index.Mode, entry.Index.Identity != 0) : null;

    /// <summary>
    /// Reserves memory for work that belongs to no snapshot (sprint 2.5: reading an allocation trace), against the same
    /// worker-wide budget. Dispose it, then <see cref="MemoryReservations.Collect"/>, when the work ends.
    /// </summary>
    public IDisposable ReserveWorking(long bytes, string what, string nextStep) => _reservations.Reserve(bytes, what, nextStep);

    public async Task<ulong[]?> GetGroupRetentionAsync(Guid snapshotId, GroupKey grouping, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            return null;
        }

        var dominators = await GetDominatorsAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false);
        if (dominators is null)
        {
            return null;
        }

        const string phase = "Summing retained sizes by group";
        using var subscription = entry.Derived.DominatorProgress.Subscribe(progress);
        using var waiter = entry.Derived.JoinGroupRetention(grouping, lifetime => Task.Run(
            () =>
            {
                using var lease = entry.Snapshot.AcquireLease();
                var graph = entry.Snapshot.Graph;
                var stopwatch = Stopwatch.StartNew();
                var throttled = new Rpc.ThrottledProgress<JobProgress>(entry.Derived.DominatorProgress);
                using (_reservations.Reserve((long)GroupRetention.WorkingBytesPerObject * graph.ObjectCount, phase, entry.Index.Mode, entry.Index.Identity != 0))
                {
                    try
                    {
                        var groups = TypeGroups.GroupOfType(graph, grouping, out var names);
                        var totals = GroupRetention.Compute(graph, dominators.Dominators, groups, names.Length, new PhaseProgress(throttled, phase, dominators.Reachable), lifetime);
                        throttled.Flush();
                        logger.LogInformation(
                            "Retained sizes of snapshot {Id} by {Grouping} ({Groups} groups) in {Seconds:N2} s",
                            entry.Descriptor.SnapshotId,
                            grouping,
                            names.Length,
                            stopwatch.Elapsed.TotalSeconds);
                        return totals;
                    }
                    finally
                    {
                        // The working cursors (4 bytes per object) are garbage now (review M3).
                        MemoryReservations.Collect();
                    }
                }
            },
            lifetime));
        try
        {
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<DominatorIndex> OpenOrComputeDominatorsAsync(Entry entry, CancellationToken cancellationToken)
    {
        using var lease = entry.Snapshot.AcquireLease();
        var graph = entry.Snapshot.Graph;
        var stopwatch = Stopwatch.StartNew();
        var throttled = new Rpc.ThrottledProgress<JobProgress>(entry.Derived.DominatorProgress);
        var hasCache = entry.Index.CachePath is not null && entry.Index.Identity != 0;
        var target = new DominatorTarget(
            hasCache ? IndexCacheLocator.GetSidecarPath(entry.Index.CachePath!, DominatorSidecarKind) : null,
            entry.Index.Identity,
            entry.Index.Fingerprint,
            entry.Index.Mode,
            entry.Index.Forced);

        DominatorIndex index;
        if (DominatorBuilder.TryOpen(graph, target, cancellationToken) is { } opened)
        {
            index = opened;
        }
        else
        {
            // Refused before the reverse index is built for nothing (review M1); the builder reserves the memory itself.
            _reservations.Check(DominatorIndexBuilder.NeededBytes(graph.ObjectCount, managedOutputs: !hasCache), "Computing retained sizes", entry.Index.Mode, hasCache);
            var reverse = await GetReverseReferencesAsync(entry.Descriptor.SnapshotId, throttled, cancellationToken).ConfigureAwait(false)
                ?? throw new ObjectDisposedException(nameof(SnapshotStore));
            index = DominatorBuilder.Compute(graph, reverse.References, target, throttled, cancellationToken);
        }

        index = index with { Seconds = stopwatch.Elapsed.TotalSeconds };
        if (!graph.Attach(index.Dominators))
        {
            index.Dominators.Dispose();
            throw new ObjectDisposedException(nameof(SnapshotStore));
        }

        throttled.Flush();
        long peak;
        using (var process = Process.GetCurrentProcess())
        {
            peak = process.PeakPagedMemorySize64;
        }

        logger.LogInformation(
            "Dominators of snapshot {Id} {How} in {Seconds:N2} s ({Mode}, {Megabytes:N0} MB, {Reachable} of {Objects} objects reachable); worker peak commit so far {PeakMb:N0} MB",
            entry.Descriptor.SnapshotId,
            index.FromSidecar ? "opened from the sidecar" : "computed",
            index.Seconds,
            index.Mode,
            index.Dominators.ColumnBytes / (1024 * 1024),
            index.Reachable,
            graph.ObjectCount,
            peak / (1024 * 1024));
        return index;
    }
}
