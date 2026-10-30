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

using HeapScope.Analysis.Inspections;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Inspections and leak detectors (<see cref="IInspectionService"/>): which apply to a snapshot, and running one.</summary>
internal sealed class InspectionHandler(
    ResultSetStore resultSets,
    SnapshotAccess access,
    InspectionRunner runner,
    ILogger<InspectionHandler> logger) : RpcHandler(logger), IInspectionService
{
    public Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        GuardAsync(() =>
        {
            var (snapshot, lease) = access.Lease(snapshotId);
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
                Logger.LogInformation("Inspection {Inspection} unavailable for snapshot {Id}: {Reason}", unavailable.Name, snapshotId, unavailable.Unavailable);
            }

            return Task.FromResult(list);
        });

    public Task<InspectionResultDto> RunInspectionAsync(InspectionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            var run = await runner.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);

            // The set keeps the lease: its rows read the graph while it lives.
            var rows = resultSets.Add(new InspectionSource(run.Result.Rows), run.Lease);
            return new InspectionResultDto(run.Name, run.Result.Count, run.Result.Bytes, run.Result.WastedBytes, run.Result.Note, rows, run.Seconds, run.BaselineId);
        });
}
