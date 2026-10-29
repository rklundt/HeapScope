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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using HeapScope.Analysis;
using HeapScope.Analysis.Retention;
using HeapScope.Analysis.Sets;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Sprint 3.2 (ADR-031): Similar Retention, and the interfaces of a snapshot's types.</summary>
internal sealed partial class WorkerService
{
    public Task<SimilarRetentionResult> QuerySimilarRetentionAsync(SimilarRetentionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Set);
            if (request.MaxGroups is < 1 or > SimilarRetentionLimits.MaxGroups)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"MaxGroups must be between 1 and {SimilarRetentionLimits.MaxGroups}."), nameof(request));
            }

            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var set = await ResolveSetAsync(request.SnapshotId, snapshot, request.Set, progress, cancellationToken).ConfigureAwait(false);
            RequireMembers(set);
            var stopwatch = Stopwatch.StartNew();
            var (tree, reverse) = await RetentionInputsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
            const string phase = "Grouping objects by retention pattern";
            progress?.Report(new JobProgress(phase, 0, null));
            var groupStopwatch = Stopwatch.StartNew();
            var groups = await Task.Run(
                () =>
                {
                    // The member groups, the patterns and the signature memo (the 3.2 review), compacted after.
                    using var reservation = snapshots.Reserve(request.SnapshotId, SimilarRetention.WorkingBytes(set.Count, graph.ObjectCount), phase)
                        ?? throw SnapshotNotLoaded(request.SnapshotId);
                    try
                    {
                        return SimilarRetention.Group(graph, tree, reverse.References, set, request.MaxGroups, cancellationToken);
                    }
                    finally
                    {
                        MemoryReservations.Collect();
                    }
                },
                cancellationToken).ConfigureAwait(false);
            var groupSeconds = groupStopwatch.Elapsed.TotalSeconds;
            var result = new SimilarRetentionResult(
                [.. groups.Groups.Select((g, rank) => new RetentionGroupDto(
                    rank,
                    g.Objects,
                    (long)g.Bytes,
                    g.Types,
                    graph.Types.GetName(g.FirstType),
                    g.PathCount,
                    [.. g.Shown.Select(p => RetentionMapping.KeyPath(graph, p))],
                    graph.Addresses[g.ExampleObject],
                    g.Key))],
                Info(set),
                groups.GroupCount,
                groups.Unreachable,
                (long)groups.UnreachableBytes,
                groups.OtherObjects,
                (long)groups.OtherBytes,
                groups.LimitedMembers,
                IsComplete(snapshot),
                stopwatch.Elapsed.TotalSeconds,
                RetentionMapping.UnreachableNote(snapshot.Info.Kind));

            // What the view rests on (decision 7): the set, the groups, and the largest groups with the paths shown; every
            // listed group at Debug (the 3.2 review), numbered as the app and the CLI number them.
            logger.LogInformation(
                "Similar retention (rule v{Version}, reverse index {Mode}): {Shape} in snapshot {Id}, {Members} members in {Groups} groups ({Unreachable} unreachable, {Limited} with more than {Max} referrers) grouped in {Seconds:0.00} s; largest {Largest}",
                SimilarRetention.Version,
                reverse.Mode,
                request.Set.Shape,
                request.SnapshotId,
                set.Count,
                result.GroupCount,
                result.Unreachable,
                result.LimitedMembers,
                SimilarRetention.MaxReferrers,
                groupSeconds,
                string.Join("; ", result.Groups.Take(5).Select(Provenance.Describe)));
            if (logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var group in result.Groups.Skip(5))
                {
                    logger.LogDebug("Similar retention of {Shape} in snapshot {Id}: {Group}", request.Set.Shape, request.SnapshotId, Provenance.Describe(group));
                }
            }

            return result;
        });

    /// <summary>The root path tree and the reverse references, built or opened once per snapshot.</summary>
    private async Task<(RootPathTree Tree, ReverseIndex Reverse)> RetentionInputsAsync(Guid snapshotId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var tree = await snapshots.GetRootPathTreeAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotNotLoaded(snapshotId);
        var (reverse, _) = await GetReverseAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false);
        return (tree, reverse);
    }

    /// <summary>The members of a Similar Retention group: the inner set grouped again, then the group at the spec's rank.</summary>
    private async Task<(ObjectSet Set, string Detail)> ResolveRetentionGroupAsync(Guid snapshotId, ISnapshot snapshot, ObjectSetSpec spec, IProgress<JobProgress>? progress, CancellationToken cancellationToken, int depth)
    {
        var innerSpec = spec.Inner ?? throw new ArgumentException("A retention group set needs its inner set.");
        if (string.IsNullOrEmpty(spec.Name))
        {
            throw new ArgumentException("A retention group set needs its group's pattern key.");
        }

        var inner = await ResolveSetAsync(snapshotId, snapshot, innerSpec, progress, cancellationToken, depth + 1).ConfigureAwait(false);
        RequireMembers(inner);
        var (tree, reverse) = await RetentionInputsAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false);
        var graph = snapshot.Graph;
        var set = await Task.Run(
            () =>
            {
                using var reservation = snapshots.Reserve(snapshotId, SimilarRetention.WorkingBytes(inner.Count, graph.ObjectCount) + ObjectSet.WorkingBytes(graph), "Resolving a retention group")
                    ?? throw SnapshotNotLoaded(snapshotId);
                try
                {
                    return ObjectSet.OfIndices(graph, SimilarRetention.MembersOf(graph, tree, reverse.References, inner, spec.Name ?? string.Empty, spec.Row, cancellationToken).AsSpan());
                }
                finally
                {
                    MemoryReservations.Collect();
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (set.Count == 0)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"No group of this set is retained the way group {spec.Row + 1} was (pattern #{spec.Name}) in this snapshot. List the groups again with Similar Retention (the CLI's --view similar)."));
        }

        return (set, string.Create(CultureInfo.InvariantCulture, $"group {spec.Row + 1} (pattern #{spec.Name}) of the inner set's {inner.Count} objects"));
    }

    /// <summary>
    /// The interfaces of each type of a snapshot (decision 6), read once and kept with the snapshot. gcdumps and snapshots
    /// without a dump reader refuse; <paramref name="side"/> names the snapshot in a comparison's message.
    /// </summary>
    private async Task<TypeInterfaces> GetInterfacesAsync(Guid snapshotId, ISnapshot snapshot, IProgress<JobProgress>? progress, CancellationToken cancellationToken, string? side = null)
    {
        if (!snapshot.Capabilities.HasFlag(SnapshotCapabilities.FieldValues) || !snapshot.TryGetService<IObjectReader>(out var reader) || reader is null)
        {
            throw new CapabilityNotSupportedException(
                SnapshotCapabilities.FieldValues,
                $"Interfaces need a full dump: {side ?? "this snapshot"} is a gcdump, which records no interfaces. Group by type, namespace or assembly, or capture a full dump.");
        }

        return await snapshots.GetInterfacesAsync(snapshotId, reader, progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotNotLoaded(snapshotId);
    }
}
