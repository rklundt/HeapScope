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

using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Retention;

/// <summary>
/// Flattens the dominator tree for a paged grid (sprint 2.4, ADR-027): from the virtual root (its children, the top-level
/// dominators, are the biggest retainers) or from a focus object, each expanded row's children in retained order, as
/// objects or grouped by type or namespace. Siblings in the dominator tree retain disjoint sets, so a group's or a "more"
/// row's retained bytes are the exact sum of its members'. A list shows <c>maxChildren</c> rows and a "more" row; each
/// expanded "more" row shows another batch. Keys survive a re-run: an object's is its address, a group's and a "more"
/// row's extend their list's key. The key formats and the limits are the wire's (Contracts mirrors them, a test checks).
/// </summary>
public static class DominatorTreeRows
{
    /// <summary>The most rows one answer holds; the rest is cut and the view says so.</summary>
    public const int MaxRows = 50_000;

    /// <summary>The deepest level shown: expansions below it are not followed (a long chain cannot exhaust the stack).</summary>
    public const int MaxDepth = 1_000;

    /// <summary>The focus's dominators kept at the top of the trail; the rest of a long trail is its nearest ones.</summary>
    public const int TrailHead = 3;

    /// <summary>The focus's nearest dominators kept at the end of the trail.</summary>
    public const int TrailTail = 20;

    /// <summary>The key of the virtual root's list (the top level).</summary>
    public const string RootKey = "";

    // Each grouping's tables per graph: computed once per snapshot, not per click (the 2.4 review).
    private static readonly ConditionalWeakTable<HeapGraph, GroupTables?[]> s_groupTables = [];

    public static string ObjectKey(ulong address) => "o:" + address.ToString("x", CultureInfo.InvariantCulture);

    public static string GroupKey(string listKey, string group) => listKey + "|g:" + group;

    /// <summary>A group's name in its key: a type's name and module file name in brackets, or a namespace.</summary>
    public static string GroupName(string name, string? module) => string.IsNullOrEmpty(module) ? name : name + " [" + module + "]";

    public static string MoreKey(string listKey, int shown) => listKey + "|m:" + shown.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The rows of the tree below <paramref name="focus"/> (an object index; -1 for the virtual root), with the rows whose
    /// keys are in <paramref name="expanded"/> expanded. The focus itself is the first row and is always expanded. The
    /// trail of the focus's dominators keeps the top <see cref="TrailHead"/> and the nearest <see cref="TrailTail"/>.
    /// </summary>
    public static DominatorTreeView Build(
        HeapGraph graph,
        Dominators dominators,
        int focus,
        IReadOnlySet<string> expanded,
        DominatorGrouping grouping,
        int maxChildren,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dominators);
        ArgumentNullException.ThrowIfNull(expanded);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChildren, 1);
        if (focus < -1 || focus >= graph.ObjectCount)
        {
            throw new ArgumentOutOfRangeException(nameof(focus));
        }

        var builder = new Builder(graph, dominators, expanded, grouping, maxChildren, cancellationToken);
        IReadOnlyList<int> ancestors = [];
        var depth = 0;
        if (focus >= 0)
        {
            (ancestors, depth) = Trail(dominators, focus, cancellationToken);
            builder.Focus(focus);
        }
        else
        {
            builder.Children(RootKey, dominators.Root, depth: 0);
        }

        var total = dominators.ReachableBytes();
        var focusRetained = focus >= 0 ? dominators.RetainedSize(focus) : total;
        return new DominatorTreeView(builder.Rows, builder.Truncated, ancestors, depth, focusRetained, total);
    }

    /// <summary>
    /// The focus's dominators from the top, at most <see cref="TrailHead"/> + <see cref="TrailTail"/> of them (a chain of a
    /// million nodes would otherwise go to the client whole; the 2.4 review), and how many there are.
    /// </summary>
    private static (IReadOnlyList<int> Trail, int Depth) Trail(Dominators dominators, int focus, CancellationToken cancellationToken)
    {
        // Walking up meets the nearest first; the top ones are the last met, kept in a small ring.
        var nearest = new List<int>(TrailTail);
        var top = new int[TrailHead];
        var depth = 0;
        for (var v = dominators.ImmediateDominator(focus); v >= 0; v = dominators.ImmediateDominator(v))
        {
            if (nearest.Count < TrailTail)
            {
                nearest.Add(v);
            }

            top[depth % TrailHead] = v;
            if ((++depth & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        if (depth <= TrailHead + TrailTail)
        {
            // Short enough to keep whole: walk again, a few dozen steps at most.
            var all = new List<int>(depth);
            for (var v = dominators.ImmediateDominator(focus); v >= 0; v = dominators.ImmediateDominator(v))
            {
                all.Add(v);
            }

            all.Reverse();
            return (all, depth);
        }

        // The top one was met last, at ring slot (depth - 1) % TrailHead.
        var trail = new List<int>(TrailHead + TrailTail);
        for (var i = 0; i < TrailHead; i++)
        {
            trail.Add(top[(depth - 1 - i) % TrailHead]);
        }

        nearest.Reverse();
        trail.AddRange(nearest);
        return (trail, depth);
    }

    private static GroupTables Tables(HeapGraph graph, DominatorGrouping grouping)
    {
        var all = s_groupTables.GetValue(graph, _ => new GroupTables?[3]);
        lock (all)
        {
            return all[(int)grouping] ??= GroupTables.Build(graph, grouping);
        }
    }

    private sealed class Builder
    {
        private readonly HeapGraph _graph;
        private readonly Dominators _dominators;
        private readonly IReadOnlySet<string> _expanded;
        private readonly int _maxChildren;
        private readonly CancellationToken _cancellationToken;
        private readonly GroupTables? _groups;

        public Builder(HeapGraph graph, Dominators dominators, IReadOnlySet<string> expanded, DominatorGrouping grouping, int maxChildren, CancellationToken cancellationToken)
        {
            _graph = graph;
            _dominators = dominators;
            _expanded = expanded;
            _maxChildren = maxChildren;
            _cancellationToken = cancellationToken;
            _groups = grouping == DominatorGrouping.None ? null : Tables(graph, grouping);
        }

        public List<DominatorRow> Rows { get; } = [];

        public bool Truncated { get; private set; }

        public void Focus(int obj)
        {
            if (Add(ObjectRow(obj, RootKey, depth: 0, expanded: true)))
            {
                Children(ObjectKey(_graph.Addresses[obj]), obj, depth: 1);
            }
        }

        /// <summary>The children of <paramref name="vertex"/> under the list key <paramref name="listKey"/>.</summary>
        public void Children(string listKey, int vertex, int depth)
        {
            var children = _dominators.ChildrenOf(vertex);
            if (_groups is null)
            {
                Objects(listKey, children, default(AllMembers), children.Length, null, depth);
            }
            else
            {
                Groups(listKey, children, _groups, depth);
            }
        }

        // The members of a list (those the filter accepts, count of them), the largest first, then a "more" row for the
        // rest: the list's totals (when known) less what is shown, or one pass over the list.
        private void Objects<TFilter>(string listKey, ReadOnlySpan<int> members, TFilter filter, long count, (ulong Retained, ulong Bytes)? totals, int depth)
            where TFilter : IMemberFilter, allows ref struct
        {
            var shown = (int)Math.Min(count, Math.Min((long)Batches(listKey) * _maxChildren, MaxRows - Rows.Count + 1L));
            var ordered = RetainedOrder.Top(members, shown, _dominators, filter);
            for (var i = 0; i < ordered.Length && !Truncated; i++)
            {
                var obj = ordered[i];
                var key = ObjectKey(_graph.Addresses[obj]);
                var isExpanded = depth < MaxDepth && _expanded.Contains(key) && _dominators.ChildCount(obj) > 0;
                if (Add(ObjectRow(obj, listKey, depth, isExpanded)) && isExpanded)
                {
                    Children(key, obj, depth + 1);
                }
            }

            if (count > ordered.Length && !Truncated)
            {
                var (retained, bytes) = totals ?? Sum(members);
                foreach (var obj in ordered)
                {
                    retained -= _dominators.RetainedSize(obj);
                    bytes -= _graph.GetSize(obj);
                }

                var remaining = count - ordered.Length;
                Add(new DominatorRow(
                    MoreKey(listKey, ordered.Length),
                    listKey,
                    depth,
                    DominatorRowKind.More,
                    -1,
                    string.Create(CultureInfo.InvariantCulture, $"{remaining:N0} more objects"),
                    null,
                    remaining,
                    retained,
                    bytes,
                    (int)Math.Min(remaining, _maxChildren),
                    false));
            }
        }

        private (ulong Retained, ulong Bytes) Sum(ReadOnlySpan<int> members)
        {
            var sizes = _dominators.RetainedSizes.Span;
            ulong retained = 0;
            ulong bytes = 0;
            for (var i = 0; i < members.Length; i++)
            {
                retained += sizes[members[i]];
                bytes += _graph.GetSize(members[i]);
                if ((i & 0xFFFF) == 0xFFFF)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return (retained, bytes);
        }

        private void Groups(string listKey, ReadOnlySpan<int> children, GroupTables tables, int depth)
        {
            // Dense sums per group, pooled and reset where touched: no hashing per child (the 2.4 review; DEVELOPER 4).
            var groupCount = tables.Groups.Count;
            var counts = ArrayPool<long>.Shared.Rent(groupCount);
            Array.Clear(counts, 0, groupCount);
            var retainedSums = ArrayPool<ulong>.Shared.Rent(groupCount);
            var byteSums = ArrayPool<ulong>.Shared.Rent(groupCount);
            var touched = new List<int>();
            try
            {
                var types = _graph.TypeIndices.Span;
                var sizes = _dominators.RetainedSizes.Span;
                var groupOfType = tables.GroupOfType;
                for (var i = 0; i < children.Length; i++)
                {
                    var obj = children[i];
                    var group = groupOfType[types[obj]];
                    if (counts[group]++ == 0)
                    {
                        touched.Add(group);
                        retainedSums[group] = 0;
                        byteSums[group] = 0;
                    }

                    retainedSums[group] += sizes[obj];
                    byteSums[group] += _graph.GetSize(obj);
                    if ((i & 0xFFFF) == 0xFFFF)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                var present = touched
                    .OrderByDescending(g => retainedSums[g]).ThenBy(g => tables.Groups[g].Name, StringComparer.Ordinal).ThenBy(g => tables.Groups[g].Module, StringComparer.Ordinal)
                    .ToArray();
                var shown = Math.Min(present.Length, Batches(listKey) * _maxChildren);
                for (var i = 0; i < shown && !Truncated; i++)
                {
                    var group = present[i];
                    var (name, module) = tables.Groups[group];
                    var key = GroupKey(listKey, GroupName(name, module));
                    var isExpanded = depth < MaxDepth && _expanded.Contains(key);
                    var row = new DominatorRow(key, listKey, depth, DominatorRowKind.Group, -1, name, module, counts[group], retainedSums[group], byteSums[group], (int)Math.Min(counts[group], int.MaxValue), isExpanded);
                    if (Add(row) && isExpanded)
                    {
                        // The group's largest members picked straight from the list (no copy of a group of millions); its
                        // "more" row is the group's totals less what is shown.
                        Objects(key, children, new GroupMembers(types, groupOfType, group), counts[group], (retainedSums[group], byteSums[group]), depth + 1);
                    }
                }

                if (present.Length > shown && !Truncated)
                {
                    long objects = 0;
                    ulong moreRetained = 0;
                    ulong moreBytes = 0;
                    foreach (var group in present[shown..])
                    {
                        objects += counts[group];
                        moreRetained += retainedSums[group];
                        moreBytes += byteSums[group];
                    }

                    var remaining = present.Length - shown;
                    Add(new DominatorRow(
                        MoreKey(listKey, shown),
                        listKey,
                        depth,
                        DominatorRowKind.More,
                        -1,
                        string.Create(CultureInfo.InvariantCulture, $"{remaining:N0} more groups ({objects:N0} objects)"),
                        null,
                        objects,
                        moreRetained,
                        moreBytes,
                        Math.Min(remaining, _maxChildren),
                        false));
                }
            }
            finally
            {
                foreach (var group in touched)
                {
                    counts[group] = 0;
                }

                ArrayPool<long>.Shared.Return(counts);
                ArrayPool<ulong>.Shared.Return(retainedSums);
                ArrayPool<ulong>.Shared.Return(byteSums);
            }
        }

        // One batch, plus one more for each of the list's "more" rows that is expanded (never past the row limit).
        private int Batches(string listKey)
        {
            var batches = 1;
            while ((long)batches * _maxChildren < MaxRows && _expanded.Contains(MoreKey(listKey, batches * _maxChildren)))
            {
                batches++;
            }

            return batches;
        }

        private DominatorRow ObjectRow(int obj, string parentKey, int depth, bool expanded)
        {
            var type = _graph.TypeIndices[obj];
            return new DominatorRow(
                ObjectKey(_graph.Addresses[obj]),
                parentKey,
                depth,
                DominatorRowKind.Object,
                obj,
                _graph.Types.GetName(type),
                TypeGroups.ModuleKey(_graph.Types.GetModule(type)),
                1,
                _dominators.RetainedSize(obj),
                _graph.GetSize(obj),
                _dominators.ChildCount(obj),
                expanded);
        }

        private bool Add(DominatorRow row)
        {
            if (Rows.Count >= MaxRows)
            {
                Truncated = true;
                return false;
            }

            Rows.Add(row);
            if ((Rows.Count & 0xFFF) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            return true;
        }
    }

    /// <summary>Each type's group and each group's name and module, for one grouping of one graph.</summary>
    private sealed class GroupTables(int[] groupOfType, List<(string Name, string? Module)> groups)
    {
        public int[] GroupOfType { get; } = groupOfType;

        public List<(string Name, string? Module)> Groups { get; } = groups;

        public static GroupTables Build(HeapGraph graph, DominatorGrouping grouping)
        {
            var ids = new Dictionary<(string Name, string Module), int>(TypeKeyComparer.Type);
            var groups = new List<(string Name, string? Module)>();
            var groupOfType = new int[graph.Types.Count];
            for (var t = 0; t < groupOfType.Length; t++)
            {
                var name = graph.Types.GetName(t);
                var key = grouping == DominatorGrouping.Namespace
                    ? (TypeGroups.NamespaceOf(name), string.Empty)
                    : (name, TypeGroups.ModuleKey(graph.Types.GetModule(t)));
                if (!ids.TryGetValue(key, out var id))
                {
                    id = groups.Count;
                    ids.Add(key, id);
                    groups.Add((key.Item1, key.Item2.Length == 0 ? null : key.Item2));
                }

                groupOfType[t] = id;
            }

            return new GroupTables(groupOfType, groups);
        }
    }
}
