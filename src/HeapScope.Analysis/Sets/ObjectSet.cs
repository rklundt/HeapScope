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

namespace HeapScope.Analysis.Sets;

/// <summary>
/// The members of an object set in one snapshot (sprint 3.1, ADR-030): their object indices in ascending order, a bit per
/// object of the graph for membership tests, and their totals. Immutable; the worker caches it per snapshot.
/// </summary>
public sealed class ObjectSet
{
    private readonly ulong[] _mask;

    private ObjectSet(int[] members, ulong[] mask, ulong bytes, int types)
    {
        Members = members;
        _mask = mask;
        Bytes = bytes;
        TypeCount = types;
    }

    /// <summary>Member object indices, ascending.</summary>
    public int[] Members { get; }

    public int Count => Members.Length;

    /// <summary>The members' shallow bytes.</summary>
    public ulong Bytes { get; }

    /// <summary>Distinct type table entries among the members.</summary>
    public int TypeCount { get; }

    /// <summary>Memory held: the members and the mask.</summary>
    public long EstimatedBytes => (4L * Members.Length) + (8L * _mask.Length);

    /// <summary>Memory a set of a graph needs at most while built and kept (a bit and an index per object).</summary>
    public static long WorkingBytes(HeapGraph graph) => ((long)(graph?.ObjectCount ?? 0) * 4) + (((long)(graph?.ObjectCount ?? 0) + 63) / 64 * 8);

    /// <summary>The membership bits, one per object (read only by convention).</summary>
    internal ulong[] Mask => _mask;

    public bool Contains(int objectIndex) => (_mask[objectIndex >> 6] & (1UL << (objectIndex & 63))) != 0;

    /// <summary>The objects whose type is marked in <paramref name="typeInSet"/>.</summary>
    public static ObjectSet OfTypes(HeapGraph graph, bool[] typeInSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(typeInSet);
        if (typeInSet.Length != graph.Types.Count)
        {
            throw new ArgumentException("The type mask does not match the graph.", nameof(typeInSet));
        }

        var types = graph.TypeIndices.Span;
        var mask = new ulong[(types.Length + 63) / 64];
        for (var i = 0; i < types.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (typeInSet[types[i]])
            {
                mask[i >> 6] |= 1UL << (i & 63);
            }
        }

        return FromMask(graph, mask);
    }

    /// <summary>The objects at <paramref name="addresses"/>; addresses with no object (or inside one) are left out.</summary>
    public static ObjectSet OfAddresses(HeapGraph graph, IEnumerable<ulong> addresses)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(addresses);
        var mask = new ulong[(graph.ObjectCount + 63) / 64];
        foreach (var address in addresses)
        {
            if (graph.TryGetIndex(address, out var index))
            {
                mask[index >> 6] |= 1UL << (index & 63);
            }
        }

        return FromMask(graph, mask);
    }

    /// <summary>The objects at <paramref name="indices"/> (any order, repeats allowed).</summary>
    public static ObjectSet OfIndices(HeapGraph graph, IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(indices);
        var mask = new ulong[(graph.ObjectCount + 63) / 64];
        foreach (var index in indices)
        {
            if ((uint)index >= (uint)graph.ObjectCount)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), "An object index is outside the graph.");
            }

            mask[index >> 6] |= 1UL << (index & 63);
        }

        return FromMask(graph, mask);
    }

    /// <summary>The objects whose bit is set in <paramref name="mask"/> (one bit per object, taken over by the set).</summary>
    public static ObjectSet FromMask(HeapGraph graph, ulong[] mask)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Length != (graph.ObjectCount + 63) / 64)
        {
            throw new ArgumentException("The mask does not match the graph.", nameof(mask));
        }

        var count = 0;
        foreach (var word in mask)
        {
            count += System.Numerics.BitOperations.PopCount(word);
        }

        var members = new int[count];
        var filled = 0;
        ulong bytes = 0;
        var types = graph.TypeIndices.Span;
        var seen = new bool[graph.Types.Count];
        var typeCount = 0;
        for (var w = 0; w < mask.Length; w++)
        {
            var word = mask[w];
            while (word != 0)
            {
                var index = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(word);
                word &= word - 1;
                members[filled++] = index;
                bytes += graph.GetSize(index);
                if (!seen[types[index]])
                {
                    seen[types[index]] = true;
                    typeCount++;
                }
            }
        }

        return new ObjectSet(members, mask, bytes, typeCount);
    }
}
