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
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Snapshots;

/// <summary>
/// Each snapshot's reachable objects and the dominator tree's group tables: small values Analysis computes and the
/// snapshot keeps, so the detectors, the instances scope, the unreachable set and the tree's clicks share one copy.
/// </summary>
internal sealed partial class SnapshotStore
{
    /// <summary>
    /// The objects the leak detectors count as reachable (<see cref="Reachability.Compute"/>), walked once and kept with the
    /// snapshot. Returns null when the snapshot is not loaded or closes meanwhile.
    /// </summary>
    public Task<Reachability?> GetReachableAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        SharedAsync(
            snapshotId,
            entry => entry.Derived.JoinReachable(lifetime => Task.Run(() => Walk(entry, "Reachability (leak detectors)", Reachability.Compute, lifetime), lifetime)),
            cancellationToken);

    /// <summary>
    /// The objects the retention views count as alive (<see cref="Reachability.ComputeFromPathRoots"/>, ADR-024), walked
    /// once and kept with the snapshot. Returns null when the snapshot is not loaded or closes meanwhile.
    /// </summary>
    public Task<Reachability?> GetPathRootReachableAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        SharedAsync(
            snapshotId,
            entry => entry.Derived.JoinPathRootReachable(lifetime => Task.Run(() => Walk(entry, "Reachability (path roots)", Reachability.ComputeFromPathRoots, lifetime), lifetime)),
            cancellationToken);

    /// <summary>
    /// The dominator tree's group tables of <paramref name="grouping"/> (by type or by namespace), built once per grouping
    /// and kept with the snapshot. Returns null when the snapshot is not loaded or closes meanwhile.
    /// </summary>
    public Task<DominatorGroupTables?> GetDominatorGroupsAsync(Guid snapshotId, DominatorGrouping grouping, CancellationToken cancellationToken) =>
        SharedAsync(
            snapshotId,
            entry => entry.Derived.JoinDominatorGroups(grouping, lifetime => Task.Run(
                () =>
                {
                    using var lease = entry.Snapshot.AcquireLease();
                    return DominatorGroupTables.Build(entry.Snapshot.Graph, grouping);
                },
                lifetime)),
            cancellationToken);

    /// <summary>Waits for a shared computation of an open snapshot; null when it is not loaded or closes meanwhile.</summary>
    private async Task<T?> SharedAsync<T>(Guid snapshotId, Func<Entry, DerivedWaiter<T>> join, CancellationToken cancellationToken)
        where T : class
    {
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            return null;
        }

        using var waiter = join(entry);
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

    private Reachability Walk(Entry entry, string what, Func<Core.Graph.HeapGraph, CancellationToken, Reachability> walk, CancellationToken cancellationToken)
    {
        using var lease = entry.Snapshot.AcquireLease();
        var graph = entry.Snapshot.Graph;
        var stopwatch = Stopwatch.StartNew();

        // Reserved like every other walk (3.8 review): its stack is bounded by the object count, not by anything smaller.
        Reachability reachable;
        using (_reservations.Reserve((long)(Reachability.WalkBytesPerObject * graph.ObjectCount), what, entry.Index.Mode, entry.Index.Identity != 0))
        {
            reachable = walk(graph, cancellationToken);
        }

        logger.LogInformation(
            "{What} of snapshot {Id}: {Reachable} of {Objects} objects in {Seconds:N2} s",
            what,
            entry.Descriptor.SnapshotId,
            reachable.Count,
            graph.ObjectCount,
            stopwatch.Elapsed.TotalSeconds);
        return reachable;
    }
}
