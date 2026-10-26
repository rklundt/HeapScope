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
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Storage;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Rpc;

/// <summary>Incoming references: who references an object or a type (sprint 2.1).</summary>
internal sealed partial class WorkerService
{
    public Task<ReferrersResult> QueryReferrersAsync(ReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);

            // The set keeps the lease: its pages read the graph. The reverse index is attached to the graph, so the lease
            // also keeps it while the rows are copied out of it.
            var (snapshot, lease) = Lease(request.SnapshotId);
            try
            {
                if (!snapshot.Graph.TryGetIndex(request.Address, out var index))
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
                }

                var (reverse, info) = await GetReverseAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
                snapshot.TryGetService<IObjectReader>(out var reader);
                var noReader = ContractMapping.ExplainMissing(SnapshotCapabilities.FieldValues, snapshot.Info.Kind);
                var throttled = new ThrottledProgress<JobProgress>(progress);
                var source = await Task.Run(
                    () => ReferrersSource.Create(snapshot.Graph, reverse.References, reader, index, noReader, throttled, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                throttled.Flush();
                return new ReferrersResult(resultSets.Add(source, lease), source.ReferenceCount, source.FieldsNote, info, IsComplete(snapshot));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });

    public Task<TypeReferrersResult> QueryTypeReferrersAsync(TypeReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrEmpty(request.TypeName))
            {
                throw new ArgumentException("A type name is required.", nameof(request));
            }

            var (snapshot, lease) = Lease(request.SnapshotId);
            try
            {
                // An unknown sort column or type is answered before the index is built for nothing (review M8).
                if (TypeReferrersSource.EmptyUnlessMatched(snapshot.Graph, request) is { } none)
                {
                    return new TypeReferrersResult(resultSets.Add(none, lease), 0, 0, 0, null, IsComplete(snapshot));
                }

                var (reverse, info) = await GetReverseAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new JobProgress("Grouping referrers by type", 0, null));
                var source = await Task.Run(() => TypeReferrersSource.Create(snapshot.Graph, reverse.References, request, cancellationToken), cancellationToken).ConfigureAwait(false);
                return new TypeReferrersResult(resultSets.Add(source, lease), source.MatchedTypes, source.TargetObjects, source.ReferenceCount, info, IsComplete(snapshot));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });

    private async Task<(ReverseIndex Index, ReverseIndexInfo Info)> GetReverseAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var index = await snapshots.GetReverseReferencesAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotNotLoaded(snapshotId);
        var mode = index.Mode == MemoryMode.Mapped ? SnapshotMemoryMode.Mapped : SnapshotMemoryMode.InMemory;
        return (index, new ReverseIndexInfo(index.FromSidecar, mode, stopwatch.Elapsed.TotalSeconds, index.References.ColumnBytes, index.SidecarWritten));
    }

    private static bool IsComplete(ISnapshot snapshot) => snapshot.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph);
}
