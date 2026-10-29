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
    // Each snapshot's interfaces per type index, read from the dump once (decision 6).
    private readonly ConcurrentDictionary<Guid, Lazy<Task<string[][]>>> _interfaces = new();

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
            var groups = await Task.Run(
                () =>
                {
                    using var reservation = snapshots.Reserve(request.SnapshotId, (long)SimilarRetention.WorkingBytesPerMember * set.Count, phase)
                        ?? throw SnapshotNotLoaded(request.SnapshotId);
                    return SimilarRetention.Group(graph, tree, reverse.References, set, request.MaxGroups, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
            var result = new SimilarRetentionResult(
                [.. groups.Groups.Select((g, rank) => new RetentionGroupDto(
                    rank,
                    g.Objects,
                    (long)g.Bytes,
                    g.Types,
                    graph.Types.GetName(g.FirstType),
                    g.PathCount,
                    [.. g.Shown.Select(p => RetentionMapping.KeyPath(graph, p))],
                    graph.Addresses[g.ExampleObject]))],
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

            // What the view rests on (decision 7): the set, the groups, and the largest groups with the paths shown.
            logger.LogInformation(
                "Similar retention: {Shape} in snapshot {Id}, {Members} members in {Groups} groups ({Unreachable} unreachable, {Limited} with more than {Max} referrers) in {Seconds:0.00} s; largest {Largest}",
                request.Set.Shape,
                request.SnapshotId,
                set.Count,
                result.GroupCount,
                result.Unreachable,
                result.LimitedMembers,
                SimilarRetention.MaxReferrers,
                result.Seconds,
                string.Join("; ", result.Groups.Take(5).Select(g => string.Create(CultureInfo.InvariantCulture, $"#{g.Rank} {g.Objects} objects, {g.Bytes} bytes, {g.PathCount} paths: {string.Join(" | ", g.Paths.Select(Provenance.Describe))}"))));
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
        var inner = await ResolveSetAsync(snapshotId, snapshot, innerSpec, progress, cancellationToken, depth + 1).ConfigureAwait(false);
        RequireMembers(inner);
        var (tree, reverse) = await RetentionInputsAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false);
        var graph = snapshot.Graph;
        var set = await Task.Run(
            () =>
            {
                using var reservation = snapshots.Reserve(snapshotId, ((long)SimilarRetention.WorkingBytesPerMember * inner.Count) + ObjectSet.WorkingBytes(graph), "Resolving a retention group")
                    ?? throw SnapshotNotLoaded(snapshotId);
                try
                {
                    return ObjectSet.OfIndices(graph, SimilarRetention.MembersOf(graph, tree, reverse.References, inner, spec.Row, cancellationToken).AsSpan());
                }
                finally
                {
                    MemoryReservations.Collect();
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (set.Count == 0)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The set has no retention group {spec.Row}."));
        }

        return (set, string.Create(CultureInfo.InvariantCulture, $"group {spec.Row} of the inner set's {inner.Count} objects"));
    }

    /// <summary>
    /// The interfaces of each type of a snapshot, read once from the dump through one object of each type (decision 6).
    /// gcdumps and snapshots without a dump reader refuse.
    /// </summary>
    private async Task<string[][]> GetInterfacesAsync(Guid snapshotId, ISnapshot snapshot, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (!snapshot.Capabilities.HasFlag(SnapshotCapabilities.FieldValues) || !snapshot.TryGetService<IObjectReader>(out var reader) || reader is null)
        {
            throw new CapabilityNotSupportedException(SnapshotCapabilities.FieldValues, "Grouping by interface needs a full dump: a gcdump records no interfaces.");
        }

        while (true)
        {
            var shared = _interfaces.GetOrAdd(snapshotId, _ => new Lazy<Task<string[][]>>(() => Task.Run(() => ReadInterfaces(snapshotId, snapshot.Graph, reader, progress, cancellationToken), cancellationToken)));
            try
            {
                return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _interfaces.TryRemove(new KeyValuePair<Guid, Lazy<Task<string[][]>>>(snapshotId, shared));
            }
            catch
            {
                _interfaces.TryRemove(new KeyValuePair<Guid, Lazy<Task<string[][]>>>(snapshotId, shared));
                throw;
            }
        }
    }

    private string[][] ReadInterfaces(Guid snapshotId, Core.Graph.HeapGraph graph, IObjectReader reader, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var typeCount = graph.Types.Count;
        var example = new int[typeCount];
        Array.Fill(example, -1);
        var typeIndices = graph.TypeIndices.Span;
        var found = 0;
        for (var o = 0; o < typeIndices.Length && found < typeCount; o++)
        {
            if (example[typeIndices[o]] < 0)
            {
                example[typeIndices[o]] = o;
                found++;
            }
        }

        const string phase = "Reading the types' interfaces";
        var interfaces = new string[typeCount][];
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var failed = 0;
        for (var t = 0; t < typeCount; t++)
        {
            if ((t & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new JobProgress(phase, t, typeCount));
            }

            interfaces[t] = [];
            if (example[t] < 0)
            {
                continue;
            }

            try
            {
                // One string per interface name across the table.
                interfaces[t] = [.. (reader.ReadInterfaces(graph.Addresses[example[t]]) ?? []).Select(n => names.TryGetValue(n, out var known) ? known : names[n] = n)];
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
            {
                failed++;
            }
        }

        logger.LogInformation(
            "Interfaces of snapshot {Id}: {Types} types read, {Failed} failed, {Interfaces} distinct interfaces in {Seconds:0.00} s",
            snapshotId,
            found,
            failed,
            names.Count,
            stopwatch.Elapsed.TotalSeconds);
        return interfaces;
    }
}
