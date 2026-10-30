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
using System.Globalization;
using HeapScope.Analysis;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Storage;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// What a snapshot derives once and keeps (reverse references, dominators, the root path tree, interfaces, group
/// retention), with how each was obtained, for the handlers and the shared services. It depends on the snapshot store only.
/// </summary>
internal sealed class DerivedQueries(SnapshotStore snapshots)
{
    /// <summary>The snapshot's reverse references, built or opened on first use, and how they were obtained.</summary>
    public async Task<(ReverseIndex Index, ReverseIndexInfo Info)> GetReverseAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var index = await snapshots.GetReverseReferencesAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotAccess.NotLoaded(snapshotId);
        var mode = index.Mode == MemoryMode.Mapped ? SnapshotMemoryMode.Mapped : SnapshotMemoryMode.InMemory;
        return (index, new ReverseIndexInfo(index.FromSidecar, mode, stopwatch.Elapsed.TotalSeconds, index.References.ColumnBytes, index.SidecarWritten));
    }

    /// <summary>The snapshot's dominators, computed or opened on first use, and how they were obtained.</summary>
    public async Task<(DominatorIndex Index, DominatorIndexInfo Info)> GetDominatorsAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var snapshot = snapshots.TryGet(snapshotId) ?? throw SnapshotAccess.NotLoaded(snapshotId);
        var index = await snapshots.GetDominatorsAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotAccess.NotLoaded(snapshotId);
        return (index, Info(index, snapshot.Info.Kind, snapshot.Graph.ObjectCount, SnapshotAccess.IsComplete(snapshot)));
    }

    /// <summary>
    /// How the dominators were obtained, with the caveats a reader of retained sizes needs (review M6): an incomplete graph
    /// misses references, and a gcdump's survivors whose root it did not record retain nothing, so in both cases the
    /// retained sizes of the objects that share with them may be overstated.
    /// </summary>
    public static DominatorIndexInfo Info(DominatorIndex index, SnapshotKind kind, long objectCount, bool completeGraph)
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
            // How much memory is uncertain, not only how many objects (the long-term fix is in the Backlog).
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{unrooted:N0} objects ({Megabytes(index.UnreachableBytes)}) of this gcdump survived its collection but have no recorded root: they retain nothing here, and the retained sizes of objects that share them with a rooted object may be overstated."));
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

    /// <summary>The root path tree and the reverse references, built or opened once per snapshot.</summary>
    public async Task<(RootPathTree Tree, ReverseIndex Reverse)> RetentionInputsAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var tree = await snapshots.GetRootPathTreeAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotAccess.NotLoaded(snapshotId);
        var (reverse, _) = await GetReverseAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false);
        return (tree, reverse);
    }

    /// <summary>
    /// The interfaces of each type of a snapshot (decision 6), read once and kept with the snapshot. gcdumps and snapshots
    /// without a dump reader refuse; <paramref name="side"/> names the snapshot in a comparison's message.
    /// </summary>
    public async Task<TypeInterfaces> GetInterfacesAsync(Guid snapshotId, ISnapshot snapshot, IProgress<JobProgress>? progress, CancellationToken cancellationToken, string? side = null)
    {
        if (!snapshot.Capabilities.HasFlag(SnapshotCapabilities.FieldValues) || !snapshot.TryGetService<IObjectReader>(out var reader) || reader is null)
        {
            throw new CapabilityNotSupportedException(
                SnapshotCapabilities.FieldValues,
                $"Interfaces need a full dump: {side ?? "this snapshot"} is a gcdump, which records no interfaces. Group by type, namespace or assembly, or capture a full dump.");
        }

        return await snapshots.GetInterfacesAsync(snapshotId, reader, progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotAccess.NotLoaded(snapshotId);
    }

    /// <summary>The retained lower bound of each group of <paramref name="grouping"/>, with how the dominators were obtained.</summary>
    public async Task<(GroupRetained Retained, DominatorIndexInfo Info)> GroupRetainedAsync(Guid id, ISnapshot snapshot, TypeGrouping grouping, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var (_, info) = await GetDominatorsAsync(id, progress, cancellationToken).ConfigureAwait(false);
        var bytes = await snapshots.GetGroupRetentionAsync(id, grouping.ToGroupKey(), progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotAccess.NotLoaded(id);
        TypeGroups.GroupOfType(snapshot.Graph, grouping.ToGroupKey(), out var names);
        return (new GroupRetained(bytes, names, grouping == TypeGrouping.Type), info);
    }

    private static string Megabytes(ulong bytes) => bytes < 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:N1} KB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):N1} MB");
}
