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
using HeapScope.Core.Graph;

namespace HeapScope.Analysis;

/// <summary>One object referencing the inspected object, and how many of its references point there.</summary>
public readonly record struct Referrer(int ObjectIndex, int References);

/// <summary>
/// The objects of one referrer type that reference a set of objects (sprint 2.1).
/// </summary>
/// <param name="TypeIndex">The referrers' type.</param>
/// <param name="Objects">Distinct objects of this type that reference the set.</param>
/// <param name="References">References from them into the set.</param>
/// <param name="Targets">Distinct objects of the set they reference.</param>
/// <param name="Bytes">Shallow size of the referring objects.</param>
public readonly record struct ReferrerTypeRow(int TypeIndex, long Objects, long References, long Targets, ulong Bytes);

/// <summary>Who references an object or the objects of a type, from <see cref="ReverseReferences"/> (sprint 2.1).</summary>
public static class IncomingReferences
{
    /// <summary>Targets per cancellation check.</summary>
    private const int Chunk = 1 << 16;

    /// <summary>
    /// The distinct objects referencing <paramref name="objectIndex"/>, ascending, each with its number of references to
    /// it (an object holding it in two fields counts 2; a self reference counts too).
    /// </summary>
    public static Referrer[] ForObject(ReverseReferences reverse, int objectIndex)
    {
        ArgumentNullException.ThrowIfNull(reverse);
        var referrers = reverse.GetReferrers(objectIndex);
        var result = new List<Referrer>();
        for (var i = 0; i < referrers.Length;)
        {
            var start = i;
            while (i < referrers.Length && referrers[i] == referrers[start])
            {
                i++;
            }

            result.Add(new Referrer(referrers[start], i - start));
        }

        return [.. result];
    }

    /// <summary>
    /// The referrers of every object whose type is set in <paramref name="targetTypes"/>, grouped by the referrer's type
    /// name and module file name (ADR-018's key, so one type loaded twice is one row; <see cref="ReferrerTypeRow.TypeIndex"/>
    /// is the group's first type), in type index order. Also counts the target objects and the references into them.
    /// One pass over the targets' incoming references, with a bit per object to count each referrer once.
    /// </summary>
    public static ReferrerTypeRow[] ForTypes(HeapGraph graph, ReverseReferences reverse, IReadOnlyList<bool> targetTypes, out long targetObjects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(reverse);
        ArgumentNullException.ThrowIfNull(targetTypes);
        var (groupOf, firstType) = GroupByNameAndModule(graph);
        var groups = firstType.Length;
        var objects = new long[groups];
        var references = new long[groups];
        var targets = new long[groups];
        var bytes = new ulong[groups];

        // The last target each group was counted for, so a target held by several objects of one group counts once.
        var lastTarget = new int[groups];
        Array.Fill(lastTarget, -1);
        var seen = new BitArray(graph.ObjectCount);
        var typeIndices = graph.TypeIndices.Span;
        targetObjects = 0;
        for (var target = 0; target < typeIndices.Length; target++)
        {
            if ((target & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!targetTypes[typeIndices[target]])
            {
                continue;
            }

            targetObjects++;
            foreach (var referrer in reverse.GetReferrers(target))
            {
                var group = groupOf[typeIndices[referrer]];
                references[group]++;
                if (lastTarget[group] != target)
                {
                    lastTarget[group] = target;
                    targets[group]++;
                }

                if (!seen[referrer])
                {
                    seen[referrer] = true;
                    objects[group]++;
                    bytes[group] += graph.GetSize(referrer);
                }
            }
        }

        var rows = new List<ReferrerTypeRow>();
        for (var g = 0; g < groups; g++)
        {
            if (references[g] > 0)
            {
                rows.Add(new ReferrerTypeRow(firstType[g], objects[g], references[g], targets[g], bytes[g]));
            }
        }

        return [.. rows];
    }

    /// <summary>A group per distinct type name and module file name, numbered in order of the group's first type.</summary>
    private static (int[] GroupOf, int[] FirstType) GroupByNameAndModule(HeapGraph graph)
    {
        var groupOf = new int[graph.Types.Count];
        var firstType = new List<int>();
        var groups = new Dictionary<(string Name, string Module), int>();
        for (var t = 0; t < groupOf.Length; t++)
        {
            var key = (graph.Types.GetName(t), TypeGroups.ModuleKey(graph.Types.GetModule(t)).ToUpperInvariant());
            if (!groups.TryGetValue(key, out var group))
            {
                group = firstType.Count;
                groups.Add(key, group);
                firstType.Add(t);
            }

            groupOf[t] = group;
        }

        return (groupOf, [.. firstType]);
    }
}
