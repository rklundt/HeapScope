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

namespace HeapScope.Integration.Tests.Infrastructure;

/// <summary>
/// Brute-force queries over a <see cref="HeapGraph"/> for tests: simple and obviously right, not fast. The real
/// analyses (summaries, retention paths, dominators) arrive in later sprints and are tested against these.
/// </summary>
internal static class GraphQueries
{
    public static Dictionary<string, int> CountTypes(HeapGraph graph)
    {
        var perType = new int[graph.Types.Count];
        foreach (var type in graph.TypeIndices.Span)
        {
            perType[type]++;
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var t = 0; t < perType.Length; t++)
        {
            var name = graph.Types.GetName(t);
            result[name] = result.GetValueOrDefault(name) + perType[t];
        }

        return result;
    }

    public static IEnumerable<int> Instances(HeapGraph graph, string typeName)
    {
        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if (graph.Types.GetName(graph.TypeIndices[i]) == typeName)
            {
                yield return i;
            }
        }
    }

    public static List<int> Referrers(HeapGraph graph, int target)
    {
        var result = new List<int>();
        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if (graph.GetReferences(i).Contains(target))
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>Marks every object reachable from a retaining root by following graph edges.</summary>
    public static bool[] ReachableFromRetainingRoots(HeapGraph graph)
    {
        var reachable = new bool[graph.ObjectCount];
        var queue = new Queue<int>();
        foreach (var root in graph.Roots.Roots.Span)
        {
            if (root.IsRetaining && !reachable[root.ObjectIndex])
            {
                reachable[root.ObjectIndex] = true;
                queue.Enqueue(root.ObjectIndex);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var target in graph.GetReferences(queue.Dequeue()))
            {
                if (!reachable[target])
                {
                    reachable[target] = true;
                    queue.Enqueue(target);
                }
            }
        }

        return reachable;
    }

    /// <summary>Shortest path (type names, start included) from <paramref name="start"/> to the first object of <paramref name="targetType"/>.</summary>
    public static List<string>? FindPath(HeapGraph graph, int start, string targetType)
    {
        var parents = new Dictionary<int, int> { [start] = -1 };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (graph.Types.GetName(graph.TypeIndices[current]) == targetType)
            {
                var path = new List<string>();
                for (var step = current; step >= 0; step = parents[step])
                {
                    path.Add(graph.Types.GetName(graph.TypeIndices[step]));
                }

                path.Reverse();
                return path;
            }

            foreach (var target in graph.GetReferences(current))
            {
                if (parents.TryAdd(target, current))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return null;
    }
}
