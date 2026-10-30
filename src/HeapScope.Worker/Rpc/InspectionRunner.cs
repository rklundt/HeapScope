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

/// <summary>
/// Runs an inspection and keeps a leak detector's rows in the object set cache, for the inspection RPC and for an
/// inspection row resolved as a set. It writes to the cache and never resolves sets, so the set resolver can depend on it.
/// </summary>
internal sealed class InspectionRunner(SnapshotAccess access, DerivedQueries derived, ObjectSetCache sets, ILogger<InspectionRunner> logger)
{
    /// <summary>
    /// Runs an inspection and logs its answer; the caller owns the returned lease (a set's row resolution, sprint 3.1,
    /// disposes it at once; the RPC keeps it with the rows).
    /// </summary>
    public async Task<(InspectionResult Result, string Name, Guid? BaselineId, SnapshotLease Lease, double Seconds)> RunAsync(
        InspectionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inspection = InspectionCatalog.Find(request.Name)
            ?? throw new ArgumentException($"Unknown inspection {request.Name}. Inspections: {string.Join(", ", InspectionCatalog.All.Select(i => i.Name))}.");
        var stopwatch = Stopwatch.StartNew();

        // The set keeps the lease: its rows read the graph while it lives.
        var (snapshot, lease) = access.Lease(request.SnapshotId);
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
            string? baselineName = null;
            var baselineId = inspection.UsesBaseline ? request.BaselineSnapshotId : null;
            if (baselineId is { } id)
            {
                if (id == request.SnapshotId)
                {
                    throw new ArgumentException("The baseline must be another snapshot: an earlier one of the same process.");
                }

                var (earlier, held) = access.Lease(id);
                baselineLease = held;

                // A baseline the detector cannot read (a gcdump) is refused, never silently ignored (review H8).
                if (InspectionAvailability.Explain(inspection, earlier.Capabilities, earlier.Info) is { } unusable)
                {
                    throw new ArgumentException($"The baseline {System.IO.Path.GetFileName(earlier.Info.SourcePath)} cannot be used: {unusable} Choose a full dump of the same process, captured earlier.");
                }

                earlier.TryGetService<IObjectReader>(out var baselineReader);
                baseline = new InspectionBaseline(earlier.Graph, earlier.Info, baselineReader);
                baselineName = $"{id} ({System.IO.Path.GetFileName(earlier.Info.SourcePath)}, captured {earlier.Info.CapturedAtUtc?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "at an unknown time"})";
            }

            // The incoming references for the inspections that follow references back (sprint 2.6).
            HeapScope.Core.Graph.ReverseReferences? referrers = null;
            if (inspection.UsesReferrers)
            {
                var (index, _) = await derived.GetReverseAsync(request.SnapshotId, throttled, cancellationToken).ConfigureAwait(false);
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
                "Inspection {Inspection} v{Version} ({Parameters}) on snapshot {Id} with baseline {Baseline}: {Count} findings, {Bytes} bytes, {Wasted} wasted, {Rows} rows in {Seconds:F2} s; basis: {Basis}; note: {Note}",
                inspection.Name,
                inspection.Version,
                inspection.Parameters.Length == 0 ? "no thresholds" : inspection.Parameters,
                request.SnapshotId,
                baselineName ?? "none",
                result.Count,
                result.Bytes,
                result.WastedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
                result.Rows.Count,
                stopwatch.Elapsed.TotalSeconds,
                result.Basis ?? "not recorded",
                result.Note ?? "none");
            // Kept so a detector's row opens as a set (sprint 3.1) without running the inspection again.
            if (result.Rows.HasMembers)
            {
                sets.AddTable(request.SnapshotId, inspection.Name, baselineId, result.Rows);
            }

            return (result, inspection.Name, baselineId, lease, stopwatch.Elapsed.TotalSeconds);
        }
        catch
        {
            baselineLease?.Dispose();
            lease.Dispose();
            throw;
        }
    }
}
