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
using HeapScope.Analysis.Inspections;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Rpc;

/// <summary>Instances, single objects and their references.</summary>
internal sealed partial class WorkerService
{
    /// <summary>Array elements listed when an object is inspected (more are summarized in one row).</summary>
    private const int MaxInspectedElements = 100;

    /// <summary>Characters of a string returned with its object.</summary>
    private const int MaxInspectedText = 10_000;

    public Task<InstancesResult> QueryInstancesAsync(InstancesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();

            // The set keeps the lease: its pages read the graph and the dump (string values) while it lives.
            var (snapshot, lease) = Lease(request.SnapshotId);
            try
            {
                snapshot.TryGetService<IObjectReader>(out var reader);
                if (request.Scope == InstanceScope.Finalizable && !snapshot.Info.Facts.HasFlag(SnapshotFacts.FinalizerRegistrations))
                {
                    throw new CapabilityNotSupportedException(InspectionAvailability.Explain("Listing finalizable objects", snapshot.Info));
                }

                DominatorIndex? dominators = null;
                DominatorIndexInfo? info = null;
                // Sprint 2.3 review M7: a bad request is refused, and a type with no objects answered, before any dominators.
                var valid = InstancesSource.Validate(snapshot.Graph, request);
                var set = request.Set is { } spec ? await ResolveSetAsync(request.SnapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false) : null;
                if (valid && request.IncludeRetained)
                {
                    // Sprint 2.3: attached to the graph, so the set's lease keeps them while its pages are read.
                    (dominators, info) = await GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
                }

                var throttled = new ThrottledProgress<JobProgress>(progress);
                var source = await Task.Run(() => InstancesSource.Create(snapshot.Graph, reader, request, cancellationToken, throttled, dominators?.Dominators, set), cancellationToken).ConfigureAwait(false);
                var rows = resultSets.Add(source, lease);
                return new InstancesResult(rows, source.TotalObjects, source.TotalBytes, source.MatchedTypes, reader is not null, stopwatch.Elapsed.TotalSeconds, info, source.Note);
            }
            catch (CapabilityNotSupportedException ex) when (ex.Missing != SnapshotCapabilities.None)
            {
                // The source knows what is missing; the reason depends on the snapshot's kind (sprint 1.8).
                lease.Dispose();
                throw new CapabilityNotSupportedException(
                    ex.Missing,
                    ContractMapping.ExplainMissing(ex.Missing, snapshot.Info.Kind));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });

    public Task<ObjectDetails> GetObjectAsync(ObjectRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            progress?.Report(new JobProgress("Reading the object", 0, null));
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            if (!graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            var type = graph.TypeIndices[index];
            IReadOnlyList<ObjectFieldDto>? fields = null;
            string? unavailable = null;
            string? text = null;
            if (snapshot.TryGetService<IObjectReader>(out var reader))
            {
                (fields, text) = await Task.Run(
                    () =>
                    {
                        var read = reader.ReadFields(request.Address, MaxInspectedElements, MaxFieldText, cancellationToken).Select(ObjectFormatter.ToDto).ToArray();
                        var str = graph.Types.Kinds[type].HasFlag(Core.Graph.TypeKind.String) ? reader.ReadString(request.Address, MaxInspectedText) : null;
                        return ((IReadOnlyList<ObjectFieldDto>)read, str);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                unavailable = ContractMapping.ExplainMissing(SnapshotCapabilities.FieldValues, snapshot.Info.Kind);
            }

            return new ObjectDetails(
                request.Address,
                graph.Types.GetName(type),
                (long)graph.GetSize(index),
                graph.HeapKinds[index].ToDto(),
                graph.GetReferences(index).Length,
                graph.Roots.IsRetained(index),
                fields,
                unavailable,
                text);
        });

    public Task<ResultSetInfo> QueryReferencesAsync(ReferencesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            progress?.Report(new JobProgress("Naming references", 0, null));
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            if (!snapshot.Graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            snapshot.TryGetService<IObjectReader>(out var reader);
            var source = await Task.Run(() => ReferencesSource.Create(snapshot.Graph, reader, index, cancellationToken), cancellationToken).ConfigureAwait(false);

            // The source formats pages from the graph, so the set keeps a lease of its own.
            var owner = snapshot.AcquireLease();
            return resultSets.Add(source, owner);
        });

    /// <summary>Characters of a string shown in a field (longer ones end with "...").</summary>
    private const int MaxFieldText = 1_000;
}
