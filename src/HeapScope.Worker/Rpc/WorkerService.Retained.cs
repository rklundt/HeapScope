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

using System.Globalization;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Storage;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Rpc;

internal sealed partial class WorkerService
{
    public Task<RetainedResult> QueryRetainedAsync(RetainedRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);

            // The lease keeps the graph, and the dominators attached to it, while they are read.
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            if (!graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            var (dominators, info) = await GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
            var idom = dominators.Dominators.ImmediateDominator(index);
            return new RetainedResult(
                request.Address,
                graph.Types.GetName(graph.TypeIndices[index]),
                (long)graph.GetSize(index),
                idom != DominatorTree.Unreachable,
                (long)dominators.Dominators.RetainedSize(index),
                idom >= 0 ? graph.Addresses[idom] : 0,
                idom >= 0 ? graph.Types.GetName(graph.TypeIndices[idom]) : null,
                idom == DominatorTree.VirtualRoot,
                RetentionMapping.UnreachableNote(snapshot.Info.Kind),
                info,
                IsComplete(snapshot));
        });

    /// <summary>The snapshot's dominators, computed or opened on first use, and how they were obtained.</summary>
    private async Task<(DominatorIndex Index, DominatorIndexInfo Info)> GetDominatorsAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var snapshot = snapshots.TryGet(snapshotId) ?? throw SnapshotNotLoaded(snapshotId);
        var index = await snapshots.GetDominatorsAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotNotLoaded(snapshotId);
        return (index, Info(index, snapshot.Info.Kind, snapshot.Graph.ObjectCount, IsComplete(snapshot)));
    }

    /// <summary>
    /// How the dominators were obtained, with the caveats a reader of retained sizes needs (review M6): an incomplete graph
    /// misses references, and a gcdump's survivors whose root it did not record retain nothing, so in both cases the
    /// retained sizes of the objects that share with them may be overstated.
    /// </summary>
    internal static DominatorIndexInfo Info(DominatorIndex index, SnapshotKind kind, long objectCount, bool completeGraph)
    {
        ArgumentNullException.ThrowIfNull(index);
        var notes = new List<string>();
        if (!completeGraph)
        {
            notes.Add("This snapshot is incomplete, so some references are missing and retained sizes may be overstated.");
        }

        var unrooted = objectCount - index.Reachable;
        if (kind == SnapshotKind.GcDump && unrooted > 0)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{unrooted:N0} objects of this gcdump survived its collection but have no recorded root: they retain nothing here, and the retained sizes of objects that share them with a rooted object may be overstated."));
        }

        return new DominatorIndexInfo(
            index.FromSidecar,
            index.Mode == MemoryMode.Mapped ? SnapshotMemoryMode.Mapped : SnapshotMemoryMode.InMemory,
            index.Seconds,
            index.Dominators.ColumnBytes,
            index.Saved,
            index.Reachable,
            completeGraph,
            notes.Count == 0 ? null : string.Join(" ", notes));
    }
}
