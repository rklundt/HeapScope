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
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Heap queries (<see cref="IHeapQueryService"/>): type summaries, diffs (with objects matched by address, retained bytes
/// and heap kinds, sprint 3.3, ADR-032) and trends, instances, single objects, their references and their referrers.
/// </summary>
internal sealed class HeapQueryHandler(
    SnapshotStore snapshots,
    ResultSetStore resultSets,
    SnapshotAccess access,
    DerivedQueries derived,
    IdentityMatcher identities,
    ObjectSetResolver resolver,
    ILogger<HeapQueryHandler> logger) : RpcHandler(logger), IHeapQueryService
{
    /// <summary>Array elements listed when an object is inspected (more are summarized in one row).</summary>
    private const int MaxInspectedElements = 100;

    /// <summary>Characters of a string returned with its object.</summary>
    private const int MaxInspectedText = 10_000;

    /// <summary>Characters of a string shown in a field (longer ones end with "...").</summary>
    private const int MaxFieldText = 1_000;

    public Task<TypeSummaryResult> QueryTypeSummaryAsync(TypeSummaryRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();
            var snapshot = snapshots.TryGet(request.SnapshotId) ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);

            // The lease keeps a mapped graph alive while the aggregation and the grouping read it; the result source
            // itself holds only names and totals. A snapshot closed between the lookup and here refuses the lease.
            SnapshotLease lease;
            try
            {
                lease = snapshot.AcquireLease();
            }
            catch (ObjectDisposedException)
            {
                throw SnapshotAccess.NotLoaded(request.SnapshotId);
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

                set = await resolver.ResolveAsync(request.SnapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false);
                var members = set;
                aggregation = await Task.Run(() => Analysis.TypeAggregation.Compute(snapshot.Graph, members, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                aggregation = await snapshots.GetAggregationAsync(
                    request.SnapshotId,
                    new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", done, total))),
                    cancellationToken).ConfigureAwait(false) ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);
            }
            var generations = FeatureRules.WhyUnavailable(SnapshotFeatureIds.Generations, snapshot) is null;
            Analysis.TypeInterfaces? interfaces = null;
            if (request.GroupBy == TypeGrouping.Interface)
            {
                // Interface groups overlap, so they have no retained lower bound (decision 6); a gcdump is refused first.
                interfaces = await derived.GetInterfacesAsync(request.SnapshotId, snapshot, progress, cancellationToken).ConfigureAwait(false);
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
                dominators = (await derived.GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false)).Info;
                retained = await snapshots.GetGroupRetentionAsync(request.SnapshotId, request.GroupBy.ToGroupKey(), progress, cancellationToken).ConfigureAwait(false)
                    ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);
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
            var (baseline, baselineLease) = access.Lease(request.BaselineId);
            using var _ = baselineLease;
            var (current, currentLease) = access.Lease(request.CurrentId);
            using var __ = currentLease;
            var throttled = new ThrottledProgress<JobProgress>(progress);
            var total = baseline.Graph.ObjectCount + (long)current.Graph.ObjectCount;
            long baselineDone = 0;
            var baselineAggregation = await snapshots.GetAggregationAsync(
                request.BaselineId,
                new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", baselineDone = done, total))),
                cancellationToken).ConfigureAwait(false) ?? throw SnapshotAccess.NotLoaded(request.BaselineId);
            var currentAggregation = await snapshots.GetAggregationAsync(
                request.CurrentId,
                new CallbackProgress(done => throttled.Report(new JobProgress("Summarizing types", baselineDone + done, total))),
                cancellationToken).ConfigureAwait(false) ?? throw SnapshotAccess.NotLoaded(request.CurrentId);
            Analysis.TypeInterfaces? baselineInterfaces = null, currentInterfaces = null;
            if (request.GroupBy == TypeGrouping.Interface)
            {
                baselineInterfaces = await derived.GetInterfacesAsync(request.BaselineId, baseline, progress, cancellationToken, "the baseline").ConfigureAwait(false);
                currentInterfaces = await derived.GetInterfacesAsync(request.CurrentId, current, progress, cancellationToken, "the current snapshot").ConfigureAwait(false);
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
                    var (snapshot, lease) = access.Lease(ids[i]);
                    leases.Add(lease);
                    progress?.Report(new JobProgress(string.Create(CultureInfo.InvariantCulture, $"Summarizing snapshot {i + 1} of {ids.Count}"), i, ids.Count));
                    var aggregation = await snapshots.GetAggregationAsync(ids[i], null, cancellationToken).ConfigureAwait(false) ?? throw SnapshotAccess.NotLoaded(ids[i]);
                    var interfaces = request.GroupBy == TypeGrouping.Interface
                        ? await derived.GetInterfacesAsync(ids[i], snapshot, progress, cancellationToken, string.Create(CultureInfo.InvariantCulture, $"snapshot {i + 1}")).ConfigureAwait(false)
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
                Logger.LogInformation(
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

    public Task<InstancesResult> QueryInstancesAsync(InstancesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var stopwatch = Stopwatch.StartNew();

            // The set keeps the lease: its pages read the graph and the dump (string values) while it lives.
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            try
            {
                snapshot.TryGetService<IObjectReader>(out var reader);
                if (request.Scope == InstanceScope.Finalizable)
                {
                    // Without recorded registrations the scope would silently hold nothing.
                    FeatureRules.Require(SnapshotFeatureIds.FinalizableScope, snapshot);
                }

                DominatorIndex? dominators = null;
                DominatorIndexInfo? info = null;
                // Sprint 2.3 review M7: a bad request is refused, and a type with no objects answered, before any dominators.
                var valid = InstancesSource.Validate(snapshot.Graph, request);
                var set = request.Set is { } spec ? await resolver.ResolveAsync(request.SnapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false) : null;
                if (valid && request.IncludeRetained)
                {
                    // Sprint 2.3: attached to the graph, so the set's lease keeps them while its pages are read.
                    (dominators, info) = await derived.GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
                }

                var throttled = new ThrottledProgress<JobProgress>(progress);
                var source = await Task.Run(() => InstancesSource.Create(snapshot.Graph, reader, request, cancellationToken, throttled, dominators?.Dominators, set), cancellationToken).ConfigureAwait(false);
                var rows = resultSets.Add(source, lease);
                return new InstancesResult(rows, source.TotalObjects, source.TotalBytes, source.MatchedTypes, reader is not null, stopwatch.Elapsed.TotalSeconds, info, source.Note);
            }
            catch (CapabilityNotSupportedException ex) when (ex.Missing != SnapshotCapabilities.None)
            {
                // The source knows what is missing (string text for a value filter or sort); the feature's rule words it.
                lease.Dispose();
                throw new CapabilityNotSupportedException(ex.Missing, FeatureRules.ExplainMissing(ex.Missing, snapshot));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });

    public Task<ObjectDetails> GetObjectAsync(ObjectRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            progress?.Report(new JobProgress("Reading the object", 0, null));
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            if (!graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            var type = graph.TypeIndices[index];
            IReadOnlyList<ObjectFieldDto>? fields = null;
            var unavailable = FeatureRules.WhyUnavailable(SnapshotFeatureIds.FieldValues, snapshot);
            string? text = null;
            if (unavailable is null && snapshot.TryGetService<IObjectReader>(out var reader))
            {
                (fields, text) = await Task.Run(
                    () =>
                    {
                        var read = reader.ReadFields(request.Address, MaxInspectedElements, MaxFieldText, cancellationToken).Select(ObjectFormatter.ToDto).ToArray();
                        var str = graph.Types.Kinds[type].HasFlag(Core.Graph.TypeKind.String) ? reader.ReadString(request.Address, MaxInspectedText) : null;
                        return ((IReadOnlyList<ObjectFieldDto>)read, str);
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return new ObjectDetails(
                request.Address,
                graph.Types.GetName(type),
                (long)graph.GetSize(index),
                graph.HeapKinds[index].ToDto(),
                graph.GetReferences(index).Length,
                graph.Roots.IsRetained(index),
                fields,
                unavailable,
                text);
        });

    public Task<ResultSetInfo> QueryReferencesAsync(ReferencesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            progress?.Report(new JobProgress("Naming references", 0, null));
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            if (!snapshot.Graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            snapshot.TryGetService<IObjectReader>(out var reader);
            var source = await Task.Run(() => ReferencesSource.Create(snapshot.Graph, reader, index, cancellationToken), cancellationToken).ConfigureAwait(false);

            // The source formats pages from the graph, so the set keeps a lease of its own.
            var owner = snapshot.AcquireLease();
            return resultSets.Add(source, owner);
        });

    public Task<ReferrersResult> QueryReferrersAsync(ReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);

            // The set keeps the lease: its pages read the graph. The reverse index is attached to the graph, so the lease
            // also keeps it while the rows are copied out of it.
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            try
            {
                if (!snapshot.Graph.TryGetIndex(request.Address, out var index))
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
                }

                var (reverse, info) = await derived.GetReverseAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
                snapshot.TryGetService<IObjectReader>(out var reader);
                var noReader = FeatureRules.ExplainMissing(SnapshotCapabilities.FieldValues, snapshot);
                var throttled = new ThrottledProgress<JobProgress>(progress);
                var source = await Task.Run(
                    () => ReferrersSource.Create(snapshot.Graph, reverse.References, reader, index, noReader, throttled, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                throttled.Flush();
                return new ReferrersResult(resultSets.Add(source, lease), source.ReferenceCount, source.FieldsNote, info, SnapshotAccess.IsComplete(snapshot));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });

    public Task<TypeReferrersResult> QueryTypeReferrersAsync(TypeReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrEmpty(request.TypeName))
            {
                throw new ArgumentException("A type name is required.", nameof(request));
            }

            var (snapshot, lease) = access.Lease(request.SnapshotId);
            try
            {
                // An unknown sort column or type is answered before the index is built for nothing (review M8).
                if (TypeReferrersSource.EmptyUnlessMatched(snapshot.Graph, request) is { } none)
                {
                    return new TypeReferrersResult(resultSets.Add(none, lease), 0, 0, 0, null, SnapshotAccess.IsComplete(snapshot));
                }

                var (reverse, info) = await derived.GetReverseAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new JobProgress("Grouping referrers by type", 0, null));
                var source = await Task.Run(() => TypeReferrersSource.Create(snapshot.Graph, reverse.References, request, cancellationToken), cancellationToken).ConfigureAwait(false);
                return new TypeReferrersResult(resultSets.Add(source, lease), source.MatchedTypes, source.TargetObjects, source.ReferenceCount, info, SnapshotAccess.IsComplete(snapshot));
            }
            catch
            {
                lease.Dispose();
                throw;
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
            comparison.IdentityNote = IdentityMatcher.WhyNoIdentity(baseline, current);
            if (comparison.IdentityNote is null)
            {
                var identity = await identities.MatchAsync(request.BaselineId, baseline, current, progress, cancellationToken).ConfigureAwait(false);
                comparison.Identity = identity;
                comparison.IdentityNote = IdentityMatcher.WhyNotOneRun(identity);
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
            Logger.LogInformation(
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
            (baselineRetained, comparison.BaselineDominators) = await derived.GroupRetainedAsync(request.BaselineId, baseline, request.GroupBy, progress, cancellationToken).ConfigureAwait(false);
            (currentRetained, comparison.CurrentDominators) = await derived.GroupRetainedAsync(request.CurrentId, current, request.GroupBy, progress, cancellationToken).ConfigureAwait(false);
        }

        var kinds = baseline.Capabilities.HasFlag(SnapshotCapabilities.Generations) && current.Capabilities.HasFlag(SnapshotCapabilities.Generations);
        return (new DiffExtras { NewPerCurrentType = newPerType, DeadPerBaselineType = deadPerType, BaselineRetained = baselineRetained, CurrentRetained = currentRetained, Kinds = kinds }, comparison);
    }

    private void LogDiff(TypeDiffRequest request, Comparison comparison, TypeDiffSource source) =>
        Logger.LogInformation(
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
