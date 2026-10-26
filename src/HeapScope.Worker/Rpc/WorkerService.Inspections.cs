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
            IReadOnlyList<InspectionInfo> list = [.. InspectionCatalog.All.Select(i => new InspectionInfo(i.Name, i.Title, i.Cause, i.Fix, InspectionAvailability.Explain(i, snapshot.Capabilities, snapshot.Info)))];

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
            try
            {
                if (InspectionAvailability.Explain(inspection, snapshot.Capabilities, snapshot.Info) is { } why)
                {
                    throw new CapabilityNotSupportedException(why);
                }

                snapshot.TryGetService<IObjectReader>(out var reader);
                var throttled = new ThrottledProgress<JobProgress>(progress);
                var context = new InspectionContext(
                    snapshot.Graph,
                    snapshot.Info,
                    reader,
                    new CallbackProgress<InspectionProgress>(p => throttled.Report(new JobProgress(p.Phase, p.Done, p.Total))),
                    cancellationToken);
                var result = await Task.Run(() => inspection.Run(context), cancellationToken).ConfigureAwait(false);
                throttled.Flush();

                // The rule and what the answer rests on (sprint 2.3.7 review H3, M5); the guard logs the totals too.
                logger.LogInformation(
                    "Inspection {Inspection} v{Version} ({Parameters}) on snapshot {Id}: {Count} findings, {Bytes} bytes, {Wasted} wasted, {Rows} rows; basis: {Basis}; note: {Note}",
                    inspection.Name,
                    inspection.Version,
                    inspection.Parameters.Length == 0 ? "no thresholds" : inspection.Parameters,
                    request.SnapshotId,
                    result.Count,
                    result.Bytes,
                    result.WastedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
                    result.Rows.Count,
                    result.Basis ?? "not recorded",
                    result.Note ?? "none");
                var rows = resultSets.Add(new InspectionSource(result.Rows), lease);
                return new InspectionResultDto(inspection.Name, result.Count, result.Bytes, result.WastedBytes, result.Note, rows, stopwatch.Elapsed.TotalSeconds);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });
}
