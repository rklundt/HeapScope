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

using System.Collections;
using System.Runtime.InteropServices;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Retention;

/// <summary>The steps of a dominator computation, for progress.</summary>
public enum DominatorPhase
{
    /// <summary>Numbering the objects depth first from the roots.</summary>
    Numbering = 0,

    /// <summary>Finding each object's immediate dominator.</summary>
    Dominators = 1,

    /// <summary>Summing retained sizes.</summary>
    RetainedSizes = 2,
}

/// <summary>Progress of a dominator computation: objects done of the total in one phase.</summary>
public readonly record struct DominatorProgress(DominatorPhase Phase, long Done, long Total);

/// <summary>
/// Immediate dominators and retained sizes (sprint 2.3, ARCHITECTURE.md 6.4). An object's immediate dominator is the
/// closest object every path from a root to it passes through; its retained size is its own size plus the sizes of the
/// objects it dominates, the bytes that would become unreachable without it. Paths start at a virtual root joined to
/// exactly the objects of <see cref="PathRoots.Sources"/> (retaining roots, then frozen objects), so retention paths and
/// dominators agree on what is alive by itself.
/// </summary>
/// <remarks>
/// Semi-NCA (Georgiadis; the Lengauer-Tarjan family), chosen in sprint 1.9: a depth-first preorder, semidominators in one
/// reverse preorder pass with path-compressed evaluation, then each immediate dominator as the nearest common ancestor of
/// its depth-first parent and its semidominator. Every step is iterative: the depth-first search keeps no stack (it climbs
/// back through the parents and keeps each vertex's next edge in an array used later for labels) and the path compression
/// reverses pointers instead of keeping the path, so a list of millions of objects costs nothing extra. Retained sizes are summed in reverse preorder, since a dominator is a depth-first ancestor.
/// The results go into spans the caller gives: managed arrays, or sections of a mapped sidecar being written.
/// </remarks>
public static class DominatorTree
{
    /// <summary>Immediate dominator of an object no root reaches; its retained size is 0.</summary>
    public const int Unreachable = -1;

    /// <summary>Immediate dominator of an object only the roots dominate (a root's own object, or one several roots hold).</summary>
    public const int VirtualRoot = -2;

    /// <summary>The version of this computation's own rules; part of <see cref="SemanticsVersion"/>.</summary>
    public const int AlgorithmVersion = 1;

    /// <summary>
    /// The version of the rules the results depend on (decision 2): where paths start (<see cref="PathRoots.SemanticsVersion"/>)
    /// and this computation's own. A dominators sidecar computed under another version is rebuilt.
    /// </summary>
    public const int SemanticsVersion = (PathRoots.SemanticsVersion << 16) | AlgorithmVersion;

    /// <summary>Bytes per object kept: the immediate dominator (4) and the retained size (8).</summary>
    public const int KeptBytesPerObject = 12;

    /// <summary>
    /// Bytes per object of managed working arrays while computing (vertex, parent and label by preorder number, plus a bit
    /// per object for the roots), rounded up. The semidominators and the forest's ancestors live in the retained sizes
    /// output until the sizes are summed, and the preorder numbers in the immediate dominators output (review M4), so
    /// managed outputs come on top only when the outputs themselves are managed.
    /// </summary>
    public const int WorkingBytesPerObject = 13;

    /// <summary>Items between cancellation checks and progress reports (DEVELOPER.md 4: at most 64K).</summary>
    private const int Chunk = 1 << 16;

    /// <summary>
    /// Computes the immediate dominator (an object index, <see cref="VirtualRoot"/> or <see cref="Unreachable"/>) and the
    /// retained size of every object of <paramref name="graph"/> into <paramref name="immediateDominators"/> and
    /// <paramref name="retainedSizes"/> (one element per object each). <paramref name="reverse"/> is the graph's reverse
    /// references index. Returns the number of objects the roots reach.
    /// </summary>
    public static int Compute(
        HeapGraph graph,
        ReverseReferences reverse,
        Span<int> immediateDominators,
        Span<ulong> retainedSizes,
        IProgress<DominatorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(reverse);
        var n = graph.ObjectCount;
        if (immediateDominators.Length != n || retainedSizes.Length != n)
        {
            throw new ArgumentException("The output spans must have one element per object.");
        }

        if (reverse.ObjectCount != n || reverse.EdgeCount != graph.EdgeCount)
        {
            throw new ArgumentException("The reverse references belong to another graph.", nameof(reverse));
        }

        var sources = PathRoots.Sources(graph).ToArray();
        var isSource = new BitArray(n);
        foreach (var source in sources)
        {
            isSource[source] = true;
        }

        // Preorder numbers by object live in the first output until the dominators are known; they are not needed after.
        var number = immediateDominators;
        number.Fill(Unreachable);
        var vertex = GC.AllocateUninitializedArray<int>(n + 1);
        var parent = GC.AllocateUninitializedArray<int>(n + 1);
        var label = GC.AllocateUninitializedArray<int>(n + 1);
        var count = Number(graph, sources, number, vertex, parent, label, progress, cancellationToken);

        // Semidominators in reverse preorder; ancestor and label form the path-compressed forest of evaluated vertices.
        // Semidominators and ancestors are kept by preorder number minus one in the retained sizes output, which is not
        // used until the sizes are summed (review M4): the virtual root (0) is never evaluated, its semidominator is never
        // read and it has no ancestor, so 2 ints per reachable object fit the output's 8 bytes per object.
        var reachable = count - 1;
        var spare = MemoryMarshal.Cast<ulong, int>(retainedSizes);
        var semi = spare[..reachable];
        var ancestor = spare.Slice(reachable, reachable);
        label[0] = 0;
        for (var w = 1; w < count; w++)
        {
            semi[w - 1] = w;
            label[w] = w;
            ancestor[w - 1] = -1;
        }

        var inStarts = reverse.InEdgeStarts.Span;
        var inEdges = reverse.InEdges.Span;
        for (var w = count - 1; w > 0; w--)
        {
            var node = vertex[w];
            if (isSource[node])
            {
                semi[w - 1] = 0;
            }

            foreach (var referrer in inEdges[inStarts[node]..inStarts[node + 1]])
            {
                var v = number[referrer];
                if (v < 0)
                {
                    continue; // a referrer no root reaches
                }

                var u = Eval(v, ancestor, label, semi);
                if (semi[u - 1] < semi[w - 1])
                {
                    semi[w - 1] = semi[u - 1];
                }
            }

            ancestor[w - 1] = parent[w];
            if (((count - w) & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DominatorProgress(DominatorPhase.Dominators, count - w, count - 1));
            }
        }

        // Each immediate dominator is the nearest common ancestor of the parent and the semidominator on the depth-first
        // tree; in preorder an ancestor has a smaller number, and every smaller vertex is final already.
        var idom = parent;
        for (var w = 1; w < count; w++)
        {
            while (idom[w] > semi[w - 1])
            {
                idom[w] = idom[idom[w]];
            }

            if ((w & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // From preorder numbers to object indices. Unreachable objects still hold Unreachable from the numbering.
        for (var w = 1; w < count; w++)
        {
            immediateDominators[vertex[w]] = idom[w] == 0 ? VirtualRoot : vertex[idom[w]];
            if ((w & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        progress?.Report(new DominatorProgress(DominatorPhase.Dominators, count - 1, count - 1));
        SumRetainedSizes(graph, vertex, count, immediateDominators, retainedSizes, progress, cancellationToken);
        return count - 1;
    }

    /// <summary>
    /// Numbers the objects depth first from the virtual root (number 0), following the sources, then references in order.
    /// No stack: <paramref name="next"/> holds each vertex's next edge, and the search climbs back through
    /// <paramref name="parent"/>. Returns the vertex count, the virtual root included.
    /// </summary>
    private static int Number(
        HeapGraph graph,
        int[] sources,
        Span<int> number,
        int[] vertex,
        int[] parent,
        int[] next,
        IProgress<DominatorProgress>? progress,
        CancellationToken cancellationToken)
    {
        var n = graph.ObjectCount;
        var edgeStarts = graph.EdgeStarts.Span;
        var edges = graph.Edges.Span;
        vertex[0] = -1;
        parent[0] = 0;
        next[0] = 0;
        var count = 1;
        var current = 0;
        while (true)
        {
            int child;
            if (current == 0)
            {
                if (next[0] == sources.Length)
                {
                    break;
                }

                child = sources[next[0]++];
            }
            else
            {
                var node = vertex[current];
                var edge = edgeStarts[node] + next[current];
                if (edge == edgeStarts[node + 1])
                {
                    current = parent[current];
                    continue;
                }

                next[current]++;
                child = edges[edge];
            }

            if (number[child] < 0)
            {
                number[child] = count;
                vertex[count] = child;
                parent[count] = current;
                next[count] = 0;
                current = count++;
                if ((count & (Chunk - 1)) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new DominatorProgress(DominatorPhase.Numbering, count, n));
                }
            }
        }

        progress?.Report(new DominatorProgress(DominatorPhase.Numbering, count - 1, count - 1));
        return count;
    }

    /// <summary>
    /// The vertex with the smallest semidominator on <paramref name="v"/>'s path to the root of its tree in the forest,
    /// compressing the path as it goes. No buffer (review M5): the climb reverses each ancestor pointer so the path can be
    /// walked back down from its top, where compression must start; a path can be as long as the reachable objects (a
    /// circular list). Ancestors and semidominators are indexed by preorder number minus one; the virtual root (0) has no
    /// ancestor.
    /// </summary>
    private static int Eval(int v, Span<int> ancestor, int[] label, Span<int> semi)
    {
        if (ancestor[v - 1] < 0)
        {
            return v;
        }

        // Up: stop at the vertex whose ancestor is a tree's root, pointing each vertex passed at the one below it.
        var x = v;
        var below = -1;
        while (true)
        {
            var a = ancestor[x - 1];
            if (a == 0 || ancestor[a - 1] < 0)
            {
                break;
            }

            ancestor[x - 1] = below;
            below = x;
            x = a;
        }

        // Down: each vertex takes its (already compressed) ancestor's label when smaller, and that ancestor's ancestor.
        var above = x;
        for (var current = below; current >= 0;)
        {
            var next = ancestor[current - 1];
            if (semi[label[above] - 1] < semi[label[current] - 1])
            {
                label[current] = label[above];
            }

            ancestor[current - 1] = ancestor[above - 1];
            above = current;
            current = next;
        }

        return label[v];
    }

    /// <summary>Adds every object's size into itself and then into its dominator, in reverse preorder.</summary>
    private static void SumRetainedSizes(
        HeapGraph graph,
        int[] vertex,
        int count,
        ReadOnlySpan<int> immediateDominators,
        Span<ulong> retained,
        IProgress<DominatorProgress>? progress,
        CancellationToken cancellationToken)
    {
        retained.Clear();
        var sizes = graph.Sizes.Span;
        for (var w = count - 1; w > 0; w--)
        {
            var node = vertex[w];
            var size = sizes[node];
            retained[node] += size == HeapGraph.LargeSizeMarker ? graph.GetSize(node) : size;
            var dominator = immediateDominators[node];
            if (dominator >= 0)
            {
                retained[dominator] += retained[node];
            }

            if (((count - w) & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DominatorProgress(DominatorPhase.RetainedSizes, count - w, count - 1));
            }
        }

        progress?.Report(new DominatorProgress(DominatorPhase.RetainedSizes, count - 1, count - 1));
    }
}
