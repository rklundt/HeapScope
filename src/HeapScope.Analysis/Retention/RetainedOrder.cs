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

namespace HeapScope.Analysis.Retention;

/// <summary>
/// The order the retention explorer shows objects in (sprint 2.4): largest retained size first, then by object index, so
/// it never varies between runs. Only the rows a query shows are ordered: the largest k of a node's children are found in
/// one pass with a heap of k (O(m log k)), so a node with millions of children answers in milliseconds.
/// </summary>
public static class RetainedOrder
{
    /// <summary>
    /// The first <paramref name="count"/> of <paramref name="members"/> (those <paramref name="include"/> accepts, when
    /// given) in retained order; fewer when fewer are accepted.
    /// </summary>
    public static int[] Top(ReadOnlySpan<int> members, int count, Dominators dominators, Func<int, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(dominators);
        var sizes = dominators.RetainedSizes.Span;
        count = Math.Clamp(count, 0, members.Length);
        var heap = new int[count];
        if (count == 0)
        {
            return heap;
        }

        // A heap whose root is the last of the kept ones in the order: a member before it replaces it.
        var filled = 0;
        foreach (var member in members)
        {
            if (include is not null && !include(member))
            {
                continue;
            }

            if (filled < count)
            {
                heap[filled] = member;
                Up(heap, filled++, sizes);
            }
            else if (Before(member, heap[0], sizes))
            {
                heap[0] = member;
                Down(heap, count, sizes);
            }
        }

        if (filled < count)
        {
            Array.Resize(ref heap, filled);
            count = filled;
        }

        // Taking the root off one at a time leaves them in order from the back.
        for (var last = count - 1; last > 0; last--)
        {
            (heap[0], heap[last]) = (heap[last], heap[0]);
            Down(heap, last, sizes);
        }

        return heap;
    }

    /// <summary>Whether <paramref name="a"/> comes before <paramref name="b"/>: larger retained size, then lower index.</summary>
    public static bool Before(int a, int b, ReadOnlySpan<ulong> sizes) =>
        sizes[a] != sizes[b] ? sizes[a] > sizes[b] : a < b;

    private static void Up(int[] heap, int at, ReadOnlySpan<ulong> sizes)
    {
        while (at > 0)
        {
            var parent = (at - 1) / 2;
            if (!Before(heap[parent], heap[at], sizes))
            {
                return;
            }

            (heap[parent], heap[at]) = (heap[at], heap[parent]);
            at = parent;
        }
    }

    private static void Down(int[] heap, int length, ReadOnlySpan<ulong> sizes)
    {
        var at = 0;
        while (true)
        {
            var later = at;
            var left = (2 * at) + 1;
            if (left < length && Before(heap[later], heap[left], sizes))
            {
                later = left;
            }

            if (left + 1 < length && Before(heap[later], heap[left + 1], sizes))
            {
                later = left + 1;
            }

            if (later == at)
            {
                return;
            }

            (heap[later], heap[at]) = (heap[at], heap[later]);
            at = later;
        }
    }
}
