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

using System.Globalization;
using System.IO;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The fake worker's Inspections (<see cref="IInspectionService"/>).</summary>
internal sealed partial class FakeWorkerService : IInspectionService
{
    public Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InspectionInfo>>([.. Inspections]);

    public async Task<InspectionResultDto> RunInspectionAsync(InspectionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        InspectionRuns.Add(request.Name);
        InspectionRequests.Add(request);
        if (InspectionGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Inspections.SingleOrDefault(i => i.Name == request.Name) is not { Unavailable: null })
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.CapabilityNotSupported, "Not available.", null);
        }

        var rows = InspectionRows.GetValueOrDefault(request.Name) ?? [];
        var id = Guid.NewGuid();
        _sets[id] = [.. rows];
        var columns = InspectionColumnsFor(request.Name);
        return new InspectionResultDto(request.Name, rows.Count, 1000, request.Name == "loh" ? null : 64, null, new ResultSetInfo(id, rows.Count, columns), 0.1, request.BaselineSnapshotId);
    }
}
