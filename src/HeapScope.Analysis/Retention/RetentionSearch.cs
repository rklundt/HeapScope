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

using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Retention;

/// <summary>
/// One object of a retention tree: <see cref="Child"/> is the node it references on the way to the inspected object (-1 for
/// the inspected object itself, node 0), <see cref="Depth"/> its distance from the inspected object.
/// </summary>
public readonly record struct RetentionNode(int ObjectIndex, int Child, int Depth);

/// <summary>
/// Why one object is alive (sprint 2.2): the shortest paths from up to N path roots to it, merged into one tree whose node 0
/// is the object and whose leaves are the roots (<see cref="Roots"/>, closest first, ties by root priority).
/// </summary>
/// <param name="Nodes">The tree's objects; each node's <see cref="RetentionNode.Child"/> leads toward node 0.</param>
/// <param name="Roots">The roots found, as (node id, root), closest first.</param>
/// <param name="StoppedEarly">The search stopped at its root or visit limit, so more roots may exist.</param>
/// <param name="Visited">Objects the search visited.</param>
public sealed record RetentionTree(IReadOnlyList<RetentionNode> Nodes, IReadOnlyList<(int Node, PathRoot Root)> Roots, bool StoppedEarly, int Visited)
{
    /// <summary>
    /// Whether the search found a root. Without the root path tree a search that stopped early can miss one; the worker
    /// decides reachability from the tree (review H2). What no root means depends on the snapshot's kind (review M3).
    /// </summary>
    public bool IsReachable => Roots.Count > 0;

    /// <summary>The node ids of one root's path, from the root to the inspected object.</summary>
    public IReadOnlyList<int> PathOf(int rootNode)
    {
        var path = new List<int>();
        for (var node = rootNode; node >= 0; node = Nodes[node].Child)
        {
            path.Add(node);
        }

        return path;
    }
}

/// <summary>
/// Finds why one object is alive (sprint 2.2): a breadth-first search backward from the object over its referrers (the
/// reverse index, 2.1), which stops at path roots (an object held by a retaining root, or a frozen object) instead of
/// passing through them, so each root found comes with its shortest path. The search finishes the distance at which it
/// found enough roots, then keeps the closest ones, preferring better roots at equal distance (decision 1).
/// </summary>
public static class RetentionSearch
{
    /// <summary>Roots returned by default (decision 2).</summary>
    public const int DefaultMaxRoots = 10;

    /// <summary>Objects visited at most by default (decision 2).</summary>
    public const int DefaultMaxVisited = 1_000_000;

    /// <param name="paths">When given (review H2), referrers no root reaches are not visited: the search never wanders
    /// into garbage, and an object no root reaches is answered at once.</param>
    public static RetentionTree Find(
        HeapGraph graph,
        ReverseReferences reverse,
        int target,
        int maxRoots = DefaultMaxRoots,
        int maxVisited = DefaultMaxVisited,
        CancellationToken cancellationToken = default,
        RootPathTree? paths = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(reverse);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRoots, 1);

        // child[o]: the object o references on its shortest way to the target; depth for the tie rules.
        var child = new Dictionary<int, (int Child, int Depth)> { [target] = (-1, 0) };
        var found = new List<PathRoot>();
        var queue = new Queue<int>();
        if (paths is null || paths.IsReachable(target))
        {
            queue.Enqueue(target);
        }

        var stopDepth = int.MaxValue;
        var stoppedEarly = false;
        var dequeued = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var depth = child[current].Depth;
            if ((++dequeued & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (depth > stopDepth)
            {
                // Enough roots at a closer distance; anything left could hold more roots.
                stoppedEarly = true;
                break;
            }

            if (PathRoots.TryGet(graph, current, out var root))
            {
                found.Add(root);
                if (found.Count >= maxRoots)
                {
                    stopDepth = depth;
                }

                // A path ends at its root; the inspected object itself still shows who else holds it.
                if (current != target)
                {
                    continue;
                }
            }

            var referrers = reverse.GetReferrers(current);
            for (var i = 0; i < referrers.Length; i++)
            {
                var referrer = referrers[i];
                if (child.ContainsKey(referrer) || (paths is not null && !paths.IsReachable(referrer)))
                {
                    continue;
                }

                if (child.Count >= maxVisited)
                {
                    stoppedEarly = true;
                    queue.Clear();
                    break;
                }

                child[referrer] = (current, depth + 1);
                queue.Enqueue(referrer);
            }
        }

        var kept = found
            .OrderBy(r => child[r.ObjectIndex].Depth)
            .ThenBy(PathRoots.Priority)
            .Take(maxRoots)
            .ToList();
        stoppedEarly |= found.Count > kept.Count;

        // The union of the kept roots' paths, numbered from the target outward.
        var nodes = new List<RetentionNode> { new(target, -1, 0) };
        var ids = new Dictionary<int, int> { [target] = 0 };
        var roots = new List<(int, PathRoot)>();
        foreach (var root in kept)
        {
            roots.Add((NodeOfIterative(root.ObjectIndex, child, nodes, ids), root));
        }

        return new RetentionTree(nodes, roots, stoppedEarly, child.Count);
    }

    /// <summary>
    /// The one path the root path tree knows for <paramref name="target"/> (review H2): what an object's paths fall back to
    /// when the search stopped at its limit before reaching a root, so a live object is never reported as unreachable.
    /// Empty when no root reaches the object. The result says it stopped early: more roots may exist.
    /// </summary>
    public static RetentionTree FromRootPath(HeapGraph graph, RootPathTree paths, int target)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(paths);
        if (!paths.IsReachable(target))
        {
            return new RetentionTree([new RetentionNode(target, -1, 0)], [], StoppedEarly: false, Visited: 1);
        }

        var nodes = new List<RetentionNode> { new(target, -1, 0) };
        var current = target;
        while (paths.Parent(current) != RootPathTree.Source)
        {
            current = paths.Parent(current);
            nodes.Add(new RetentionNode(current, nodes.Count - 1, nodes.Count));
        }

        PathRoots.TryGet(graph, current, out var root);
        return new RetentionTree(nodes, [(nodes.Count - 1, root)], StoppedEarly: true, Visited: nodes.Count);
    }

    /// <summary>Adds the path from <paramref name="objectIndex"/> to the target to the tree (no recursion on long paths).</summary>
    private static int NodeOfIterative(int objectIndex, Dictionary<int, (int Child, int Depth)> child, List<RetentionNode> nodes, Dictionary<int, int> ids)
    {
        var pending = new Stack<int>();
        var current = objectIndex;
        while (!ids.ContainsKey(current))
        {
            pending.Push(current);
            current = child[current].Child;
        }

        var id = ids[current];
        while (pending.Count > 0)
        {
            var objectOnPath = pending.Pop();
            nodes.Add(new RetentionNode(objectOnPath, id, child[objectOnPath].Depth));
            id = nodes.Count - 1;
            ids[objectOnPath] = id;
        }

        return ids[objectIndex];
    }
}
