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
/// Allocation traces: opening and closing a trace, and its timeline, types, stacks and call tree. Served by the worker on its one
/// JSON-RPC connection with the other contracts in <see cref="WorkerContracts.All"/>; method names are unique across them
/// (ARCHITECTURE.md 4.4).
/// </summary>
public interface IAllocationTraceService
{
    /// <summary>
    /// Reads the sampled allocations of an EventPipe trace (sprint 2.5, ADR-028). A file that is not a trace fails with
    /// <see cref="HeapScopeErrorCode.SnapshotFormat"/>; one too large for the memory budget with
    /// <see cref="HeapScopeErrorCode.InsufficientMemory"/>.
    /// </summary>
    Task<TraceDescriptor> OpenTraceAsync(OpenTraceRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Closes a trace; closing an unknown trace does nothing.</summary>
    Task CloseTraceAsync(Guid traceId, CancellationToken cancellationToken);

    /// <summary>
    /// Sampled bytes per stretch of the trace. This and the other allocation queries fail with
    /// <see cref="HeapScopeErrorCode.TraceNotLoaded"/> for an unknown trace and with
    /// <see cref="HeapScopeErrorCode.InvalidArgument"/> for an empty interval or a type no sample has.
    /// </summary>
    Task<AllocationTimelineResult> QueryAllocationTimelineAsync(AllocationTimelineRequest request, CancellationToken cancellationToken);

    /// <summary>The types allocated in an interval, most sampled bytes first.</summary>
    Task<AllocationTypesResult> QueryAllocationTypesAsync(AllocationTypesRequest request, CancellationToken cancellationToken);

    /// <summary>The call stacks with the most sampled bytes in an interval.</summary>
    Task<AllocationStacksResult> QueryAllocationStacksAsync(AllocationStacksRequest request, CancellationToken cancellationToken);

    /// <summary>The call tree or back traces of an interval, flattened from the open keys.</summary>
    Task<AllocationTreeResult> QueryAllocationTreeAsync(AllocationTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}
