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
using System.IO;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The fake worker's Retention (<see cref="IRetentionService"/>).</summary>
internal sealed partial class FakeWorkerService : IRetentionService
{
    public Task<ObjectPathsResult> QueryObjectPathsAsync(ObjectPathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectPathQueries.Add(request);
        if (ObjectPathsFailure is { } failure)
        {
            return Task.FromException<ObjectPathsResult>(failure);
        }

        return Task.FromResult(ObjectPaths ?? new ObjectPathsResult(
            [new RetentionNodeDto(0, -1, request.Address, "App.Order", null, false, false, 0, null, 0, 0)],
            [], [], false, false, 1, null, CompleteGraph, new ReverseIndexInfo(true, SnapshotMemoryMode.InMemory, 0, 1024, true), false, UnreachableNote));
    }

    public Task<TypePathsResult> QueryTypePathsAsync(TypePathsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        TypePathQueries.Add(request);
        return Task.FromResult(new TypePathsResult(
            [.. TypePaths], 1, TypePaths.Sum(p => p.Objects) + 2, 2, 80, 0, 0, 0, CompleteGraph, 0.3, UnreachableNote));
    }

    public Task<RetainedResult> QueryRetainedAsync(RetainedRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        RetainedQueries.Add(request);
        return Task.FromResult(Retained ?? new RetainedResult(
            request.Address, "App.Order", 48, true, 4096, 0x500, "App.Cache", false, UnreachableNote, Dominators, CompleteGraph));
    }

    public Task<RetainedBySetResult> QueryRetainedBySetAsync(RetainedBySetRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        SetQueries.Add(request);
        var total = SetRows.Sum(r => r.Bytes);
        IReadOnlyList<string?>[] rows =
        [
            .. SetRows.Select(r => (IReadOnlyList<string?>)[
                r.Name, r.Objects.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (total == 0 ? 0 : 100.0 * r.Bytes / total).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), r.Name, "App.dll"]),
        ];
        var id = Guid.NewGuid();
        _sets[id] = [.. rows];
        var columns = new ResultColumn[]
        {
            new(RetainedBySetColumns.Name, ResultColumnKind.Text, IsKey: true), new(RetainedBySetColumns.Objects, ResultColumnKind.Integer),
            new(RetainedBySetColumns.Retained, ResultColumnKind.Bytes), new(RetainedBySetColumns.Percent, ResultColumnKind.Percent),
            new(RetainedBySetColumns.TypeName, ResultColumnKind.Text), new(RetainedBySetColumns.Module, ResultColumnKind.Text),
        };
        var set = SetRows.FirstOrDefault(r => r.Name == request.TypeName);
        return Task.FromResult(new RetainedBySetResult(
            new ResultSetInfo(id, rows.Length, columns), SetRows.Count == 0 ? 0 : 1, set.Objects, set.Bytes, SetRows.Sum(r => r.Objects), total, set.Bytes, 10 * total, 0.25, Dominators));
    }

    public Task<SimilarRetentionResult> QuerySimilarRetentionAsync(SimilarRetentionRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        SimilarRetentionQueries.Add(request);
        return Task.FromResult(new SimilarRetentionResult(
            [.. RetentionGroups],
            new ObjectSetInfo(RetentionGroups.Sum(g => g.Objects) + 1, RetentionGroups.Sum(g => g.Bytes) + 10, 1),
            RetentionGroups.Count,
            1,
            10,
            0,
            0,
            0,
            CompleteGraph,
            0.2,
            UnreachableNote));
    }

    public Task<SetDominatorsResult> QuerySetDominatorsAsync(SetDominatorsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        SetDominatorQueries.Add(request);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        IReadOnlyList<string?>[] rows =
        [
            .. SetDominatorRows.Select(r => (IReadOnlyList<string?>)[
                "0x" + r.Address.ToString("x", invariant), r.Type, r.Bytes.ToString(invariant), r.Retained.ToString(invariant), r.Below.ToString(invariant)]),
        ];
        var id = Guid.NewGuid();
        _sets[id] = [.. rows];
        var columns = new ResultColumn[]
        {
            new(SetDominatorColumns.Address, ResultColumnKind.Address, IsKey: true), new(SetDominatorColumns.Type, ResultColumnKind.Text),
            new(SetDominatorColumns.Bytes, ResultColumnKind.Bytes), new(SetDominatorColumns.Retained, ResultColumnKind.Bytes),
            new(SetDominatorColumns.Members, ResultColumnKind.Integer),
        };
        var members = SetDominatorRows.Count + SetDominatorRows.Sum(r => r.Below);
        return Task.FromResult(new SetDominatorsResult(
            new ResultSetInfo(id, rows.Length, columns), new ObjectSetInfo(members, SetDominatorRows.Sum(r => r.Bytes), 1), SetDominatorRows.Count, SetDominatorRows.Sum(r => r.Retained), 0, Dominators));
    }

    /// <summary>
    /// The fake's icicle: below the focus (or the top level), each <see cref="TreeNodes"/> child side by side, as wide as its
    /// retained bytes, down to <see cref="IcicleRequest.Levels"/>.
    /// </summary>
    public Task<IcicleResult> QueryIcicleAsync(IcicleRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        IcicleQueries.Add(request);
        var focus = request.FocusAddress == 0 ? null : TreeNodes.First(n => n.Address == request.FocusAddress);
        var total = focus?.Retained ?? TreeNodes.Where(n => n.ParentKey.Length == 0).Sum(n => n.Retained);
        var segments = new List<IcicleSegmentDto> { new(0, 0, total, focus?.Key ?? string.Empty, focus?.Name ?? "All reachable objects", focus?.Address ?? 0) };
        void Add(string parent, int level, long offset)
        {
            if (level > request.Levels)
            {
                return;
            }

            foreach (var node in TreeNodes.Where(n => n.ParentKey == parent))
            {
                segments.Add(new IcicleSegmentDto(level, offset, node.Retained, node.Key, node.Name, node.Address));
                Add(node.Key, level + 1, offset);
                offset += node.Retained;
            }
        }

        Add(focus?.Key ?? string.Empty, 1, 0);
        return Task.FromResult(new IcicleResult([.. segments.OrderBy(s => s.Level)], total, Dominators));
    }

    public Task<DominatorTreeResult> QueryDominatorTreeAsync(DominatorTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        TreeQueries.Add(request);
        var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
        var focus = request.FocusAddress == 0 ? null : TreeNodes.FirstOrDefault(n => n.Address == request.FocusAddress);
        var total = TreeNodes.Where(n => n.ParentKey.Length == 0).Sum(n => n.Retained);
        var rows = new List<IReadOnlyList<string?>>();
        var keys = new List<string>();
        int ChildCount(FakeTreeNode node) => TreeNodes.Count(n => n.ParentKey == node.Key);
        void Add(FakeTreeNode node, int depth, bool open)
        {
            rows.Add(node.Cells(depth, open, ChildCount(node), total));
            keys.Add(node.Key);
        }

        void Emit(string parent, int depth)
        {
            foreach (var node in TreeNodes.Where(n => n.ParentKey == parent))
            {
                var open = expanded.Contains(node.Key) && ChildCount(node) > 0;
                Add(node, depth, open);
                if (open)
                {
                    Emit(node.Key, depth + 1);
                }
            }
        }

        // The focus's dominators from the top: its parents by key.
        var ancestors = new List<DominatorAncestorDto>();
        if (focus is not null)
        {
            for (var parent = TreeNodes.FirstOrDefault(n => n.Key == focus.ParentKey); parent is not null; parent = TreeNodes.FirstOrDefault(n => n.Key == parent.ParentKey))
            {
                ancestors.Insert(0, new DominatorAncestorDto(parent.Key, parent.Name, parent.Address, parent.Retained));
            }

            Add(focus, 0, true);
            Emit(focus.Key, 1);
        }
        else
        {
            Emit(string.Empty, 0);
        }

        var id = Guid.NewGuid();
        _sets[id] = rows;
        return Task.FromResult(new DominatorTreeResult(
            new ResultSetInfo(id, rows.Count, FakeTreeNode.Columns),
            focus?.Key,
            [.. ancestors.Take(DominatorTreeLimits.TrailHead), .. ancestors.Skip(DominatorTreeLimits.TrailHead).TakeLast(DominatorTreeLimits.TrailTail)],
            focus?.Retained ?? total,
            total,
            TreeNodes.Count(n => n.ParentKey.Length == 0),
            false,
            Dominators,
            ancestors.Count,
            request.RevealKey is { } reveal ? keys.IndexOf(reveal) : -1));
    }
}
