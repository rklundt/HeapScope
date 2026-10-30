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
    private readonly ulong[] _bits;

    private Reachability(ulong[] bits, long count)
    {
        _bits = bits;
        Count = count;
    }

    /// <summary>Bytes kept per object while the snapshot keeps it (the bit set; the walk's stack is chunked and small).</summary>
    public const double BytesPerObject = 0.125;

    /// <summary>Reachable objects.</summary>
    public long Count { get; }

    /// <summary>What "reachable" means and how many, for a detector's basis (logged once per line).</summary>
    public static string Describe(Reachability reachable) =>
        string.Create(CultureInfo.InvariantCulture, $"{reachable.Count} objects reachable (from retaining roots other than the finalizer queue, and frozen objects)");

    public bool IsReachable(int objectIndex) => (_bits[objectIndex >> 6] & (1UL << (objectIndex & 63))) != 0;

    /// <summary>
    /// What the retention views count as alive (sprint 3.5): a walk from <see cref="PathRoots.Sources"/>, the finalizer
    /// queue included, so an object this leaves out has no retention path and retains nothing (ADR-024).
    /// </summary>
    public static Reachability ComputeFromPathRoots(HeapGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return Walk(graph, PathRoots.Sources(graph), cancellationToken);
    }

    /// <summary>
    /// The leak detectors' reachability: a walk from the retaining roots other than the finalizer queue, and the frozen
    /// objects. Nothing is kept here; the worker computes it once per snapshot and keeps it with the snapshot.
    /// </summary>
    public static Reachability Compute(HeapGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var starts = new List<int>();
        foreach (var root in graph.Roots.Roots.Span)
        {
            if (root.IsRetaining && root.Kind != RootKind.FinalizerQueue)
            {
                starts.Add(root.ObjectIndex);
            }
        }

        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if (graph.HeapKinds[i] == HeapKind.Frozen)
            {
                starts.Add(i);
            }
        }

        return Walk(graph, starts, cancellationToken);
    }

    private static Reachability Walk(HeapGraph graph, IEnumerable<int> starts, CancellationToken cancellationToken)
    {
        var bits = new ulong[(graph.ObjectCount + 63) >> 6];
        var stack = new ChunkedStack();
        long count = 0;
        foreach (var start in starts)
        {
            if (Mark(bits, start))
            {
                count++;
                stack.Push(start);
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
