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
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>The dominator tree explorer (sprint 2.4, ADR-027).</summary>
internal sealed partial class WorkerService
{
    /// <summary>The most expanded keys one request may carry (a user expands rows one at a time).</summary>
    private const int MaxExpanded = 10_000;

    public Task<DominatorTreeResult> QueryDominatorTreeAsync(DominatorTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.MaxChildren is < 1 or > 1_000)
            {
                throw new ArgumentException("MaxChildren must be between 1 and 1,000.", nameof(request));
            }

            if (request.Expanded is null || request.Expanded.Count > MaxExpanded)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Expanded must list at most {MaxExpanded:N0} keys."), nameof(request));
            }

            // The set keeps the lease while its rows are paged (they hold no graph data, but the snapshot must stay open
            // for the links they lead to).
            var (snapshot, lease) = Lease(request.SnapshotId);
            try
            {
                var graph = snapshot.Graph;
                var focus = -1;
                if (request.FocusAddress != 0 && !graph.TryGetIndex(request.FocusAddress, out focus))
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no object at 0x{request.FocusAddress:x} in this snapshot."));
                }

                var (dominators, info) = await GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
                var tree = dominators.Dominators;
                if (focus >= 0 && !tree.IsReachable(focus))
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"No root reaches 0x{request.FocusAddress:x}, so it retains nothing and is not in the dominator tree."));
                }

                progress?.Report(new JobProgress("Listing retainers", 0, null));
                var grouping = request.Grouping switch
                {
                    DominatorGroupingDto.Type => DominatorGrouping.Type,
                    DominatorGroupingDto.Namespace => DominatorGrouping.Namespace,
                    _ => DominatorGrouping.None,
                };
                var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
                var view = await Task.Run(() => DominatorTreeRows.Build(graph, tree, focus, expanded, grouping, request.MaxChildren, cancellationToken), cancellationToken).ConfigureAwait(false);

                // The rows as shown at the top (sprint 2.3.7 review H3): a disputed "biggest retainer" is answered from the log.
                logger.LogInformation(
                    "Dominator tree of snapshot {Id}: focus {Focus}, {Rows} rows{Truncated}, top level {TopLevel}, total retained {Total} bytes; first rows {First}",
                    request.SnapshotId,
                    focus >= 0 ? string.Create(CultureInfo.InvariantCulture, $"0x{request.FocusAddress:x}") : "root",
                    view.Rows.Count,
                    view.Truncated ? " (cut at the row limit)" : string.Empty,
                    tree.ChildCount(tree.Root),
                    view.TotalRetained,
                    string.Join("; ", view.Rows.Take(10).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Kind} {r.Name}{(r.Object >= 0 ? $" 0x{graph.Addresses[r.Object]:x}" : string.Empty)} retains {r.Retained}"))));
                var rowAddresses = view.Rows.Select(r => r.Object >= 0 ? graph.Addresses[r.Object] : 0).ToArray();
                var rows = resultSets.Add(new DominatorTreeSource(view, rowAddresses), lease);
                return new DominatorTreeResult(
                    rows,
                    focus >= 0 ? DominatorTreeRows.ObjectKey(request.FocusAddress) : null,
                    [.. view.Ancestors.Select(a => new DominatorAncestorDto(DominatorTreeRows.ObjectKey(graph.Addresses[a]), graph.Types.GetName(graph.TypeIndices[a]), graph.Addresses[a], (long)tree.RetainedSize(a)))],
                    (long)view.FocusRetained,
                    (long)view.TotalRetained,
                    tree.ChildCount(tree.Root),
                    view.Truncated,
                    info);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });
}
