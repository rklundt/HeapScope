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

using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Rpc;

/// <summary>Leases on loaded snapshots, and the refusal for a snapshot that is not loaded, for every handler.</summary>
internal sealed class SnapshotAccess(SnapshotStore snapshots)
{
    /// <summary>A loaded snapshot and a lease on it; a snapshot closed meanwhile refuses the lease.</summary>
    public (ISnapshot Snapshot, SnapshotLease Lease) Lease(Guid snapshotId)
    {
        var snapshot = snapshots.TryGet(snapshotId) ?? throw NotLoaded(snapshotId);
        try
        {
            return (snapshot, snapshot.AcquireLease());
        }
        catch (ObjectDisposedException)
        {
            throw NotLoaded(snapshotId);
        }
    }

    public static StreamJsonRpc.LocalRpcException NotLoaded(Guid snapshotId) => WorkerErrors.Create(
        HeapScopeErrorCode.SnapshotNotLoaded,
        $"Snapshot {snapshotId} is not loaded in the analysis worker: it was closed or the worker restarted. Open it again.");

    public static bool IsComplete(ISnapshot snapshot) => snapshot.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph);
}
