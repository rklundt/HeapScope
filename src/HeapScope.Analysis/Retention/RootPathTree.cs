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
/// Every object's shortest path from a root (sprint 2.2): one breadth-first search forward from all path roots at once,
/// keeping for each object the object it was first reached from. Roots are queued best first (static fields, then stacks,
/// handles, the finalizer queue, other roots, then frozen objects; decision 1), so at equal distance the better root wins.
/// Costs 4 bytes per object kept (<see cref="BytesPerObject"/>), and as many more while it is built.
/// <see cref="BuildBytesPerObject"/> is what the worker checks against its budget first.
/// </summary>
public sealed class RootPathTree
{
    /// <summary>Marks an object no root reaches.</summary>
    public const int Unreachable = -1;

    /// <summary>Marks a path root (an object held by a retaining root, or a frozen object).</summary>
    public const int Source = -2;

    /// <summary>Bytes per object the tree keeps.</summary>
    public const int BytesPerObject = 4;

    /// <summary>Bytes per object while it is built (the tree and the breadth-first queue).</summary>
    public const int BuildBytesPerObject = 8;

    private readonly int[] _parent;

    private RootPathTree(int[] parent, long reachable)
    {
        _parent = parent;
        ReachableCount = reachable;
    }

    /// <summary>Objects some root reaches.</summary>
    public long ReachableCount { get; }

    /// <summary>Bytes kept by the tree.</summary>
    public long ColumnBytes => 4L * _parent.Length;

    /// <summary>
    /// The object that references <paramref name="objectIndex"/> on its shortest path from a root, or
    /// <see cref="Source"/> for a path root, or <see cref="Unreachable"/>.
    /// </summary>
    public int Parent(int objectIndex) => _parent[objectIndex];

    public bool IsReachable(int objectIndex) => _parent[objectIndex] != Unreachable;

    /// <summary>Builds the tree. Progress reports objects reached.</summary>
    public static RootPathTree Build(HeapGraph graph, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var count = graph.ObjectCount;
        var parent = new int[count];
        Array.Fill(parent, Unreachable);
        var queue = new int[count];
        var tail = 0;

        // Sources, best root kind first: the queue order decides ties at equal distance.
        foreach (var source in PathRoots.Sources(graph))
        {
            parent[source] = Source;
            queue[tail++] = source;
        }

        var starts = graph.EdgeStarts.Span;
        var edges = graph.Edges.Span;
        for (var head = 0; head < tail; head++)
        {
            var current = queue[head];
            foreach (var target in edges[starts[current]..starts[current + 1]])
            {
                if (parent[target] == Unreachable)
                {
                    parent[target] = current;
                    queue[tail++] = target;
                }
            }

            if ((head & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(head);
            }
        }

        progress?.Report(tail);
        return new RootPathTree(parent, tail);
    }
}
