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

namespace HeapScope.Analysis.Retention;

/// <summary>
/// One group of Similar Retention (sprint 3.2, ADR-031): members retained the same way.
/// </summary>
/// <param name="FirstType">The type of the group's first member.</param>
/// <param name="PathCount">Distinct key paths of the group's pattern.</param>
/// <param name="Shown">At most two of them: the first member's own path, and the one sharing the fewest steps with it.</param>
/// <param name="ExampleObject">The group's first member.</param>
public sealed record RetentionGroup(long Objects, ulong Bytes, int Types, int FirstType, int PathCount, IReadOnlyList<KeyPath> Shown, int ExampleObject);

/// <summary>A set's Similar Retention groups, largest first, and what they leave out.</summary>
/// <param name="GroupCount">All groups, the ones beyond <see cref="Groups"/> included.</param>
/// <param name="LimitedMembers">Members with more direct referrers than <see cref="SimilarRetention.MaxReferrers"/>.</param>
public sealed record SimilarRetentionSet(
    IReadOnlyList<RetentionGroup> Groups,
    int GroupCount,
    long Unreachable,
    ulong UnreachableBytes,
    long OtherObjects,
    ulong OtherBytes,
    long LimitedMembers);

/// <summary>
/// Groups a set's members by retention pattern (sprint 3.2, dotMemory's Similar Retention). A member's pattern is the set of
/// distinct key path signatures (<see cref="KeyPaths"/>: shortest path from a root, repeats of one type folded) of its
/// direct referrers that a root reaches, its own shortest path always among them, plus its root when it is one. Members
/// with the same pattern are retained the same way and form a group. Deterministic for one graph, so a group's rank names
/// it (<see cref="MembersOf"/>).
/// </summary>
public static class SimilarRetention
{
    /// <summary>Direct referrers looked at per member, its own shortest path's included (decision 1).</summary>
    public const int MaxReferrers = 16;

    /// <summary>Memory per member while grouping: its group number.</summary>
    public const int WorkingBytesPerMember = 4;

    /// <summary>The groups of <paramref name="set"/>, the largest <paramref name="maxGroups"/> of them with their paths.</summary>
    public static SimilarRetentionSet Group(HeapGraph graph, RootPathTree tree, ReverseReferences reverse, ObjectSet set, int maxGroups, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGroups, 1);
        var assigned = Assign(graph, tree, reverse, set, cancellationToken);
        var ranked = assigned.Ranked();
        var groups = new List<RetentionGroup>();
        long otherObjects = 0;
        ulong otherBytes = 0;
        for (var rank = 0; rank < ranked.Length; rank++)
        {
            var id = ranked[rank];
            var totals = assigned.Totals[id];
            if (rank >= maxGroups)
            {
                otherObjects += totals.Objects;
                otherBytes += totals.Bytes;
                continue;
            }

            var pattern = assigned.Patterns[id];
            var first = totals.FirstSignature;
            var shown = new List<int> { first };
            var mostDifferent = -1;
            var fewest = int.MaxValue;
            foreach (var signature in pattern)
            {
                if (signature == first)
                {
                    continue;
                }

                var shared = assigned.Signatures.SharedSteps(first, signature);
                if (shared < fewest)
                {
                    (fewest, mostDifferent) = (shared, signature);
                }
            }

            if (mostDifferent >= 0)
            {
                shown.Add(mostDifferent);
            }

            var paths = shown.Select(s => new KeyPath(assigned.Signatures.RootOf(s), assigned.Signatures.Steps(s), totals.Objects, totals.Bytes, totals.Example)).ToList();
            groups.Add(new RetentionGroup(totals.Objects, totals.Bytes, assigned.TypeCounts[id], graph.TypeIndices[totals.Example], pattern.Length, paths, totals.Example));
        }

        return new SimilarRetentionSet(groups, ranked.Length, assigned.Unreachable, assigned.UnreachableBytes, otherObjects, otherBytes, assigned.Limited);
    }

    /// <summary>
    /// The members of the group at <paramref name="rank"/> (as <see cref="Group"/> ranks them), ascending; empty when there
    /// is no such group.
    /// </summary>
    public static int[] MembersOf(HeapGraph graph, RootPathTree tree, ReverseReferences reverse, ObjectSet set, int rank, CancellationToken cancellationToken = default)
    {
        var assigned = Assign(graph, tree, reverse, set, cancellationToken);
        var ranked = assigned.Ranked();
        if (rank < 0 || rank >= ranked.Length)
        {
            return [];
        }

        var id = ranked[rank];
        var members = new int[assigned.Totals[id].Objects];
        var next = 0;
        for (var i = 0; i < set.Members.Length; i++)
        {
            if (assigned.GroupOfMember[i] == id)
            {
                members[next++] = set.Members[i];
            }
        }

        return members;
    }

    private static Assignment Assign(HeapGraph graph, RootPathTree tree, ReverseReferences reverse, ObjectSet set, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(reverse);
        ArgumentNullException.ThrowIfNull(set);
        var assignment = new Assignment(new PathSignatures(graph, tree), new int[set.Members.Length]);
        var patternIds = new Dictionary<int[], int>(PatternComparer.Instance);
        var types = new HashSet<long>();
        var pattern = new List<int>(MaxReferrers + 1);
        for (var i = 0; i < set.Members.Length; i++)
        {
            if ((i & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var o = set.Members[i];
            var size = graph.GetSize(o);
            if (!assignment.Signatures.TryOfObject(o, out var own))
            {
                assignment.GroupOfMember[i] = -1;
                assignment.Unreachable++;
                assignment.UnreachableBytes += size;
                continue;
            }

            pattern.Clear();
            pattern.Add(own);
            var parent = tree.Parent(o);
            var looked = 1;
            var referrers = reverse.GetReferrers(o);
            for (var r = 0; r < referrers.Length; r++)
            {
                var referrer = referrers[r];
                if ((r > 0 && referrers[r - 1] == referrer) || referrer == o || referrer == parent || !tree.IsReachable(referrer))
                {
                    continue;
                }

                if (looked == MaxReferrers)
                {
                    assignment.Limited++;
                    break;
                }

                pattern.Add(assignment.Signatures.Of(referrer));
                looked++;
            }

            pattern.Sort();
            var key = Distinct(pattern);
            if (!patternIds.TryGetValue(key, out var id))
            {
                id = assignment.Patterns.Count;
                patternIds[key] = id;
                assignment.Patterns.Add(key);
                assignment.Totals.Add(new GroupTotals { FirstSignature = own, Example = o });
                assignment.TypeCounts.Add(0);
            }

            assignment.GroupOfMember[i] = id;
            var totals = assignment.Totals[id];
            totals.Objects++;
            totals.Bytes += size;
            assignment.Totals[id] = totals;
            if (types.Add(((long)id << 32) | (uint)graph.TypeIndices[o]))
            {
                assignment.TypeCounts[id]++;
            }
        }

        return assignment;
    }

    private static int[] Distinct(List<int> sorted)
    {
        var count = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i == 0 || sorted[i] != sorted[i - 1])
            {
                count++;
            }
        }

        var result = new int[count];
        var next = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i == 0 || sorted[i] != sorted[i - 1])
            {
                result[next++] = sorted[i];
            }
        }

        return result;
    }

    private struct GroupTotals
    {
        public int Objects;
        public ulong Bytes;
        public int FirstSignature;
        public int Example;
    }

    private sealed class Assignment(PathSignatures signatures, int[] groupOfMember)
    {
        public PathSignatures Signatures { get; } = signatures;

        /// <summary>Each member's group number, -1 when no root reaches it.</summary>
        public int[] GroupOfMember { get; } = groupOfMember;

        public List<int[]> Patterns { get; } = [];

        public List<GroupTotals> Totals { get; } = [];

        public List<int> TypeCounts { get; } = [];

        public long Unreachable { get; set; }

        public ulong UnreachableBytes { get; set; }

        public long Limited { get; set; }

        /// <summary>Group numbers by rank: objects, then bytes, descending, then the first member ascending (decision 2).</summary>
        public int[] Ranked() =>
        [
            .. Enumerable.Range(0, Totals.Count)
                .OrderByDescending(id => Totals[id].Objects)
                .ThenByDescending(id => Totals[id].Bytes)
                .ThenBy(id => Totals[id].Example),
        ];
    }

    private sealed class PatternComparer : IEqualityComparer<int[]>
    {
        public static readonly PatternComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] obj)
        {
            var hash = default(HashCode);
            foreach (var value in obj)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
