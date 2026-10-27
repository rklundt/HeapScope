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
using HeapScope.Analysis.Inspections;
using HeapScope.Contracts;
using HeapScope.Core;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>The simple inspections.</summary>
internal sealed partial class WorkerService
{
    public Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        GuardAsync(() =>
        {
            var (snapshot, lease) = Lease(snapshotId);
            using var _ = lease;
            IReadOnlyList<InspectionInfo> list = [.. InspectionCatalog.All.Select(i => new InspectionInfo(
                i.Name,
                i.Title,
                i.Cause,
                i.Fix,
                InspectionAvailability.Explain(i, snapshot.Capabilities, snapshot.Info),
                i.Kind == InspectionKind.Leak ? InspectionKinds.Leak : InspectionKinds.Waste,
                i.UsesBaseline))];

            // Why an inspection was not offered (sprint 2.3.7 review M2): decided from capabilities and facts.
            foreach (var unavailable in list.Where(i => i.Unavailable is not null))
            {
                logger.LogInformation("Inspection {Inspection} unavailable for snapshot {Id}: {Reason}", unavailable.Name, snapshotId, unavailable.Unavailable);
            }

            return Task.FromResult(list);
        });

    public Task<InspectionResultDto> RunInspectionAsync(InspectionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var inspection = InspectionCatalog.Find(request.Name)
                ?? throw new ArgumentException($"Unknown inspection {request.Name}. Inspections: {string.Join(", ", InspectionCatalog.All.Select(i => i.Name))}.");
            var stopwatch = Stopwatch.StartNew();

            // The set keeps the lease: its rows read the graph while it lives.
            var (snapshot, lease) = Lease(request.SnapshotId);
            SnapshotLease? baselineLease = null;
            try
            {
                if (InspectionAvailability.Explain(inspection, snapshot.Capabilities, snapshot.Info) is { } why)
                {
                    throw new CapabilityNotSupportedException(why);
                }

                snapshot.TryGetService<IObjectReader>(out var reader);
                var throttled = new ThrottledProgress<JobProgress>(progress);

                // A baseline only for the inspections that use one (sprint 2.6), held for the run only.
                InspectionBaseline? baseline = null;
                var baselineId = inspection.UsesBaseline ? request.BaselineSnapshotId : null;
                if (baselineId is { } id)
                {
                    if (id == request.SnapshotId)
                    {
                        throw new ArgumentException("The baseline must be another snapshot: an earlier one of the same process.");
                    }

                    var (earlier, held) = Lease(id);
                    baselineLease = held;
                    earlier.TryGetService<IObjectReader>(out var baselineReader);
                    baseline = new InspectionBaseline(earlier.Graph, earlier.Info, baselineReader);
                }

                // The incoming references for the inspections that follow references back (sprint 2.6).
                HeapScope.Core.Graph.ReverseReferences? referrers = null;
                if (inspection.UsesReferrers)
                {
                    var (index, _) = await GetReverseAsync(request.SnapshotId, throttled, cancellationToken).ConfigureAwait(false);
                    referrers = index.References;
                }

                var context = new InspectionContext(
                    snapshot.Graph,
                    snapshot.Info,
                    reader,
                    new CallbackProgress<InspectionProgress>(p => throttled.Report(new JobProgress(p.Phase, p.Done, p.Total))),
                    cancellationToken,
                    baseline,
                    referrers);
                InspectionResult result;
                try
                {
                    result = await Task.Run(() => inspection.Run(context), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    baselineLease?.Dispose();
                    baselineLease = null;
                }

                throttled.Flush();

                // The rule and what the answer rests on (sprint 2.3.7 review H3, M5); the guard logs the totals too.
                logger.LogInformation(
                    "Inspection {Inspection} v{Version} ({Parameters}) on snapshot {Id} with baseline {Baseline}: {Count} findings, {Bytes} bytes, {Wasted} wasted, {Rows} rows; basis: {Basis}; note: {Note}",
                    inspection.Name,
                    inspection.Version,
                    inspection.Parameters.Length == 0 ? "no thresholds" : inspection.Parameters,
                    request.SnapshotId,
                    baselineId?.ToString() ?? "none",
                    result.Count,
                    result.Bytes,
                    result.WastedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
                    result.Rows.Count,
                    result.Basis ?? "not recorded",
                    result.Note ?? "none");
                var rows = resultSets.Add(new InspectionSource(result.Rows), lease);
                return new InspectionResultDto(inspection.Name, result.Count, result.Bytes, result.WastedBytes, result.Note, rows, stopwatch.Elapsed.TotalSeconds, baselineId);
            }
            catch
            {
                baselineLease?.Dispose();
                lease.Dispose();
                throw;
            }
        });
}
