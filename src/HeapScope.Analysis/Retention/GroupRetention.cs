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
/// Retained sizes of groups of objects: a type, a namespace or an assembly (sprint 2.3, decision 6). A group's retained
/// size here is a lower bound: the sum of the retained sizes of its objects that no other object of the same group
/// dominates, so nothing is counted twice. Objects held only by several of the group's objects together are not counted;
/// the exact retention of a set is 2.4's.
/// </summary>
public static class GroupRetention
{
    /// <summary>Bytes per object of working arrays: the dominator tree's children (row offsets, rows, a cursor per object).</summary>
    public const int WorkingBytesPerObject = 12;

    /// <summary>Items between cancellation checks and progress reports (DEVELOPER.md 4: at most 64K).</summary>
    private const int Chunk = 1 << 16;

    /// <summary>
    /// The lower bound of each group's retained size. <paramref name="groupOfType"/> gives each type table entry's group
    /// (-1 for none); groups are numbered from 0 to <paramref name="groupCount"/> - 1. Walks the dominator tree once,
    /// depth first, counting per group how many of the current object's dominators belong to it. Progress reports objects
    /// visited.
    /// </summary>
    public static ulong[] Compute(
        HeapGraph graph,
        Dominators dominators,
        int[] groupOfType,
        int groupCount,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dominators);
        ArgumentNullException.ThrowIfNull(groupOfType);
        var n = graph.ObjectCount;
        if (dominators.ObjectCount != n || groupOfType.Length != graph.Types.Count)
        {
            throw new ArgumentException("The dominators or the groups do not match the graph.");
        }

        // The dominator tree as a CSR of children; the virtual root is vertex n.
        var idom = dominators.ImmediateDominators.Span;
        var starts = new int[n + 2];
        var reachable = 0;
        for (var i = 0; i < n; i++)
        {
            var parent = Parent(idom[i], n);
            if (parent >= 0)
            {
                starts[parent + 1]++;
                reachable++;
            }

            if ((i & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        for (var i = 1; i < starts.Length; i++)
        {
            starts[i] += starts[i - 1];
        }

        var children = GC.AllocateUninitializedArray<int>(reachable);
        var cursor = GC.AllocateUninitializedArray<int>(n + 1);
        starts.AsSpan(0, n + 1).CopyTo(cursor);
        for (var i = 0; i < n; i++)
        {
            var parent = Parent(idom[i], n);
            if (parent >= 0)
            {
                children[cursor[parent]++] = i;
            }

            if ((i & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Depth first without a stack: each vertex's cursor is its next child, and the search climbs back through idom.
        starts.AsSpan(0, n + 1).CopyTo(cursor);
        var types = graph.TypeIndices.Span;
        var retained = dominators.RetainedSizes.Span;
        var active = new int[groupCount];
        var totals = new ulong[groupCount];
        var current = n;
        long visited = 0;
        while (true)
        {
            if (cursor[current] < starts[current + 1])
            {
                var child = children[cursor[current]++];
                var group = groupOfType[types[child]];
                if (group >= 0)
                {
                    if (active[group]++ == 0)
                    {
                        totals[group] += retained[child];
                    }
                }

                current = child;
                if ((++visited & (Chunk - 1)) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(visited);
                }

                continue;
            }

            if (current == n)
            {
                break;
            }

            var leaving = groupOfType[types[current]];
            if (leaving >= 0)
            {
                active[leaving]--;
            }

            current = Parent(idom[current], n);
        }

        progress?.Report(visited);
        return totals;
    }

    /// <summary>The vertex of an object's dominator: an object index, n for the virtual root, -1 when unreachable.</summary>
    private static int Parent(int immediateDominator, int n) => immediateDominator switch
    {
        DominatorTree.VirtualRoot => n,
        DominatorTree.Unreachable => -1,
        var d => d,
    };
}
