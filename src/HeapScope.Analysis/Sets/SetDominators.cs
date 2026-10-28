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

/// <summary>A member no other member dominates, with how many other members are below it in the dominator tree.</summary>
public readonly record struct SetDominator(int ObjectIndex, int MembersBelow);

/// <summary>
/// The members of a set that no other member dominates (sprint 3.1): what the set's retention divides into, each with its
/// retained size (exact, other members below it included). No two of them overlap, so their retained sizes add up.
/// One walk down the dominator tree from the root that stops recording below the first member on each branch.
/// </summary>
public static class SetDominators
{
    /// <summary>Working memory per object at worst: the walk's stack holds a vertex and its top member per entry.</summary>
    public const int WorkingBytesPerObject = 8;

    /// <summary>The top members in dominator tree order, and the members no root reaches.</summary>
    public static IReadOnlyList<SetDominator> Top(Dominators dominators, ObjectSet set, out long unreachableMembers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dominators);
        ArgumentNullException.ThrowIfNull(set);
        unreachableMembers = 0;
        foreach (var member in set.Members)
        {
            if (!dominators.IsReachable(member))
            {
                unreachableMembers++;
            }
        }

        var tops = new List<SetDominator>();
        var below = new List<int>();

        // Each entry is a vertex and the top member above it (-1 for none); the root is the virtual vertex.
        var stack = new Stack<(int Vertex, int Top)>();
        stack.Push((dominators.Root, -1));
        long visited = 0;
        while (stack.TryPop(out var entry))
        {
            if ((++visited & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var top = entry.Top;
            if (entry.Vertex != dominators.Root && set.Contains(entry.Vertex))
            {
                if (top < 0)
                {
                    top = tops.Count;
                    tops.Add(new SetDominator(entry.Vertex, 0));
                    below.Add(0);
                }
                else
                {
                    below[top]++;
                }
            }

            foreach (var child in dominators.ChildrenOf(entry.Vertex))
            {
                stack.Push((child, top));
            }
        }

        return [.. tops.Select((t, i) => t with { MembersBelow = below[i] })];
    }
}
