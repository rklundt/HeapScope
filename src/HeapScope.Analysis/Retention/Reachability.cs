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
/// Which objects a root reaches (sprint 2.6, for the leak detectors): a walk from <see cref="PathRoots.Sources"/> over
/// references and dependent handles, a bit per object. Objects no root reaches are garbage the GC has not collected yet;
/// a leak detector counts reachable objects only.
/// </summary>
public sealed class Reachability
{
    private readonly ulong[] _bits;

    private Reachability(ulong[] bits, long count)
    {
        _bits = bits;
        Count = count;
    }

    /// <summary>Bytes the walk keeps per object (the bit set; the stack is chunked and small).</summary>
    public const double BytesPerObject = 0.125;

    /// <summary>Reachable objects.</summary>
    public long Count { get; }

    public bool IsReachable(int objectIndex) => (_bits[objectIndex >> 6] & (1UL << (objectIndex & 63))) != 0;

    public static Reachability Compute(HeapGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var bits = new ulong[(graph.ObjectCount + 63) >> 6];
        var stack = new ChunkedStack();
        long count = 0;
        foreach (var source in PathRoots.Sources(graph))
        {
            if (Mark(bits, source))
            {
                count++;
                stack.Push(source);
            }
        }

        while (stack.TryPop(out var node))
        {
            foreach (var next in graph.GetReferences(node))
            {
                if (Mark(bits, next))
                {
                    count++;
                    stack.Push(next);
                    if ((count & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }
        }

        return new Reachability(bits, count);
    }

    private static bool Mark(ulong[] bits, int index)
    {
        ref var word = ref bits[index >> 6];
        var bit = 1UL << (index & 63);
        if ((word & bit) != 0)
        {
            return false;
        }

        word |= bit;
        return true;
    }
}
