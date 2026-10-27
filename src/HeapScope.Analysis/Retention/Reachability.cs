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
using System.Runtime.CompilerServices;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Retention;

/// <summary>
/// Which objects a root reaches (sprint 2.6, for the leak detectors): a walk from the retaining roots and the frozen
/// objects over references and dependent handles, a bit per object. Objects no root reaches are garbage the GC has not
/// collected yet; a leak detector counts reachable objects only. Unlike <see cref="PathRoots.Sources"/>, the finalizer
/// queue does not count (review M7): the GC already found its objects dead, and they only wait for their finalizers.
/// </summary>
public sealed class Reachability
{
    // One per graph, computed once (review M6): the graph never changes, and five detectors and the instances scope ask.
    private static readonly ConditionalWeakTable<HeapGraph, Reachability> s_cache = [];

    private readonly ulong[] _bits;

    private Reachability(ulong[] bits, long count)
    {
        _bits = bits;
        Count = count;
    }

    /// <summary>Bytes kept per object while the graph lives (the bit set; the walk's stack is chunked and small).</summary>
    public const double BytesPerObject = 0.125;

    /// <summary>Reachable objects.</summary>
    public long Count { get; }

    /// <summary>What "reachable" means and how many, for a detector's basis (logged once per line).</summary>
    public static string Describe(Reachability reachable) =>
        string.Create(CultureInfo.InvariantCulture, $"{reachable.Count} objects reachable (from retaining roots other than the finalizer queue, and frozen objects)");

    public bool IsReachable(int objectIndex) => (_bits[objectIndex >> 6] & (1UL << (objectIndex & 63))) != 0;

    /// <summary>The graph's reachability, computed on first use and kept as long as the graph.</summary>
    public static Reachability For(HeapGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (s_cache.TryGetValue(graph, out var known))
        {
            return known;
        }

        // Two callers at once may both walk; the first one stored wins.
        var computed = Compute(graph, cancellationToken);
        return s_cache.GetValue(graph, _ => computed);
    }

    public static Reachability Compute(HeapGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var bits = new ulong[(graph.ObjectCount + 63) >> 6];
        var stack = new ChunkedStack();
        long count = 0;
        foreach (var root in graph.Roots.Roots.Span)
        {
            if (root.IsRetaining && root.Kind != RootKind.FinalizerQueue && Mark(bits, root.ObjectIndex))
            {
                count++;
                stack.Push(root.ObjectIndex);
            }
        }

        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if (graph.HeapKinds[i] == HeapKind.Frozen && Mark(bits, i))
            {
                count++;
                stack.Push(i);
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
