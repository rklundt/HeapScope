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
using System.Globalization;
using HeapScope.Analysis;
using HeapScope.Analysis.Sets;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Sprint 3.3 (ADR-032): objects matched by address, retained bytes and heap kinds in diffs, and trends.</summary>
internal sealed partial class WorkerService
{
    public Task<TypeTrendResult> QueryTypeTrendAsync(TypeTrendRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var ids = request.SnapshotIds ?? [];
            if (ids.Count is < 2 or > TypeTrendColumns.MaxSnapshots || ids.Distinct().Count() != ids.Count)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A trend takes 2 to {TypeTrendColumns.MaxSnapshots} different snapshots."), nameof(request));
            }

            if (!Enum.IsDefined(request.GroupBy))
            {
                throw new ArgumentOutOfRangeException(nameof(request), $"Unknown grouping {(int)request.GroupBy}.");
            }

            var stopwatch = Stopwatch.StartNew();
            var leases = new List<IDisposable>();
            try
            {
                var inputs = new List<(Core.Graph.HeapGraph Graph, TypeAggregation Aggregation, TypeInterfaces? Interfaces)>();
                var exact = true;
                var kind = SnapshotKind.FullDump;
                for (var i = 0; i < ids.Count; i++)
                {
                    var (snapshot, lease) = Lease(ids[i]);
                    leases.Add(lease);
                    progress?.Report(new JobProgress(string.Create(CultureInfo.InvariantCulture, $"Summarizing snapshot {i + 1} of {ids.Count}"), i, ids.Count));
                    var aggregation = await snapshots.GetAggregationAsync(ids[i], null, cancellationToken).ConfigureAwait(false) ?? throw SnapshotNotLoaded(ids[i]);
                    var interfaces = request.GroupBy == TypeGrouping.Interface
                        ? await GetInterfacesAsync(ids[i], snapshot, progress, cancellationToken, string.Create(CultureInfo.InvariantCulture, $"snapshot {i + 1}")).ConfigureAwait(false)
                        : null;
                    exact &= snapshot.Capabilities.HasFlag(SnapshotCapabilities.ExactCounts);
                    if (i > 0 && snapshot.Info.Kind != kind)
                    {
                        // Full dumps hold garbage and name nested types apart; gcdumps hold survivors only (the 3.3 review).
                        throw new ArgumentException("A trend takes snapshots of one kind: full dumps and gcdumps count different objects and name nested types differently. Compare a full dump with a gcdump in a two-snapshot comparison instead.");
                    }

                    kind = snapshot.Info.Kind;
                    inputs.Add((snapshot.Graph, aggregation, interfaces));
                }

                var source = await Task.Run(() => TypeTrendSource.Create(inputs, request), cancellationToken).ConfigureAwait(false);
                var rows = resultSets.Add(source);

                // What the trend rests on (decision 7): its snapshots in order, the grouping and the rows.
                logger.LogInformation(
                    "Trend of {Count} snapshots ({Ids}; heap bytes {Totals}) by {Grouping}: {Rows} of {Unfiltered} rows (the request's filter is logged by its shape), sort {Sort}; first rows {Largest}",
                    ids.Count,
                    string.Join(", ", ids),
                    string.Join(", ", inputs.Select(x => x.Aggregation.TotalBytes)),
                    request.GroupBy,
                    source.Count,
                    source.UnfilteredCount,
                    request.SortColumn ?? TypeTrendColumns.BytesDelta,
                    source.Describe(5));
                return new TypeTrendResult(rows, [.. inputs.Select(x => (long)x.Aggregation.TotalBytes)], source.UnfilteredCount, stopwatch.Elapsed.TotalSeconds, exact);
            }
            finally
            {
                leases.ForEach(l => l.Dispose());
            }
        });

    /// <summary>
    /// What a diff adds (decisions 1 to 5): object matches when asked and possible, retained bytes when asked, per-kind bytes
    /// when both snapshots have generations. The caller holds both snapshots' leases.
    /// </summary>
    private async Task<(DiffExtras Extras, Comparison Comparison)> DiffExtrasAsync(TypeDiffRequest request, ISnapshot baseline, ISnapshot current, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        // Refused before any work (the 3.3 review).
        if (request.IncludeRetained && request.GroupBy == TypeGrouping.Interface)
        {
            throw new ArgumentException("Interface rows have no retained bytes: a type counts under each interface it implements, so the rows overlap. Group by type, namespace or assembly.");
        }

        var comparison = new Comparison();
        long[]? newPerType = null, deadPerType = null;
        if (request.MatchObjects)
        {
            comparison.IdentityNote = WhyNoIdentity(baseline, current);
            if (comparison.IdentityNote is null)
            {
                var identity = await MatchAsync(request.BaselineId, baseline, current, progress, cancellationToken).ConfigureAwait(false);
                comparison.Identity = identity;
                comparison.IdentityNote = WhyNotOneRun(identity);
                if (comparison.IdentityNote is null)
                {
                    newPerType = identity.NewPerType(current.Graph, cancellationToken);
                    deadPerType = identity.DeadPerType(baseline.Graph, cancellationToken);
                    comparison.Matched = true;
                    comparison.NewObjects = newPerType.Sum();
                    comparison.DeadObjects = deadPerType.Sum();
                    comparison.Survivors = identity.Survivors;

                    // Matched, with a caution when few objects kept their address (the 3.3 review): a collection that
                    // compacted between the dumps, such as a forced one.
                    var smaller = Math.Min(baseline.Graph.ObjectCount, current.Graph.ObjectCount);
                    if (smaller > 0 && identity.Survivors * 10 < smaller)
                    {
                        comparison.IdentityNote = string.Create(CultureInfo.InvariantCulture, $"Only {identity.Survivors:N0} objects ({100.0 * identity.Survivors / smaller:N1}%) kept their address: a collection that compacted between the dumps (a forced GC, or a gcdump captured between them) moves most objects, which then count as dead and new.");
                    }
                }
            }

            // Who was compared and which rule decided (the 3.3 review), so a contested match can be backed up.
            logger.LogInformation(
                "Object matching of {Baseline} ({BaselineKind}, process {BaselineName} {BaselinePid}, captured {BaselineCaptured:O}) and {Current} ({CurrentKind}, process {CurrentName} {CurrentPid}, captured {CurrentCaptured:O}): {Outcome}; {Survivors} survivors, {Reused} addresses reused by other objects, {FrozenSurvivors} of {Frozen} frozen objects at their address",
                request.BaselineId,
                baseline.Info.Kind,
                baseline.Info.ProcessName ?? "unknown",
                baseline.Info.ProcessId,
                baseline.Info.CapturedAtUtc,
                request.CurrentId,
                current.Info.Kind,
                current.Info.ProcessName ?? "unknown",
                current.Info.ProcessId,
                current.Info.CapturedAtUtc,
                comparison.Matched ? "matched" + (comparison.IdentityNote is null ? string.Empty : ", with a caution") : comparison.IdentityNote,
                comparison.Identity?.Survivors,
                comparison.Identity?.ReusedAddresses,
                comparison.Identity?.FrozenSurvivors,
                comparison.Identity?.BaselineFrozen);
        }

        GroupRetained? baselineRetained = null, currentRetained = null;
        if (request.IncludeRetained)
        {
            (baselineRetained, comparison.BaselineDominators) = await GroupRetainedAsync(request.BaselineId, baseline, request.GroupBy, progress, cancellationToken).ConfigureAwait(false);
            (currentRetained, comparison.CurrentDominators) = await GroupRetainedAsync(request.CurrentId, current, request.GroupBy, progress, cancellationToken).ConfigureAwait(false);
        }

        var kinds = baseline.Capabilities.HasFlag(SnapshotCapabilities.Generations) && current.Capabilities.HasFlag(SnapshotCapabilities.Generations);
        return (new DiffExtras { NewPerCurrentType = newPerType, DeadPerBaselineType = deadPerType, BaselineRetained = baselineRetained, CurrentRetained = currentRetained, Kinds = kinds }, comparison);
    }

    /// <summary>Why two snapshots' objects cannot be matched by address (decision 1), or null when they can.</summary>
    private static string? WhyNoIdentity(ISnapshot baseline, ISnapshot current)
    {
        const string still = " Comparing by type still works.";
        if (baseline.Info.Kind != SnapshotKind.FullDump || current.Info.Kind != SnapshotKind.FullDump)
        {
            return "New and dead objects need two full dumps: a gcdump records objects after a collection it forced, which moves them, and says no process id." + still;
        }

        if (!baseline.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph) || !current.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph))
        {
            return "New and dead objects need complete dumps: a dump with missing heap segments would count the objects it lost as dead or new." + still;
        }

        var (before, after) = (baseline.Info, current.Info);
        if (before.ProcessId is null || after.ProcessId is null)
        {
            return "New and dead objects need both dumps to say which process they are of, and one of them does not." + still;
        }

        if (before.ProcessId != after.ProcessId || !string.Equals(before.ProcessName, after.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Create(CultureInfo.InvariantCulture, $"New and dead objects are matched by address, which only means something within one run of a process: these dumps are of {before.ProcessName} {before.ProcessId} and {after.ProcessName} {after.ProcessId}.{still}");
        }

        if (before.CapturedAtUtc is { } t0 && after.CapturedAtUtc is { } t1 && t1 < t0)
        {
            return "New and dead objects need the earlier dump as the baseline: the baseline was captured after the current one. Swap them in Compare (in the CLI, give the earlier dump first; for a dead set, open it in the earlier dump)." + still;
        }

        return null;
    }

    /// <summary>
    /// Why a match says the dumps are not of one run of the process (the 3.3 review): frozen objects never move or die
    /// in one run, so most of them must be at their addresses. A process id reused by another run (a container's pid 1)
    /// fails here. Null when the match looks like one run.
    /// </summary>
    private static string? WhyNotOneRun(ObjectIdentity identity) =>
        identity.BaselineFrozen >= 20 && identity.FrozenSurvivors * 2 < identity.BaselineFrozen
            ? string.Create(CultureInfo.InvariantCulture, $"These dumps do not look like one run of the process: only {identity.FrozenSurvivors:N0} of the baseline's {identity.BaselineFrozen:N0} frozen objects, which never move, are at their addresses (the process id was probably reused). Comparing by type still works.")
            : null;

    /// <summary>Matches two snapshots' objects inside a reservation of a bit per object of each.</summary>
    private async Task<ObjectIdentity> MatchAsync(Guid reserveIn, ISnapshot baseline, ISnapshot current, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        const string phase = "Matching objects by address";
        progress?.Report(new JobProgress(phase, 0, null));
        return await Task.Run(
            () =>
            {
                using var reservation = snapshots.Reserve(reserveIn, ObjectIdentity.WorkingBytes(baseline.Graph, current.Graph), phase)
                    ?? throw SnapshotNotLoaded(reserveIn);
                return ObjectIdentity.Match(baseline.Graph, current.Graph, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(GroupRetained Retained, DominatorIndexInfo Info)> GroupRetainedAsync(Guid id, ISnapshot snapshot, TypeGrouping grouping, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var (_, info) = await GetDominatorsAsync(id, progress, cancellationToken).ConfigureAwait(false);
        var bytes = await snapshots.GetGroupRetentionAsync(id, grouping.ToGroupKey(), progress, cancellationToken).ConfigureAwait(false)
            ?? throw SnapshotNotLoaded(id);
        TypeGroups.GroupOfType(snapshot.Graph, grouping.ToGroupKey(), out var names);
        return (new GroupRetained(bytes, names, grouping == TypeGrouping.Type), info);
    }

    /// <summary>
    /// The members of a <see cref="ObjectSetKind.Compared"/> set: the inner set in the snapshot queried, matched against the
    /// other snapshot (decision 2). New and survived sets are queried in the later snapshot, dead ones in the earlier.
    /// </summary>
    private async Task<(ObjectSet Set, string Detail)> ResolveComparedAsync(Guid snapshotId, ISnapshot snapshot, ObjectSetSpec spec, IProgress<JobProgress>? progress, CancellationToken cancellationToken, int depth)
    {
        var innerSpec = spec.Inner ?? throw new ArgumentException("A compared set needs its inner set.");
        var change = spec.Change ?? throw new ArgumentException("A compared set needs its change: new, dead or survived.");
        var otherId = spec.Baseline ?? throw new ArgumentException("A compared set needs the snapshot it is compared with.");
        if (otherId == snapshotId)
        {
            throw new ArgumentException("A snapshot cannot be compared with itself.");
        }

        if (!Enum.IsDefined(change))
        {
            throw new ArgumentException($"Unknown change {(int)change}.");
        }

        var (other, otherLease) = Lease(otherId);
        using var _ = otherLease;
        var dead = change == ObjectChange.Dead;
        var (baseline, current) = dead ? (snapshot, other) : (other, snapshot);
        if (WhyNoIdentity(baseline, current) is { } why)
        {
            throw new ArgumentException(why);
        }

        var inner = await ResolveSetAsync(snapshotId, snapshot, innerSpec, progress, cancellationToken, depth + 1).ConfigureAwait(false);
        var identity = await MatchAsync(snapshotId, baseline, current, progress, cancellationToken).ConfigureAwait(false);
        if (WhyNotOneRun(identity) is { } notOneRun)
        {
            throw new ArgumentException(notOneRun);
        }

        var kind = change switch
        {
            ObjectChange.Dead => ObjectChangeKind.Dead,
            ObjectChange.Survived => ObjectChangeKind.Survived,
            _ => ObjectChangeKind.New,
        };
        var graph = snapshot.Graph;
        var set = await Task.Run(() => Reserved(snapshotId, graph, () => ObjectSet.OfIndices(graph, identity.Select(inner, kind).AsSpan())), cancellationToken).ConfigureAwait(false);
        return (set, string.Create(CultureInfo.InvariantCulture, $"{set.Count} of the inner set's {inner.Count} objects are {change.ToString().ToLowerInvariant()} against snapshot {otherId} ({identity.Survivors} objects matched by address in all)"));
    }

    private void LogDiff(TypeDiffRequest request, Comparison comparison, TypeDiffSource source) =>
        logger.LogInformation(
            "Diff extras of {Baseline} and {Current} by {Grouping}: objects matched {Matched} ({Why}), {New} new, {Dead} dead, {Survivors} survived by address; retained {Retained} (baseline {BaselineDominators}, current {CurrentDominators}); {Columns} columns; first rows {Rows}",
            request.BaselineId,
            request.CurrentId,
            request.GroupBy,
            comparison.Matched,
            comparison.IdentityNote ?? (request.MatchObjects ? "same process" : "not asked"),
            comparison.NewObjects,
            comparison.DeadObjects,
            comparison.Survivors,
            request.IncludeRetained,
            comparison.BaselineDominators is { } b ? Provenance.Describe(b) : "none",
            comparison.CurrentDominators is { } c ? Provenance.Describe(c) : "none",
            source.Columns.Count,
            source.Describe(10));

    /// <summary>What a diff found beside its rows.</summary>
    private sealed class Comparison
    {
        public bool Matched { get; set; }

        public string? IdentityNote { get; set; }

        public long NewObjects { get; set; }

        public long DeadObjects { get; set; }

        public long Survivors { get; set; }

        public ObjectIdentity? Identity { get; set; }

        public DominatorIndexInfo? BaselineDominators { get; set; }

        public DominatorIndexInfo? CurrentDominators { get; set; }
    }
}
