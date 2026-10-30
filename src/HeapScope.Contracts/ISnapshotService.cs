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

namespace HeapScope.Contracts;

/// <summary>
/// Snapshots: probing, opening and closing a snapshot file. Served by the worker on its one
/// JSON-RPC connection with the other contracts in <see cref="WorkerContracts.All"/>; method names are unique across them
/// (ARCHITECTURE.md 4.4).
/// </summary>
public interface ISnapshotService
{
    /// <summary>
    /// Looks at a snapshot file without loading it: its size, whether a valid index cache exists (header and
    /// fingerprint checked, sections not hashed) and whether it is open. Fails with
    /// <see cref="HeapScopeErrorCode.InvalidArgument"/> when the file does not exist.
    /// </summary>
    Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a snapshot file: reuses a valid index cache or loads the dump and writes the cache, then keeps the snapshot
    /// loaded until <see cref="CloseSnapshotAsync"/>. Opening a file that is already open returns the open snapshot
    /// (in the mode it was opened with). With <see cref="OpenSnapshotRequest.RequireCache"/> a missing or invalid cache
    /// fails with <see cref="HeapScopeErrorCode.SnapshotChanged"/> instead of loading the dump.
    /// </summary>
    Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Unloads a snapshot. Closing an unknown snapshot is not an error.</summary>
    Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Per-type count and shallow size of a loaded snapshot as a result set sorted by size. Unknown snapshots fail with
    /// <see cref="HeapScopeErrorCode.SnapshotNotLoaded"/>.
    /// </summary>
    Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken);
}
