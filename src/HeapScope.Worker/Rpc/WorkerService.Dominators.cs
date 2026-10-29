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

using System.Globalization;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>The dominator tree explorer (sprint 2.4, ADR-027).</summary>
internal sealed partial class WorkerService
{
    /// <summary>Rows of the revealed list (or the top) and trail entries written to the provenance line.</summary>
    private const int LoggedRows = 10;

    /// <summary>Expanded keys written to the provenance line.</summary>
    private const int LoggedKeys = 20;

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
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var focus = FocusIndex(graph, request.FocusAddress);
            var (tree, info) = await FocusAsync(request.SnapshotId, request.FocusAddress, focus, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new JobProgress("Listing retainers", 0, null));
            var grouping = request.Grouping switch
            {
                DominatorGroupingDto.Type => DominatorGrouping.Type,
                DominatorGroupingDto.Namespace => DominatorGrouping.Namespace,
                _ => DominatorGrouping.None,
            };
            var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
            var view = await Task.Run(() => DominatorTreeRows.Build(graph, tree, focus, expanded, grouping, request.MaxChildren, cancellationToken), cancellationToken).ConfigureAwait(false);
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
            logger.LogInformation(
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

            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var focus = FocusIndex(graph, request.FocusAddress);
            var (tree, info) = await FocusAsync(request.SnapshotId, request.FocusAddress, focus, progress, cancellationToken).ConfigureAwait(false);
            var segments = await Task.Run(() => IcicleLayout.Build(graph, tree, focus, request.Levels, request.MinShare, cancellationToken), cancellationToken).ConfigureAwait(false);
            var result = new IcicleResult(
                [.. segments.Select(s => s.Object >= 0
                    ? new IcicleSegmentDto(s.Level, (long)s.Offset, (long)s.Width, DominatorTreeRows.ObjectKey(graph.Addresses[s.Object]), graph.Types.GetName(graph.TypeIndices[s.Object]), graph.Addresses[s.Object])
                    : new IcicleSegmentDto(s.Level, (long)s.Offset, (long)s.Width, DominatorTreeRows.RootKey, "All reachable objects", 0))],
                (long)segments[0].Width,
                info);

            // The widest bars below the top, as drawn (sprint 2.3.7 review H3).
            logger.LogInformation(
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
    private async Task<(Dominators Tree, DominatorIndexInfo Info)> FocusAsync(Guid snapshotId, ulong address, int focus, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var (dominators, info) = await GetDominatorsAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false);
        var tree = dominators.Dominators;
        if (focus >= 0 && !tree.IsReachable(focus))
        {
            // Why, for this kind of snapshot, and where to go instead (the 2.4 review).
            var kind = snapshots.TryGet(snapshotId)?.Info.Kind ?? SnapshotKind.FullDump;
            throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"0x{address:x} is not in the dominator tree. {RetentionMapping.UnreachableNote(kind)} It retains nothing; the biggest retainers show the tree from the top."));
        }

        return (tree, info);
    }
}
