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
/// Retention: paths from roots, retained sizes, the dominator tree, set retention, Similar Retention and the icicle. Served by the worker on its one
/// JSON-RPC connection with the other contracts in <see cref="WorkerContracts.All"/>; method names are unique across them
/// (ARCHITECTURE.md 4.4).
/// </summary>
public interface IRetentionService
{
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

    /// <summary>
    /// One object's retained size and immediate dominator (sprint 2.3). The first retained query of a snapshot computes its
    /// dominators (after its reverse references index), or opens them from their sidecar; later queries reuse them. An
    /// address with no object fails with <see cref="HeapScopeErrorCode.InvalidArgument"/>; dominators that would not fit the
    /// worker's memory budget fail with <see cref="HeapScopeErrorCode.InsufficientMemory"/>.
    /// </summary>
    Task<RetainedResult> QueryRetainedAsync(RetainedRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The dominator tree as a flattened, paged list (sprint 2.4, ADR-027): its top level (the biggest retainers) or the
    /// tree below a focus object, with the requested rows expanded and each node's children as objects or groups. Computes
    /// or opens the dominators on first use like <see cref="QueryRetainedAsync"/>. A focus with no object, or one no root
    /// reaches, fails with <see cref="HeapScopeErrorCode.InvalidArgument"/>.
    /// </summary>
    Task<DominatorTreeResult> QueryDominatorTreeAsync(DominatorTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The objects every object of a type retains together, exactly (sprint 2.4): one reachability pass from the roots that
    /// skips the set. Computes or opens the dominators on first use like <see cref="QueryRetainedAsync"/>. A type that
    /// matches nothing fails with <see cref="HeapScopeErrorCode.InvalidArgument"/> before anything is computed.
    /// </summary>
    Task<RetainedBySetResult> QueryRetainedBySetAsync(RetainedBySetRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The members of an object set that no other member dominates, with what each retains (sprint 3.1, ADR-030). Computes
    /// or opens the dominators on first use like <see cref="QueryRetainedAsync"/>. A set that selects nothing fails with
    /// <see cref="HeapScopeErrorCode.InvalidArgument"/>.
    /// </summary>
    Task<SetDominatorsResult> QuerySetDominatorsAsync(SetDominatorsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// A set's members grouped by retention pattern (sprint 3.2, ADR-031). Builds the root path tree and the reverse
    /// references on first use. A set that selects nothing fails with <see cref="HeapScopeErrorCode.InvalidArgument"/>.
    /// </summary>
    Task<SimilarRetentionResult> QuerySimilarRetentionAsync(SimilarRetentionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The icicle chart below a focus object or the whole reachable heap (sprint 2.4). Fails like
    /// <see cref="QueryDominatorTreeAsync"/> for a focus with no object or none a root reaches.
    /// </summary>
    Task<IcicleResult> QueryIcicleAsync(IcicleRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}
