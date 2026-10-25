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
using HeapScope.Storage;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Snapshots;

/// <summary>The root path tree of each snapshot (sprint 2.2, ARCHITECTURE.md 6.4).</summary>
internal sealed partial class SnapshotStore
{
    /// <summary>
    /// Every object's shortest path from a root (<see cref="RootPathTree"/>), built on first use and kept in memory with the
    /// snapshot (decision 5: 4 bytes per object, not cached on disk). Shared like the other derived data: one build, its
    /// progress to every waiter, its own lease. Returns null when the snapshot is not loaded or closes meanwhile.
    /// </summary>
    public async Task<RootPathTree?> GetRootPathTreeAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            return null;
        }

        using var subscription = entry.Derived.RootPathProgress.Subscribe(progress);
        var task = entry.Derived.GetOrStartRootPaths(lifetime => Task.Run(
            () =>
            {
                using var lease = entry.Snapshot.AcquireLease();
                var graph = entry.Snapshot.Graph;
                var throttled = new Rpc.ThrottledProgress<JobProgress>(entry.Derived.RootPathProgress);
                var stopwatch = Stopwatch.StartNew();
                const string phase = "Finding every object's shortest path from a root";
                RootPathTree tree;

                // 8 bytes per object reserved first (2.2 review M1, 2.3 review M2); the breadth-first queue (4 bytes per
                // object) is returned without compacting however the build ends, so no graph array is copied (2.3 review M3).
                using (_reservations.Reserve((long)RootPathTree.BuildBytesPerObject * graph.ObjectCount, "Finding every object's path from a root", entry.Index.Mode, entry.Index.Identity != 0))
                {
                    try
                    {
                        throttled.Report(new JobProgress(phase, 0, graph.ObjectCount));
                        tree = RootPathTree.Build(graph, new PhaseProgress(throttled, phase, graph.ObjectCount), lifetime);
                        throttled.Flush();
                    }
                    finally
                    {
                        MemoryReservations.Collect();
                    }
                }

                logger.LogInformation(
                    "Root path tree of snapshot {Id} built in {Seconds:N2} s: {Reachable} of {Objects} objects reachable, {Megabytes:N0} MB",
                    entry.Descriptor.SnapshotId,
                    stopwatch.Elapsed.TotalSeconds,
                    tree.ReachableCount,
                    graph.ObjectCount,
                    tree.ColumnBytes / (1024 * 1024));
                return tree;
            },
            lifetime));
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

    private sealed class PhaseProgress(IProgress<JobProgress> target, string phase, long total) : IProgress<long>
    {
        public void Report(long value) => target.Report(new JobProgress(phase, value, total));
    }
}
