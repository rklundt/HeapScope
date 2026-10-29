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
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Rpc;

/// <summary>Type summaries and snapshot diffs.</summary>
internal sealed partial class WorkerService
{
    public async Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        (await QueryTypeSummaryAsync(new TypeSummaryRequest(snapshotId), null, cancellationToken).ConfigureAwait(false)).Rows;

    public Task<TypeSummaryResult> QueryTypeSummaryAsync(TypeSummaryRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();
            var snapshot = snapshots.TryGet(request.SnapshotId) ?? throw SnapshotNotLoaded(request.SnapshotId);

            // The lease keeps a mapped graph alive while the aggregation and the grouping read it; the result source
            // itself holds only names and totals. A snapshot closed between the lookup and here refuses the lease.
            SnapshotLease lease;
            try
            {
                lease = snapshot.AcquireLease();
            }
            catch (ObjectDisposedException)
            {
                throw SnapshotNotLoaded(request.SnapshotId);
            }

            using var _ = lease;
            var throttled = new ThrottledProgress<JobProgress>(progress);
            var total = snapshot.Graph.ObjectCount;
            Analysis.Sets.ObjectSet? set = null;
            Analysis.TypeAggregation aggregation;
            if (request.Set is { } spec)
            {
                // A set's Types and Generations views (sprint 3.1): its members only, aggregated per query.
                if (request.IncludeRetained)
                {
                    throw new ArgumentException("A set's types have no retained column; its Retained view gives the exact figure.");
                }

                set = await ResolveSetAsync(request.SnapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false);
                var members = set;
                aggregation = await Task.Run(() => Analysis.TypeAggregation.Compute(snapshot.Graph, members, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                aggregation = await snapshots.GetAggregationAsync(
                    request.SnapshotId,
                    new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", done, total))),
                    cancellationToken).ConfigureAwait(false) ?? throw SnapshotNotLoaded(request.SnapshotId);
            }
            var generations = snapshot.Capabilities.HasFlag(SnapshotCapabilities.Generations);
            Analysis.TypeInterfaces? interfaces = null;
            if (request.GroupBy == TypeGrouping.Interface)
            {
                // Interface groups overlap, so they have no retained lower bound (decision 6); a gcdump is refused first.
                interfaces = await GetInterfacesAsync(request.SnapshotId, snapshot, progress, cancellationToken).ConfigureAwait(false);
                if (request.IncludeRetained)
                {
                    throw new ArgumentException("Interface rows have no retained column: a type counts under each of its interfaces, so the rows overlap. Leave out retained sizes (--retained), or group by type, namespace or assembly.");
                }
            }

            ulong[]? retained = null;
            DominatorIndexInfo? dominators = null;
            if (request.IncludeRetained)
            {
                TypeSummarySource.Validate(request, generations);

                // Sprint 2.3: the dominators (computed or opened once), then the grouping's lower bounds (once per grouping).
                dominators = (await GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false)).Info;
                retained = await snapshots.GetGroupRetentionAsync(request.SnapshotId, request.GroupBy.ToGroupKey(), progress, cancellationToken).ConfigureAwait(false)
                    ?? throw SnapshotNotLoaded(request.SnapshotId);
            }

            var source = await Task.Run(() => TypeSummarySource.Create(snapshot.Graph, aggregation, request, generations, retained, interfaces), cancellationToken).ConfigureAwait(false);
            throttled.Flush();
            var rows = resultSets.Add(source);
            // Unknown (generation not determined) is listed only when present, so the bar always adds up to the total.
            var kinds = generations
                ? TypeSummarySource.KindColumns.Prepend(Core.Graph.HeapKind.Unknown)
                    .Where(k => k != Core.Graph.HeapKind.Unknown || aggregation.KindBytes[(int)k] > 0)
                    .Select(k => new HeapKindTotal(k.ToDto(), aggregation.KindObjects[(int)k], (long)aggregation.KindBytes[(int)k]))
                    .ToArray()
                : [];
            return new TypeSummaryResult(
                rows,
                aggregation.TotalObjects,
                (long)aggregation.TotalBytes,
                aggregation.Counts.Count(c => c > 0),
                generations,
                kinds,
                stopwatch.Elapsed.TotalSeconds,
                source.UnfilteredCount,
                dominators,
                set is null ? null : new ObjectSetInfo(set.Count, (long)set.Bytes, set.TypeCount));
        });

    public Task<TypeDiffResult> QueryTypeDiffAsync(TypeDiffRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();

            // Both graphs stay leased while their aggregations and names are read; the source keeps only names and totals.
            var (baseline, baselineLease) = Lease(request.BaselineId);
            using var _ = baselineLease;
            var (current, currentLease) = Lease(request.CurrentId);
            using var __ = currentLease;
            var throttled = new ThrottledProgress<JobProgress>(progress);
            var total = baseline.Graph.ObjectCount + (long)current.Graph.ObjectCount;
            long baselineDone = 0;
            var baselineAggregation = await snapshots.GetAggregationAsync(
                request.BaselineId,
                new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", baselineDone = done, total))),
                cancellationToken).ConfigureAwait(false) ?? throw SnapshotNotLoaded(request.BaselineId);
            var currentAggregation = await snapshots.GetAggregationAsync(
                request.CurrentId,
                new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", baselineDone + done, total))),
                cancellationToken).ConfigureAwait(false) ?? throw SnapshotNotLoaded(request.CurrentId);
            Analysis.TypeInterfaces? baselineInterfaces = null, currentInterfaces = null;
            if (request.GroupBy == TypeGrouping.Interface)
            {
                baselineInterfaces = await GetInterfacesAsync(request.BaselineId, baseline, progress, cancellationToken, "the baseline").ConfigureAwait(false);
                currentInterfaces = await GetInterfacesAsync(request.CurrentId, current, progress, cancellationToken, "the current snapshot").ConfigureAwait(false);
            }

            var (extras, comparison) = await DiffExtrasAsync(request, baseline, current, progress, cancellationToken).ConfigureAwait(false);
            var source = await Task.Run(
                () => TypeDiffSource.Create(baseline.Graph, baselineAggregation, current.Graph, currentAggregation, request, baseline.Info.Kind, current.Info.Kind, baselineInterfaces, currentInterfaces, extras),
                cancellationToken).ConfigureAwait(false);
            throttled.Flush();
            var rows = resultSets.Add(source);
            LogDiff(request, comparison, source);
            return new TypeDiffResult(
                rows,
                baselineAggregation.TotalObjects,
                (long)baselineAggregation.TotalBytes,
                currentAggregation.TotalObjects,
                (long)currentAggregation.TotalBytes,
                source.NewRows,
                source.GoneRows,
                source.UnfilteredCount,
                source.MatchingBaselineBytes,
                source.MatchingBytesDelta,
                stopwatch.Elapsed.TotalSeconds,
                baseline.Capabilities.HasFlag(SnapshotCapabilities.ExactCounts) && current.Capabilities.HasFlag(SnapshotCapabilities.ExactCounts),
                comparison.Matched,
                comparison.IdentityNote,
                comparison.NewObjects,
                comparison.DeadObjects,
                extras.Kinds
                    ? [.. TypeSummarySource.KindColumns.Select(k => new HeapKindTotal(k.ToDto(), currentAggregation.KindObjects[(int)k] - baselineAggregation.KindObjects[(int)k], (long)currentAggregation.KindBytes[(int)k] - (long)baselineAggregation.KindBytes[(int)k]))]
                    : null,
                comparison.BaselineDominators,
                comparison.CurrentDominators);
        });
}
