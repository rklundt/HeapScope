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
/// Heap queries: type summaries, diffs and trends, instances, objects, references and referrers. Served by the worker on its one
/// JSON-RPC connection with the other contracts in <see cref="WorkerContracts.All"/>; method names are unique across them
/// (ARCHITECTURE.md 4.4).
/// </summary>
public interface IHeapQueryService
{
    /// <summary>
    /// A type summary with grouping, filter and sort, plus totals (sprint 1.3). The first query of a snapshot aggregates
    /// all objects once; later queries reuse that aggregation. Unknown snapshots fail with
    /// <see cref="HeapScopeErrorCode.SnapshotNotLoaded"/>.
    /// </summary>
    Task<TypeSummaryResult> QueryTypeSummaryAsync(TypeSummaryRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Compares the per-type totals of two loaded snapshots (sprint 1.4, ADR-018), reusing each snapshot's aggregation.
    /// Unknown snapshots fail with <see cref="HeapScopeErrorCode.SnapshotNotLoaded"/>, an unknown sort column with
    /// <see cref="HeapScopeErrorCode.InvalidArgument"/>.
    /// </summary>
    Task<TypeDiffResult> QueryTypeDiffAsync(TypeDiffRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Rows of several snapshots side by side (sprint 3.3): objects and bytes per snapshot in the order given, the change
    /// first to last and the steps it grew in. 2 to <see cref="TypeTrendColumns.MaxSnapshots"/> snapshots.
    /// </summary>
    Task<TypeTrendResult> QueryTypeTrendAsync(TypeTrendRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The objects of a type as a result set (sprint 1.5). Unknown snapshots fail with
    /// <see cref="HeapScopeErrorCode.SnapshotNotLoaded"/>; an invalid filter or sort column with
    /// <see cref="HeapScopeErrorCode.InvalidArgument"/>.
    /// </summary>
    Task<InstancesResult> QueryInstancesAsync(InstancesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// One object's details and, when the snapshot has field values, its fields (sprint 1.5). An address with no object
    /// fails with <see cref="HeapScopeErrorCode.InvalidArgument"/>. The first inspection of a snapshot opens its dump,
    /// which can take seconds: <paramref name="progress"/> says so.
    /// </summary>
    Task<ObjectDetails> GetObjectAsync(ObjectRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>The objects one object references, as a result set (sprint 1.5); naming them walks up to a million array elements.</summary>
    Task<ResultSetInfo> QueryReferencesAsync(ReferencesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The objects referencing one object, each once, as a result set (sprint 2.1). The first referrers query of a
    /// snapshot builds its reverse references index (or opens it from its sidecar); later queries reuse it. An address
    /// with no object fails with <see cref="HeapScopeErrorCode.InvalidArgument"/>.
    /// </summary>
    Task<ReferrersResult> QueryReferrersAsync(ReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The objects referencing a type's objects, grouped by their own type, as a result set (sprint 2.1). An unknown sort
    /// column fails with <see cref="HeapScopeErrorCode.InvalidArgument"/>; a type with no objects gives no rows.
    /// </summary>
    Task<TypeReferrersResult> QueryTypeReferrersAsync(TypeReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}
