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

using HeapScope.Analysis.Retention;

namespace HeapScope.Analysis.Sets;

/// <summary>
/// The members of a set no other member dominates (sprint 3.1), most retained first: <see cref="Tops"/> with, at the same
/// position, how many other members are below each in the dominator tree; and the members no root reaches.
/// </summary>
public sealed class SetDominatorList(int[] tops, int[] below, long unreachable)
{
    /// <summary>The top members' object indices, most retained first (ties by object index).</summary>
    public int[] Tops { get; } = tops;

    /// <summary>Other members below each top member.</summary>
    public int[] Below { get; } = below;

    public int Count => Tops.Length;

    public long UnreachableMembers { get; } = unreachable;
}

/// <summary>
/// The members of a set that no other member dominates (sprint 3.1): what the set's retention divides into, each with its
/// retained size (exact, other members below it included). No two of them overlap, so their retained sizes add up.
/// Two walks down the dominator tree from the root (sprint 3.1 review: chunked stacks and exact arrays, no doubling
/// lists): the first marks the tops, the second counts the members below each; then a sort by retained size.
/// </summary>
public static class SetDominators
{
    /// <summary>
    /// Working memory per object at worst (every object a top): the tops, the members below each, the sort's 16-byte keys
    /// and its order, and the two sorted arrays (36 bytes per top, alive together at the sort), with a bit per object for
    /// the tops; the walk's chunked stack (8 bytes per entry) is gone by then.
    /// </summary>
    public const int WorkingBytesPerObject = 37;

    private const int NoTop = -1;

    public static SetDominatorList Top(Dominators dominators, ObjectSet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dominators);
        ArgumentNullException.ThrowIfNull(set);
        long unreachable = 0;
        foreach (var member in set.Members)
        {
            if (!dominators.IsReachable(member))
            {
                unreachable++;
            }
        }

        // First walk: a top is a member with no member above it.
        var isTop = new ulong[(dominators.ObjectCount + 63) / 64];
        var count = 0;
        Walk(dominators, set, cancellationToken, (vertex, above) =>
        {
            if (above == NoTop)
            {
                isTop[vertex >> 6] |= 1UL << (vertex & 63);
                count++;
                return vertex;
            }

            return above;
        });

        var tops = new int[count];
        var filled = 0;
        for (var w = 0; w < isTop.Length && filled < count; w++)
        {
            for (var word = isTop[w]; word != 0; word &= word - 1)
            {
                tops[filled++] = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(word);
            }
        }

        // Second walk: each member below a top counts for that top (found by binary search in the sorted tops).
        var below = new int[count];
        Walk(dominators, set, cancellationToken, (vertex, above) =>
        {
            if (above == NoTop)
            {
                return Array.BinarySearch(tops, vertex);
            }

            below[above]++;
            return above;
        });

        // Most retained first; equal sizes by object index. The keys pack the inverted size above the index.
        var keys = new UInt128[count];
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            keys[i] = ((UInt128)(ulong.MaxValue - dominators.RetainedSize(tops[i])) << 32) | (uint)tops[i];
            order[i] = i;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(keys, order);
        var sortedTops = new int[count];
        var sortedBelow = new int[count];
        for (var i = 0; i < count; i++)
        {
            sortedTops[i] = tops[order[i]];
            sortedBelow[i] = below[order[i]];
        }

        return new SetDominatorList(sortedTops, sortedBelow, unreachable);
    }

    /// <summary>
    /// Walks the dominator tree from the root; at each member, <paramref name="atMember"/> gets the member and the slot of
    /// the top above it (or <see cref="NoTop"/>) and returns the slot its subtree carries down.
    /// </summary>
    private static void Walk(Dominators dominators, ObjectSet set, CancellationToken cancellationToken, Func<int, int, int> atMember)
    {
        // Entries are pushed as two ints: the slot, then the vertex.
        var stack = new ChunkedStack();
        stack.Push(NoTop);
        stack.Push(dominators.Root);
        long visited = 0;
        while (stack.TryPop(out var vertex))
        {
            stack.TryPop(out var slot);
            if ((++visited & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (vertex != dominators.Root && set.Contains(vertex))
            {
                slot = atMember(vertex, slot);
            }

            foreach (var child in dominators.ChildrenOf(vertex))
            {
                stack.Push(slot);
                stack.Push(child);
            }
        }
    }
}
