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
using HeapScope.Analysis.Retention;
using HeapScope.Analysis.Sets;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Retention (<see cref="IRetentionService"/>): why an object or the objects of a type or a set are alive (retention paths,
/// Similar Retention), what they retain (dominators, a set's exact retention and its top members), and the dominator tree
/// explorer and icicle (ADR-027).
/// </summary>
internal sealed class RetentionHandler(
    SnapshotStore snapshots,
    ResultSetStore resultSets,
    SnapshotAccess access,
    DerivedQueries derived,
    ObjectSetResolver resolver,
    ILogger<RetentionHandler> logger) : RpcHandler(logger), IRetentionService
{
    /// <summary>Rows of the revealed list (or the top) and trail entries written to the provenance line.</summary>
    private const int LoggedRows = 10;

    /// <summary>Expanded keys written to the provenance line.</summary>
    private const int LoggedKeys = 20;

    public Task<ObjectPathsResult> QueryObjectPathsAsync(ObjectPathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.MaxRoots is < 1 or > 50)
            {
                throw new ArgumentException("MaxRoots must be between 1 and 50.", nameof(request));
            }

            // The lease keeps the graph, and the reverse index attached to it, while the paths are found and named.
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            if (!snapshot.Graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            var (reverse, info) = await derived.GetReverseAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);

            // Whether a root reaches the object is decided by the root path tree, which is exact; the bounded search only
            // finds the paths, and falls back to the tree's path when it stops before a root (review H2).
            var rootPaths = await snapshots.GetRootPathTreeAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false)
                ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);
            snapshot.TryGetService<IObjectReader>(out var reader);
            progress?.Report(new JobProgress("Finding retention paths", 0, null));
            var fallback = false;
            var result = await Task.Run(
                () =>
                {
                    var tree = RetentionSearch.Find(snapshot.Graph, reverse.References, index, request.MaxRoots, cancellationToken: cancellationToken, paths: rootPaths);
                    if (!tree.IsReachable && rootPaths.IsReachable(index))
                    {
                        tree = RetentionSearch.FromRootPath(snapshot.Graph, rootPaths, index);
                        fallback = true;
                    }

                    var noReader = reader is null ? FeatureRules.ExplainMissing(SnapshotCapabilities.FieldValues, snapshot) : null;
                    return RetentionMapping.ObjectPaths(snapshot.Graph, tree, reader, noReader, SnapshotAccess.IsComplete(snapshot), info, cancellationToken, snapshot.Info.Kind);
                },
                cancellationToken).ConfigureAwait(false);

            // Which roots hold the object, as shown (sprint 2.3.7 review H3): a disputed "held by a static field" is
            // answered from the log, not by running the query again.
            Logger.LogInformation(
                "Retention paths of 0x{Address:x} in snapshot {Id}: reachable {Reachable}, {Visited} objects visited, stopped early {StoppedEarly}, truncated {Truncated}, root path fallback {Fallback}; roots {Roots}; key paths {KeyPaths}",
                request.Address,
                request.SnapshotId,
                result.Reachable,
                result.Visited,
                result.StoppedEarly,
                result.Truncated,
                fallback,
                string.Join("; ", result.Nodes.Where(n => n.Root is not null).Select(n => string.Create(CultureInfo.InvariantCulture, $"{Provenance.Describe(n.Root!)} at depth {n.Depth}"))),
                string.Join("; ", result.KeyPaths.Select(Provenance.Describe)));
            return result;
        });

    public Task<TypePathsResult> QueryTypePathsAsync(TypePathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrEmpty(request.TypeName) && request.Set is null)
            {
                throw new ArgumentException("A type name or a set is required.", nameof(request));
            }

            if (request.MaxPaths is < 1 or > 100)
            {
                throw new ArgumentException("MaxPaths must be between 1 and 100.", nameof(request));
            }

            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            bool[]? types = null;
            int matched;
            ObjectSet? members = null;
            if (request.Set is { } spec)
            {
                // A set's key paths (sprint 3.2): the 2.2 grouping over its members.
                members = await resolver.ResolveAsync(request.SnapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false);
                ObjectSetResolver.RequireMembers(members);
                matched = members.TypeCount;
            }
            else
            {
                types = TypeGroups.MatchTypes(graph, request.TypeName, request.Module, out matched);
                if (matched == 0)
                {
                    // Answered before the tree is built for nothing (2.1 review M8).
                    return new TypePathsResult([], 0, 0, 0, 0, 0, 0, 0, SnapshotAccess.IsComplete(snapshot), 0, RetentionMapping.UnreachableNote(snapshot.Info.Kind));
                }
            }

            var stopwatch = Stopwatch.StartNew();
            var tree = await snapshots.GetRootPathTreeAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false)
                ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);
            var treeSeconds = stopwatch.Elapsed.TotalSeconds;
            progress?.Report(new JobProgress("Grouping objects by retention path", 0, null));
            var set = await Task.Run(
                () => members is null
                    ? KeyPaths.ForTypes(graph, tree, types!, request.MaxPaths, cancellationToken)
                    : KeyPaths.ForMembers(graph, tree, members.Members, request.MaxPaths, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var typePaths = new TypePathsResult(
                [.. set.Paths.Select(p => RetentionMapping.KeyPath(graph, p))],
                matched,
                set.Objects,
                set.Unreachable,
                (long)set.UnreachableBytes,
                set.OtherPaths,
                set.OtherObjects,
                (long)set.OtherBytes,
                SnapshotAccess.IsComplete(snapshot),
                treeSeconds,
                RetentionMapping.UnreachableNote(snapshot.Info.Kind));

            // The key paths as shown, with their roots (sprint 2.3.7 review H3).
            Logger.LogInformation(
                "Retention paths of {Subject} in snapshot {Id}: {Objects} objects, {Unreachable} unreachable, {Other} further paths; key paths {KeyPaths}",
                request.Set is { } asked ? asked.Shape : $"type {request.TypeName} ({request.Module ?? "no module"})",
                request.SnapshotId,
                typePaths.Objects,
                typePaths.Unreachable,
                typePaths.OtherPaths,
                string.Join("; ", typePaths.Paths.Select(Provenance.Describe)));
            return typePaths;
        });

    public Task<RetainedResult> QueryRetainedAsync(RetainedRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);

            // The lease keeps the graph, and the dominators attached to it, while they are read.
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            if (!graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            var (dominators, info) = await derived.GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
            var idom = dominators.Dominators.ImmediateDominator(index);
            return new RetainedResult(
                request.Address,
                graph.Types.GetName(graph.TypeIndices[index]),
                (long)graph.GetSize(index),
                idom != DominatorTree.Unreachable,
                (long)dominators.Dominators.RetainedSize(index),
                idom >= 0 ? graph.Addresses[idom] : 0,
                idom >= 0 ? graph.Types.GetName(graph.TypeIndices[idom]) : null,
                idom == DominatorTree.VirtualRoot,
                RetentionMapping.UnreachableNote(snapshot.Info.Kind),
                info,
                SnapshotAccess.IsComplete(snapshot));
        });

    public Task<DominatorTreeResult> QueryDominatorTreeAsync(DominatorTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.MaxChildren is < DominatorTreeLimits.MinChildren or > DominatorTreeLimits.MaxChildren)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Children per row must be between {DominatorTreeLimits.MinChildren} and {DominatorTreeLimits.MaxChildren:N0}."));
            }

            if (request.Expanded is null || request.Expanded.Count > DominatorTreeLimits.MaxExpanded)
            {
                throw new ArgumentException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"At most {DominatorTreeLimits.MaxExpanded:N0} rows can be open at once. Collapse some rows, or focus on one to see the tree below it."));
            }

            // The rows hold no graph data (addresses are copied per row), so the set needs no lease of its own.
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var focus = FocusIndex(graph, request.FocusAddress);
            var (tree, info) = await FocusAsync(request.SnapshotId, snapshot, request.FocusAddress, focus, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new JobProgress("Listing retainers", 0, null));
            var grouping = request.Grouping switch
            {
                DominatorGroupingDto.Type => DominatorGrouping.Type,
                DominatorGroupingDto.Namespace => DominatorGrouping.Namespace,
                _ => DominatorGrouping.None,
            };
            var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
            var groups = await derived.GetDominatorGroupsAsync(request.SnapshotId, grouping, cancellationToken).ConfigureAwait(false);
            var view = await Task.Run(() => DominatorTreeRows.Build(graph, tree, focus, expanded, groups, request.MaxChildren, cancellationToken), cancellationToken).ConfigureAwait(false);
            var reveal = -1;
            for (var i = 0; request.RevealKey is not null && i < view.Rows.Count; i++)
            {
                if (view.Rows[i].Key == request.RevealKey)
                {
                    reveal = i;
                    break;
                }
            }

            var ancestors = view.Ancestors.Select(a => new DominatorAncestorDto(DominatorTreeRows.ObjectKey(graph.Addresses[a]), graph.Types.GetName(graph.TypeIndices[a]), graph.Addresses[a], (long)tree.RetainedSize(a))).ToArray();

            // What the user looked at (sprint 2.3.7 review H3; the 2.4 review): the rows of the list just opened (or the
            // top), the trail and the open keys, so a disputed row or trail is answered from the log. Types, addresses and
            // sizes only (11).
            string Describe(DominatorRow r) => string.Create(CultureInfo.InvariantCulture, $"{r.Kind} {r.Name}{(r.Object >= 0 ? $" 0x{graph.Addresses[r.Object]:x}" : string.Empty)} retains {r.Retained}");
            var shown = reveal >= 0 && view.Rows[reveal].Expanded
                ? view.Rows.Where(r => r.ParentKey == request.RevealKey).Take(LoggedRows)
                : view.Rows.Take(LoggedRows);
            Logger.LogInformation(
                "Dominator tree of snapshot {Id}: focus {Focus}, grouping {Grouping}, {Rows} rows{Truncated}, top level {TopLevel}, total retained {Total} bytes; trail ({Depth} dominators) {Trail}; {Open} open rows {Keys}; reveal {Reveal} at row {RevealIndex}; rows shown under it {Shown}",
                request.SnapshotId,
                focus >= 0 ? string.Create(CultureInfo.InvariantCulture, $"0x{request.FocusAddress:x}") : "root",
                grouping,
                view.Rows.Count,
                view.Truncated ? " (cut at the row limit)" : string.Empty,
                tree.ChildCount(tree.Root),
                view.TotalRetained,
                view.AncestorDepth,
                string.Join(" > ", ancestors.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Name} 0x{a.Address:x} ({a.Retained})"))),
                request.Expanded.Count,
                string.Join(", ", request.Expanded.Take(LoggedKeys)),
                request.RevealKey ?? "none",
                reveal,
                string.Join("; ", shown.Select(Describe)));
            var rowAddresses = view.Rows.Select(r => r.Object >= 0 ? graph.Addresses[r.Object] : 0).ToArray();
            return new DominatorTreeResult(
                resultSets.Add(new DominatorTreeSource(view, rowAddresses)),
                focus >= 0 ? DominatorTreeRows.ObjectKey(request.FocusAddress) : null,
                ancestors,
                (long)view.FocusRetained,
                (long)view.TotalRetained,
                tree.ChildCount(tree.Root),
                view.Truncated,
                info,
                view.AncestorDepth,
                reveal);
        });

    public Task<RetainedBySetResult> QueryRetainedBySetAsync(RetainedBySetRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);

            // Any object set since sprint 3.1; a type name stays the short form of a one-type set.
            var spec = request.Set ?? (string.IsNullOrEmpty(request.TypeName)
                ? throw new ArgumentException("A type name or a set is required.", nameof(request))
                : ObjectSetSpec.OfType(request.TypeName, request.Module));
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;

            // Resolved, and checked for members, before anything is computed (2.3 review M7; the 2.4 review).
            var set = await resolver.ResolveAsync(request.SnapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false);
            ObjectSetResolver.RequireMembers(set);
            var matched = set.TypeCount;

            var (dominators, info) = await derived.GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
            var tree = dominators.Dominators;
            var aggregation = await snapshots.GetAggregationAsync(request.SnapshotId, null, cancellationToken).ConfigureAwait(false)
                ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);

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
                        phase) ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);
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
            Logger.LogInformation(
                "Retained by set: {Shape} in snapshot {Id}, {Matched} types among the members, {AllObjects} objects of which {SetObjects} reachable, of {SetBytes} bytes, retain {Objects} objects of {Bytes} bytes (lower bound {LowerBound}, heap {Total}) in {Seconds:0.00} s; first rows {First}",
                Describe(spec),
                request.SnapshotId,
                matched,
                result.AllObjects,
                result.SetObjects,
                result.SetBytes,
                result.RetainedObjects,
                result.RetainedBytes,
                spec.Kind == ObjectSetKind.Types ? lowerBound.ToString(CultureInfo.InvariantCulture) : "n/a",
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

    public Task<SetDominatorsResult> QuerySetDominatorsAsync(SetDominatorsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Set);
            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var set = await resolver.ResolveAsync(request.SnapshotId, snapshot, request.Set, progress, cancellationToken).ConfigureAwait(false);
            ObjectSetResolver.RequireMembers(set);
            var (dominators, info) = await derived.GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
            var tree = dominators.Dominators;
            var stopwatch = Stopwatch.StartNew();
            const string phase = "Finding the set's dominators";
            progress?.Report(new JobProgress(phase, 0, null));
            var tops = await Task.Run(
                () =>
                {
                    // The walks, the tops' arrays and their sort all run inside the reservation (the 3.1 review).
                    using var reservation = snapshots.Reserve(request.SnapshotId, (long)SetDominators.WorkingBytesPerObject * graph.ObjectCount, phase)
                        ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);
                    try
                    {
                        return SetDominators.Top(tree, set, cancellationToken);
                    }
                    finally
                    {
                        MemoryReservations.Collect();
                    }
                },
                cancellationToken).ConfigureAwait(false);
            var source = new SetDominatorsSource(graph, tree, tops);
            long topRetained = 0;
            foreach (var top in tops.Tops)
            {
                topRetained += (long)tree.RetainedSize(top);
            }

            // What the view rests on (2.3.7 review H3): the set, the top members and their bytes, the largest first.
            Logger.LogInformation(
                "Set dominators: {Shape} in snapshot {Id}, {Members} members ({Unreachable} unreachable), {Tops} top members retain {Retained} bytes in {Seconds:0.00} s; largest {Largest}",
                request.Set.Shape,
                request.SnapshotId,
                set.Count,
                tops.UnreachableMembers,
                tops.Count,
                topRetained,
                stopwatch.Elapsed.TotalSeconds,
                source.Describe(5));

            // The rows read the graph and the dominators while they live, so they keep a lease of their own (the 3.1 review).
            return new SetDominatorsResult(resultSets.Add(source, snapshot.AcquireLease()), ObjectSetResolver.Info(set), tops.Count, topRetained, tops.UnreachableMembers, info);
        });

    public Task<SimilarRetentionResult> QuerySimilarRetentionAsync(SimilarRetentionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Set);
            if (request.MaxGroups is < 1 or > SimilarRetentionLimits.MaxGroups)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"MaxGroups must be between 1 and {SimilarRetentionLimits.MaxGroups}."), nameof(request));
            }

            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var set = await resolver.ResolveAsync(request.SnapshotId, snapshot, request.Set, progress, cancellationToken).ConfigureAwait(false);
            ObjectSetResolver.RequireMembers(set);
            var stopwatch = Stopwatch.StartNew();
            var (tree, reverse) = await derived.RetentionInputsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
            const string phase = "Grouping objects by retention pattern";
            progress?.Report(new JobProgress(phase, 0, null));
            var groupStopwatch = Stopwatch.StartNew();
            var groups = await Task.Run(
                () =>
                {
                    // The member groups, the patterns and the signature memo (the 3.2 review), compacted after.
                    using var reservation = snapshots.Reserve(request.SnapshotId, SimilarRetention.WorkingBytes(set.Count, graph.ObjectCount), phase)
                        ?? throw SnapshotAccess.NotLoaded(request.SnapshotId);
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
                ObjectSetResolver.Info(set),
                groups.GroupCount,
                groups.Unreachable,
                (long)groups.UnreachableBytes,
                groups.OtherObjects,
                (long)groups.OtherBytes,
                groups.LimitedMembers,
                SnapshotAccess.IsComplete(snapshot),
                stopwatch.Elapsed.TotalSeconds,
                RetentionMapping.UnreachableNote(snapshot.Info.Kind));

            // What the view rests on (decision 7): the set, the groups, and the largest groups with the paths shown; every
            // listed group at Debug (the 3.2 review), numbered as the app and the CLI number them.
            Logger.LogInformation(
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
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var group in result.Groups.Skip(5))
                {
                    Logger.LogDebug("Similar retention of {Shape} in snapshot {Id}: {Group}", request.Set.Shape, request.SnapshotId, Provenance.Describe(group));
                }
            }

            return result;
        });

    public Task<IcicleResult> QueryIcicleAsync(IcicleRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Levels is < 1 or > 20)
            {
                throw new ArgumentException("Levels must be between 1 and 20.", nameof(request));
            }

            if (request.MinShare is < 0.0001 or > 0.5 || double.IsNaN(request.MinShare))
            {
                throw new ArgumentException("MinShare must be between 0.0001 and 0.5.", nameof(request));
            }

            var (snapshot, lease) = access.Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var focus = FocusIndex(graph, request.FocusAddress);
            var (tree, info) = await FocusAsync(request.SnapshotId, snapshot, request.FocusAddress, focus, progress, cancellationToken).ConfigureAwait(false);
            var segments = await Task.Run(() => IcicleLayout.Build(graph, tree, focus, request.Levels, request.MinShare, cancellationToken), cancellationToken).ConfigureAwait(false);
            var result = new IcicleResult(
                [.. segments.Select(s => s.Object >= 0
                    ? new IcicleSegmentDto(s.Level, (long)s.Offset, (long)s.Width, DominatorTreeRows.ObjectKey(graph.Addresses[s.Object]), graph.Types.GetName(graph.TypeIndices[s.Object]), graph.Addresses[s.Object])
                    : new IcicleSegmentDto(s.Level, (long)s.Offset, (long)s.Width, DominatorTreeRows.RootKey, "All reachable objects", 0))],
                (long)segments[0].Width,
                info);

            // The widest bars below the top, as drawn (sprint 2.3.7 review H3).
            Logger.LogInformation(
                "Icicle of snapshot {Id}: focus {Focus}, {Levels} levels, share {Share}, {Segments} bars of {Total} bytes; widest {Widest}",
                request.SnapshotId,
                focus >= 0 ? string.Create(CultureInfo.InvariantCulture, $"0x{request.FocusAddress:x}") : "root",
                request.Levels,
                request.MinShare,
                result.Segments.Count,
                result.Total,
                string.Join("; ", result.Segments.Where(s => s.Level > 0).OrderByDescending(s => s.Width).Take(5).Select(s => string.Create(CultureInfo.InvariantCulture, $"level {s.Level} {s.Name} 0x{s.Address:x} {s.Width}"))));
            return result;
        });

    /// <summary>A set for the log: a one-type set by its type and module (as before 3.1), any other by its shape.</summary>
    private static string Describe(ObjectSetSpec spec) =>
        spec.Kind == ObjectSetKind.Types && spec.Types is [var only]
            ? "type " + only.Name + " (" + (string.IsNullOrEmpty(only.Module) ? "no module" : only.Module) + ")"
            : spec.Shape;

    /// <summary>The index of the focus object, or -1 for none (address 0).</summary>
    private static int FocusIndex(HeapGraph graph, ulong address)
    {
        if (address == 0)
        {
            return -1;
        }

        return graph.TryGetIndex(address, out var focus)
            ? focus
            : throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{address:x} in this snapshot."));
    }

    /// <summary>The dominators, checking that a root reaches the focus.</summary>
    private async Task<(Dominators Tree, DominatorIndexInfo Info)> FocusAsync(Guid snapshotId, ISnapshot snapshot, ulong address, int focus, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var (dominators, info) = await derived.GetDominatorsAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false);
        var tree = dominators.Dominators;
        if (focus >= 0 && !tree.IsReachable(focus))
        {
            // Why, for this kind of snapshot, and where to go instead (the 2.4 review).
            throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"0x{address:x} is not in the dominator tree. {RetentionMapping.UnreachableNote(snapshot.Info.Kind)} It retains nothing; the biggest retainers show the tree from the top."));
        }

        return (tree, info);
    }
}
