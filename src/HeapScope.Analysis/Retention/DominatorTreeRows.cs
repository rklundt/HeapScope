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
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Retention;

/// <summary>
/// Flattens the dominator tree for a paged grid (sprint 2.4, ADR-027): from the virtual root (its children, the top-level
/// dominators, are the biggest retainers) or from a focus object, each expanded row's children in retained order, as
/// objects or grouped by type or namespace. Siblings in the dominator tree retain disjoint sets, so a group's or a "more"
/// row's retained bytes are the exact sum of its members'. A list shows <c>maxChildren</c> rows and a "more" row; each
/// expanded "more" row shows another batch. Keys survive a re-run: an object's is its address, a group's and a "more"
/// row's extend their list's key.
/// </summary>
public static class DominatorTreeRows
{
    /// <summary>The most rows one answer holds; the rest is cut and the view says so.</summary>
    public const int MaxRows = 50_000;

    /// <summary>The key of the virtual root's list (the top level).</summary>
    public const string RootKey = "";

    public static string ObjectKey(ulong address) => "o:" + address.ToString("x", CultureInfo.InvariantCulture);

    public static string GroupKey(string listKey, string group) => listKey + "|g:" + group;

    public static string MoreKey(string listKey, int shown) => listKey + "|m:" + shown.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The rows of the tree below <paramref name="focus"/> (an object index; -1 for the virtual root), with the rows whose
    /// keys are in <paramref name="expanded"/> expanded. The focus itself is the first row and is always expanded.
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
        var ancestors = new List<int>();
        if (focus >= 0)
        {
            for (var v = dominators.ImmediateDominator(focus); v >= 0; v = dominators.ImmediateDominator(v))
            {
                ancestors.Add(v);
            }

            ancestors.Reverse();
            builder.Focus(focus);
        }
        else
        {
            builder.Children(RootKey, dominators.Root, depth: 0);
        }

        ulong total = 0;
        foreach (var top in dominators.ChildrenOf(dominators.Root))
        {
            total += dominators.RetainedSize(top);
        }

        var focusRetained = focus >= 0 ? dominators.RetainedSize(focus) : total;
        return new DominatorTreeView(builder.Rows, builder.Truncated, ancestors, focusRetained, total);
    }

    private sealed class Builder
    {
        private readonly HeapGraph _graph;
        private readonly Dominators _dominators;
        private readonly IReadOnlySet<string> _expanded;
        private readonly DominatorGrouping _grouping;
        private readonly int _maxChildren;
        private readonly CancellationToken _cancellationToken;

        // Each type's group id and each group's name and module, for grouped children (computed once per answer).
        private readonly int[] _groupOfType;
        private readonly List<(string Name, string? Module)> _groups = [];

        public Builder(HeapGraph graph, Dominators dominators, IReadOnlySet<string> expanded, DominatorGrouping grouping, int maxChildren, CancellationToken cancellationToken)
        {
            _graph = graph;
            _dominators = dominators;
            _expanded = expanded;
            _grouping = grouping;
            _maxChildren = maxChildren;
            _cancellationToken = cancellationToken;
            _groupOfType = grouping == DominatorGrouping.None ? [] : GroupTypes(graph, grouping, _groups);
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
            if (_grouping == DominatorGrouping.None)
            {
                Objects(listKey, children, depth);
            }
            else
            {
                Groups(listKey, children, depth);
            }
        }

        private void Objects(string listKey, ReadOnlySpan<int> members, int depth)
        {
            var shown = Math.Min(members.Length, Batches(listKey) * _maxChildren);
            for (var i = 0; i < shown && !Truncated; i++)
            {
                var obj = members[i];
                var key = ObjectKey(_graph.Addresses[obj]);
                var isExpanded = _expanded.Contains(key) && _dominators.ChildCount(obj) > 0;
                if (Add(ObjectRow(obj, listKey, depth, isExpanded)) && isExpanded)
                {
                    Children(key, obj, depth + 1);
                }
            }

            if (members.Length > shown && !Truncated)
            {
                ulong retained = 0;
                ulong bytes = 0;
                foreach (var obj in members[shown..])
                {
                    retained += _dominators.RetainedSize(obj);
                    bytes += _graph.GetSize(obj);
                }

                var remaining = members.Length - shown;
                Add(new DominatorRow(
                    MoreKey(listKey, shown),
                    listKey,
                    depth,
                    DominatorRowKind.More,
                    -1,
                    string.Create(CultureInfo.InvariantCulture, $"{remaining:N0} more objects"),
                    null,
                    remaining,
                    retained,
                    bytes,
                    Math.Min(remaining, _maxChildren),
                    false));
            }
        }

        private void Groups(string listKey, ReadOnlySpan<int> children, int depth)
        {
            // Sums per group in one pass; members are collected only for the expanded groups (children come in retained
            // order, so the members do too).
            var count = new long[_groups.Count];
            var retained = new ulong[_groups.Count];
            var bytes = new ulong[_groups.Count];
            var types = _graph.TypeIndices.Span;
            foreach (var obj in children)
            {
                var group = _groupOfType[types[obj]];
                count[group]++;
                retained[group] += _dominators.RetainedSize(obj);
                bytes[group] += _graph.GetSize(obj);
            }

            var present = Enumerable.Range(0, _groups.Count).Where(g => count[g] > 0)
                .OrderByDescending(g => retained[g]).ThenBy(g => _groups[g].Name, StringComparer.Ordinal).ThenBy(g => _groups[g].Module, StringComparer.Ordinal)
                .ToArray();
            var shown = Math.Min(present.Length, Batches(listKey) * _maxChildren);
            for (var i = 0; i < shown && !Truncated; i++)
            {
                var group = present[i];
                var (name, module) = _groups[group];
                var key = GroupKey(listKey, string.IsNullOrEmpty(module) ? name : name + " [" + module + "]");
                var isExpanded = _expanded.Contains(key);
                var row = new DominatorRow(key, listKey, depth, DominatorRowKind.Group, -1, name, module, count[group], retained[group], bytes[group], (int)Math.Min(count[group], int.MaxValue), isExpanded);
                if (Add(row) && isExpanded)
                {
                    var members = new int[count[group]];
                    var filled = 0;
                    foreach (var obj in children)
                    {
                        if (_groupOfType[types[obj]] == group)
                        {
                            members[filled++] = obj;
                        }
                    }

                    Objects(key, members, depth + 1);
                }
            }

            if (present.Length > shown && !Truncated)
            {
                long objects = 0;
                ulong moreRetained = 0;
                ulong moreBytes = 0;
                foreach (var group in present[shown..])
                {
                    objects += count[group];
                    moreRetained += retained[group];
                    moreBytes += bytes[group];
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

        // One batch, plus one more for each of the list's "more" rows that is expanded.
        private int Batches(string listKey)
        {
            var batches = 1;
            while (_expanded.Contains(MoreKey(listKey, batches * _maxChildren)))
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

        private static int[] GroupTypes(HeapGraph graph, DominatorGrouping grouping, List<(string Name, string? Module)> groups)
        {
            var ids = new Dictionary<(string Name, string Module), int>(TypeKeyComparer.Type);
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

            return groupOfType;
        }
    }
}
