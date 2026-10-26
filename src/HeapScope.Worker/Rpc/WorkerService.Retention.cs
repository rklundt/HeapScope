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
using HeapScope.Contracts;
using HeapScope.Core;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Retention paths: why an object or the objects of a type are alive (sprint 2.2).</summary>
internal sealed partial class WorkerService
{
    public Task<ObjectPathsResult> QueryObjectPathsAsync(ObjectPathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.MaxRoots is < 1 or > 50)
            {
                throw new ArgumentException("MaxRoots must be between 1 and 50.", nameof(request));
            }

            // The lease keeps the graph, and the reverse index attached to it, while the paths are found and named.
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            if (!snapshot.Graph.TryGetIndex(request.Address, out var index))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.Address:x} in this snapshot."));
            }

            var (reverse, info) = await GetReverseAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);

            // Whether a root reaches the object is decided by the root path tree, which is exact; the bounded search only
            // finds the paths, and falls back to the tree's path when it stops before a root (review H2).
            var rootPaths = await snapshots.GetRootPathTreeAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false)
                ?? throw SnapshotNotLoaded(request.SnapshotId);
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

                    var noReader = reader is null ? ContractMapping.ExplainMissing(SnapshotCapabilities.FieldValues, snapshot.Info.Kind) : null;
                    return RetentionMapping.ObjectPaths(snapshot.Graph, tree, reader, noReader, IsComplete(snapshot), info, cancellationToken, snapshot.Info.Kind);
                },
                cancellationToken).ConfigureAwait(false);

            // Which roots hold the object, as shown (sprint 2.3.7 review H3): a disputed "held by a static field" is
            // answered from the log, not by running the query again.
            logger.LogInformation(
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
            if (string.IsNullOrEmpty(request.TypeName))
            {
                throw new ArgumentException("A type name is required.", nameof(request));
            }

            if (request.MaxPaths is < 1 or > 100)
            {
                throw new ArgumentException("MaxPaths must be between 1 and 100.", nameof(request));
            }

            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var types = TypeGroups.MatchTypes(graph, request.TypeName, request.Module, out var matched);
            if (matched == 0)
            {
                // Answered before the tree is built for nothing (2.1 review M8).
                return new TypePathsResult([], 0, 0, 0, 0, 0, 0, 0, IsComplete(snapshot), 0, RetentionMapping.UnreachableNote(snapshot.Info.Kind));
            }

            var stopwatch = Stopwatch.StartNew();
            var tree = await snapshots.GetRootPathTreeAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false)
                ?? throw SnapshotNotLoaded(request.SnapshotId);
            var treeSeconds = stopwatch.Elapsed.TotalSeconds;
            progress?.Report(new JobProgress("Grouping objects by retention path", 0, null));
            var set = await Task.Run(() => KeyPaths.ForTypes(graph, tree, types, request.MaxPaths, cancellationToken), cancellationToken).ConfigureAwait(false);
            var typePaths = new TypePathsResult(
                [.. set.Paths.Select(p => RetentionMapping.KeyPath(graph, p))],
                matched,
                set.Objects,
                set.Unreachable,
                (long)set.UnreachableBytes,
                set.OtherPaths,
                set.OtherObjects,
                (long)set.OtherBytes,
                IsComplete(snapshot),
                treeSeconds,
                RetentionMapping.UnreachableNote(snapshot.Info.Kind));

            // The key paths as shown, with their roots (sprint 2.3.7 review H3).
            logger.LogInformation(
                "Retention paths of type {Type} ({Module}) in snapshot {Id}: {Objects} objects, {Unreachable} unreachable, {Other} further paths; key paths {KeyPaths}",
                request.TypeName,
                request.Module ?? "no module",
                request.SnapshotId,
                typePaths.Objects,
                typePaths.Unreachable,
                typePaths.OtherPaths,
                string.Join("; ", typePaths.Paths.Select(Provenance.Describe)));
            return typePaths;
        });
}
