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
                EnsureRoomForRootPaths(graph.ObjectCount);
                var throttled = new Rpc.ThrottledProgress<JobProgress>(entry.Derived.RootPathProgress);
                var stopwatch = Stopwatch.StartNew();
                const string phase = "Finding every object's shortest path from a root";
                throttled.Report(new JobProgress(phase, 0, graph.ObjectCount));
                var tree = RootPathTree.Build(graph, new PhaseProgress(throttled, phase, graph.ObjectCount), lifetime);
                throttled.Flush();

                // The breadth-first queue (4 bytes per object) is garbage now: return it like the 2.1 index's built arrays,
                // without compacting, so no graph array is copied (review M1).
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
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

    /// <summary>
    /// Refuses to build the root path tree when its 8 bytes per object would not fit what is left of the worker's share of
    /// physical memory, or what the machine has available (review M1); the error says how much it needs.
    /// </summary>
    private static void EnsureRoomForRootPaths(int objectCount)
    {
        var needed = (long)RootPathTree.BuildBytesPerObject * objectCount;
        var budget = MemoryBudget.Current();
        long committed;
        using (var process = Process.GetCurrentProcess())
        {
            committed = process.PrivateMemorySize64;
        }

        var room = Math.Min(budget.AvailableBytes, (long)(budget.TotalPhysicalBytes * budget.Fraction) - committed);
        if (needed > room)
        {
            throw new InsufficientMemoryException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Finding every object's path from a root needs about {needed / (1024 * 1024):N0} MB, and the analysis worker has {Math.Max(0, room) / (1024 * 1024):N0} MB left of its memory budget. Close other snapshots, or open this one mapped, and try again."));
        }
    }

    private sealed class PhaseProgress(IProgress<JobProgress> target, string phase, long total) : IProgress<long>
    {
        public void Report(long value) => target.Report(new JobProgress(phase, value, total));
    }
}
