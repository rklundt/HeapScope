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
using HeapScope.Analysis;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Exact retention of a set (sprint 2.4).</summary>
internal sealed partial class WorkerService
{
    /// <summary>A set for the log: a one-type set by its type and module (as before 3.1), any other by its shape.</summary>
    private static string Describe(ObjectSetSpec spec) =>
        spec.Kind == ObjectSetKind.Types && spec.Types is [var only]
            ? "type " + only.Name + " (" + (string.IsNullOrEmpty(only.Module) ? "no module" : only.Module) + ")"
            : spec.Shape;

    public Task<RetainedBySetResult> QueryRetainedBySetAsync(RetainedBySetRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);

            // Any object set since sprint 3.1; a type name stays the short form of a one-type set.
            var spec = request.Set ?? (string.IsNullOrEmpty(request.TypeName)
                ? throw new ArgumentException("A type name or a set is required.", nameof(request))
                : ObjectSetSpec.OfType(request.TypeName, request.Module));
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;

            // Resolved, and checked for members, before anything is computed (2.3 review M7; the 2.4 review).
            var set = await ResolveSetAsync(request.SnapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false);
            RequireMembers(set);
            var matched = set.TypeCount;

            var (dominators, info) = await GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
            var tree = dominators.Dominators;
            var aggregation = await snapshots.GetAggregationAsync(request.SnapshotId, null, cancellationToken).ConfigureAwait(false)
                ?? throw SnapshotNotLoaded(request.SnapshotId);

            var stopwatch = Stopwatch.StartNew();
            var throttled = new ThrottledProgress<JobProgress>(progress);
            var reachable = dominators.Reachable;
            const string phase = "Finding what the set retains";
            var (result, lowerBound) = await Task.Run(
                () =>
                {
                    // Like every large computation of the worker, its memory is reserved first (2.3 review M2; the 2.4 review).
                    using var reservation = snapshots.Reserve(
                        request.SnapshotId,
                        (long)(SetRetention.WorkingBytesPerObject + GroupRetention.WorkingBytesPerObject) * graph.ObjectCount,
                        phase) ?? throw SnapshotNotLoaded(request.SnapshotId);
                    try
                    {
                        var exact = SetRetention.Compute(graph, tree, set, progress: new CallbackProgress(done => throttled.Report(new JobProgress(phase, done, reachable))), cancellationToken: cancellationToken);

                        // The type summary's lower bound for the same types, beside the exact answer (whole-type sets only).
                        if (spec.Kind != ObjectSetKind.Types)
                        {
                            return (exact, 0UL);
                        }

                        var inSet = new bool[graph.Types.Count];
                        foreach (var member in set.Members)
                        {
                            inSet[graph.TypeIndices[member]] = true;
                        }

                        var groups = Array.ConvertAll(inSet, member => member ? 0 : -1);
                        return (exact, GroupRetention.Compute(graph, tree, groups, 1, cancellationToken: cancellationToken)[0]);
                    }
                    finally
                    {
                        MemoryReservations.Collect();
                    }
                },
                cancellationToken).ConfigureAwait(false);
            throttled.Flush();
            var source = RetainedBySetSource.Create(graph, aggregation, result);
            var seconds = stopwatch.Elapsed.TotalSeconds;
            var total = tree.ReachableBytes();

            // The totals and the first rows as shown (sprint 2.3.7 review H3): a disputed "this set retains" is answered from the log.
            logger.LogInformation(
                "Retained by set: {Shape} in snapshot {Id}, {Matched} type entries, {AllObjects} objects of which {SetObjects} reachable, of {SetBytes} bytes, retain {Objects} objects of {Bytes} bytes (lower bound {LowerBound}, heap {Total}) in {Seconds:0.00} s; first rows {First}",
                Describe(spec),
                request.SnapshotId,
                matched,
                result.AllObjects,
                result.SetObjects,
                result.SetBytes,
                result.RetainedObjects,
                result.RetainedBytes,
                lowerBound,
                total,
                seconds,
                string.Join("; ", source.Describe(10)));
            return new RetainedBySetResult(
                resultSets.Add(source),
                matched,
                result.SetObjects,
                (long)result.SetBytes,
                result.RetainedObjects,
                (long)result.RetainedBytes,
                (long)lowerBound,
                (long)total,
                seconds,
                info,
                result.AllObjects);
        });
}
