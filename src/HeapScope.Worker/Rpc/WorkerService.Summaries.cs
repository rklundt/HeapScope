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
        GuardAsync(async () =>
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
            var aggregation = await snapshots.GetAggregationAsync(
                request.SnapshotId,
                new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", done, total))),
                cancellationToken).ConfigureAwait(false) ?? throw SnapshotNotLoaded(request.SnapshotId);
            var generations = snapshot.Capabilities.HasFlag(SnapshotCapabilities.Generations);
            var source = await Task.Run(() => TypeSummarySource.Create(snapshot.Graph, aggregation, request, generations), cancellationToken).ConfigureAwait(false);
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
                source.UnfilteredCount);
        });

    public Task<TypeDiffResult> QueryTypeDiffAsync(TypeDiffRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
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
            var source = await Task.Run(
                () => TypeDiffSource.Create(baseline.Graph, baselineAggregation, current.Graph, currentAggregation, request, baseline.Info.Kind, current.Info.Kind),
                cancellationToken).ConfigureAwait(false);
            throttled.Flush();
            var rows = resultSets.Add(source);
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
                baseline.Capabilities.HasFlag(SnapshotCapabilities.ExactCounts) && current.Capabilities.HasFlag(SnapshotCapabilities.ExactCounts));
        });
}
