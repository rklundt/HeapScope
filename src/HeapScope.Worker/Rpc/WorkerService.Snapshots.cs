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
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Opening and closing snapshots.</summary>
internal sealed partial class WorkerService
{
    public Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken) =>
        GuardAsync(() => Task.Run(() => snapshots.Probe(path), cancellationToken));

    public Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            logger.LogInformation("Opening snapshot (rebuild {Rebuild}, mode {Mode})", request.Rebuild, request.Mode);
            return snapshots.OpenAsync(request, progress, cancellationToken);
        });

    public Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        snapshots.Close(snapshotId);
        return Task.CompletedTask;
    }
}
