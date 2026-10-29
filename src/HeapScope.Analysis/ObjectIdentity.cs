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

using HeapScope.Analysis.Sets;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis;

/// <summary>
/// Objects of two snapshots of one process matched by address and type (sprint 3.3, ADR-032): an object of the later
/// snapshot survived when the earlier one has an object of the same type key (name and module file) at its address. A
/// compacting collection moves objects, so a moved survivor counts as dead in the earlier snapshot and new in the later.
/// One merge over both snapshots' sorted addresses; a bit per object of each side.
/// </summary>
public sealed class ObjectIdentity
{
    private readonly ulong[] _survivedInCurrent;
    private readonly ulong[] _survivedInBaseline;

    private ObjectIdentity(ulong[] survivedInCurrent, ulong[] survivedInBaseline, long survivors)
    {
        _survivedInCurrent = survivedInCurrent;
        _survivedInBaseline = survivedInBaseline;
        Survivors = survivors;
    }

    /// <summary>Objects matched on both sides.</summary>
    public long Survivors { get; }

    /// <summary>Memory one match of these graphs takes: a bit per object of each.</summary>
    public static long WorkingBytes(HeapGraph baseline, HeapGraph current) =>
        (((long)(baseline?.ObjectCount ?? 0) + 63) / 64 * 8) + (((long)(current?.ObjectCount ?? 0) + 63) / 64 * 8);

    public bool SurvivedInCurrent(int objectIndex) => (_survivedInCurrent[objectIndex >> 6] & (1UL << (objectIndex & 63))) != 0;

    public bool SurvivedInBaseline(int objectIndex) => (_survivedInBaseline[objectIndex >> 6] & (1UL << (objectIndex & 63))) != 0;

    /// <summary>Matches the objects of <paramref name="baseline"/> and <paramref name="current"/>.</summary>
    public static ObjectIdentity Match(HeapGraph baseline, HeapGraph current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        // One id per type key across both snapshots.
        var keys = new Dictionary<(string, string), int>(TypeKeyComparer.Type);
        var baselineKeys = KeyIds(baseline, keys);
        var currentKeys = KeyIds(current, keys);
        var inCurrent = new ulong[(current.ObjectCount + 63) / 64];
        var inBaseline = new ulong[(baseline.ObjectCount + 63) / 64];
        var baseAddresses = baseline.Addresses.Span;
        var currentAddresses = current.Addresses.Span;
        var baseTypes = baseline.TypeIndices.Span;
        var currentTypes = current.TypeIndices.Span;
        long survivors = 0;
        var b = 0;
        for (var c = 0; c < currentAddresses.Length && b < baseAddresses.Length; c++)
        {
            if ((c & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var address = currentAddresses[c];
            while (b < baseAddresses.Length && baseAddresses[b] < address)
            {
                b++;
            }

            if (b < baseAddresses.Length && baseAddresses[b] == address && baselineKeys[baseTypes[b]] == currentKeys[currentTypes[c]])
            {
                inCurrent[c >> 6] |= 1UL << (c & 63);
                inBaseline[b >> 6] |= 1UL << (b & 63);
                survivors++;
            }
        }

        return new ObjectIdentity(inCurrent, inBaseline, survivors);
    }

    /// <summary>New objects per type index of the current snapshot (no match in the baseline).</summary>
    public long[] NewPerType(HeapGraph current, CancellationToken cancellationToken = default) => Unmatched(current, _survivedInCurrent, cancellationToken);

    /// <summary>Dead objects per type index of the baseline (no match in the current snapshot).</summary>
    public long[] DeadPerType(HeapGraph baseline, CancellationToken cancellationToken = default) => Unmatched(baseline, _survivedInBaseline, cancellationToken);

    /// <summary>
    /// The members of <paramref name="set"/> with <paramref name="change"/>: <paramref name="set"/> belongs to the current
    /// snapshot for new and survived objects, to the baseline for dead ones.
    /// </summary>
    public int[] Select(ObjectSet set, ObjectChangeKind change)
    {
        ArgumentNullException.ThrowIfNull(set);
        var mask = change == ObjectChangeKind.Dead ? _survivedInBaseline : _survivedInCurrent;
        var wantSurvivor = change == ObjectChangeKind.Survived;
        var count = 0;
        foreach (var o in set.Members)
        {
            if (((mask[o >> 6] & (1UL << (o & 63))) != 0) == wantSurvivor)
            {
                count++;
            }
        }

        var members = new int[count];
        var next = 0;
        foreach (var o in set.Members)
        {
            if (((mask[o >> 6] & (1UL << (o & 63))) != 0) == wantSurvivor)
            {
                members[next++] = o;
            }
        }

        return members;
    }

    private static int[] KeyIds(HeapGraph graph, Dictionary<(string, string), int> keys)
    {
        var ids = new int[graph.Types.Count];
        for (var t = 0; t < ids.Length; t++)
        {
            var key = (graph.Types.GetName(t), TypeGroups.ModuleKey(graph.Types.GetModule(t)));
            if (!keys.TryGetValue(key, out var id))
            {
                keys[key] = id = keys.Count;
            }

            ids[t] = id;
        }

        return ids;
    }

    private static long[] Unmatched(HeapGraph graph, ulong[] matched, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var counts = new long[graph.Types.Count];
        var types = graph.TypeIndices.Span;
        for (var o = 0; o < types.Length; o++)
        {
            if ((o & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if ((matched[o >> 6] & (1UL << (o & 63))) == 0)
            {
                counts[types[o]]++;
            }
        }

        return counts;
    }
}

/// <summary>How an object fared between two snapshots (Contracts' <c>ObjectChange</c>).</summary>
public enum ObjectChangeKind
{
    New = 0,
    Dead = 1,
    Survived = 2,
}
