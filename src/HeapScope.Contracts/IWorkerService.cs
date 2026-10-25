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
/// The worker's JSON-RPC service, called by UI and CLI through Application's worker host (ARCHITECTURE.md 4).
/// Long-running methods take <see cref="IProgress{T}"/> and <see cref="CancellationToken"/> as their last two
/// parameters; StreamJsonRpc marshals both. Large results are returned as result sets and fetched in pages.
/// </summary>
public interface IWorkerService
{
    /// <summary>Handshake: fails with <see cref="HeapScopeErrorCode.ProtocolMismatch"/> when versions differ.</summary>
    Task<WorkerInfo> GetInfoAsync(int clientProtocolVersion, CancellationToken cancellationToken);

    /// <summary>Diagnostic job: builds a synthetic graph and returns a per-type summary result set.</summary>
    Task<SyntheticJobResult> RunSyntheticJobAsync(SyntheticJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Looks at a snapshot file without loading it: its size, whether a valid index cache exists (header and
    /// fingerprint checked, sections not hashed) and whether it is open. Fails with
    /// <see cref="HeapScopeErrorCode.InvalidArgument"/> when the file does not exist.
    /// </summary>
    Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a snapshot file: reuses a valid index cache or loads the dump and writes the cache, then keeps the snapshot
    /// loaded until <see cref="CloseSnapshotAsync"/>. Opening a file that is already open returns the open snapshot
    /// (in the mode it was opened with). With <see cref="OpenSnapshotRequest.RequireCache"/> a missing or invalid cache
    /// fails with <see cref="HeapScopeErrorCode.SnapshotChanged"/> instead of loading the dump.
    /// </summary>
    Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Unloads a snapshot. Closing an unknown snapshot is not an error.</summary>
    Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Per-type count and shallow size of a loaded snapshot as a result set sorted by size. Unknown snapshots fail with
    /// <see cref="HeapScopeErrorCode.SnapshotNotLoaded"/>.
    /// </summary>
    Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken);

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

    /// <summary>
    /// Why one object is alive (sprint 2.2): its shortest paths from up to N roots, merged into one tree, with fields named
    /// on full dumps. Uses the snapshot's reverse references index (built on first use). An address with no object fails
    /// with <see cref="HeapScopeErrorCode.InvalidArgument"/>.
    /// </summary>
    Task<ObjectPathsResult> QueryObjectPathsAsync(ObjectPathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Why the objects of a type are alive (sprint 2.2): their key retention paths. The first such query of a snapshot builds
    /// its root path tree (one pass over the graph, kept with the snapshot); a type that matches nothing gives no paths.
    /// </summary>
    Task<TypePathsResult> QueryTypePathsAsync(TypePathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>The inspections for a snapshot, with why each unavailable one cannot run (sprint 1.6).</summary>
    Task<IReadOnlyList<InspectionInfo>> ListInspectionsAsync(Guid snapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one inspection; its rows are a result set. An unknown name fails with
    /// <see cref="HeapScopeErrorCode.InvalidArgument"/>, an unavailable one with
    /// <see cref="HeapScopeErrorCode.CapabilityNotSupported"/> (sprint 1.6).
    /// </summary>
    Task<InspectionResultDto> RunInspectionAsync(InspectionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Returns rows [offset, offset + count) of a result set; count is capped at
    /// <see cref="WorkerProtocol.MaxPageSize"/>. Unknown, released or evicted sets fail with
    /// <see cref="HeapScopeErrorCode.ResultSetNotFound"/>.
    /// </summary>
    Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken);

    /// <summary>Frees a result set. Releasing an unknown set is not an error.</summary>
    Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken);

    /// <summary>Asks the worker to exit after replying.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken);

    /// <summary>Test hook: terminates the worker abruptly. Needs <c>--enable-test-hooks</c>.</summary>
    Task CrashForTestingAsync(CancellationToken cancellationToken);

    /// <summary>Test hook: waits until canceled. Needs <c>--enable-test-hooks</c>.</summary>
    Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}
