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
using System.Security.Cryptography;
using System.Text;
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
/// <param name="Key">The pattern's fingerprint (<see cref="SimilarRetention.KeyOf"/>): what a group set is resolved by.</param>
public sealed record RetentionGroup(long Objects, ulong Bytes, int Types, int FirstType, int PathCount, IReadOnlyList<KeyPath> Shown, int ExampleObject, string Key);

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
/// direct referrers that a root reaches without passing through the member, its own shortest path always among them,
/// plus its root when it is one. Members with the same pattern are retained the same way and form a group. A group is
/// named by its pattern's fingerprint (<see cref="RetentionGroup.Key"/>), so a group set never silently means another group.
/// </summary>
public static class SimilarRetention
{
    /// <summary>Rule version, logged with each answer (ADR-031): a change of the pattern rule bumps it.</summary>
    public const int Version = 1;

    /// <summary>Direct referrers looked at per member, its own shortest path's included (decision 1).</summary>
    public const int MaxReferrers = 16;

    /// <summary>
    /// Steps climbed from a referrer toward its root to see whether its path passes through the member (a back pointer,
    /// which retains nothing; the 3.2 review). Back pointers are nearly always a child or two below the member.
    /// </summary>
    public const int MaxBackPointerDepth = 256;

    /// <summary>Distinct patterns at most: a set with more is refused with a message to narrow it (the 3.2 review).</summary>
    public const int MaxPatterns = 1_000_000;

    /// <summary>Memory per member while grouping: its group number.</summary>
    public const int WorkingBytesPerMember = 4;

    /// <summary>Memory per distinct pattern at most: its signatures (up to 17), totals, type count and table entries.</summary>
    public const int WorkingBytesPerPattern = 200;

    /// <summary>Memory a grouping of <paramref name="members"/> in a graph of <paramref name="objects"/> may use, the signature memo included.</summary>
    public static long WorkingBytes(long members, long objects) =>
        (WorkingBytesPerMember * members) + ((long)WorkingBytesPerPattern * Math.Min(members, MaxPatterns)) + PathSignatures.WorkingBytes(objects);

    /// <summary>The groups of <paramref name="set"/>, the largest <paramref name="maxGroups"/> of them with their paths.</summary>
    /// <exception cref="ArgumentException">The set has more than <see cref="MaxPatterns"/> distinct patterns (too broad a set).</exception>
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
            groups.Add(new RetentionGroup(totals.Objects, totals.Bytes, assigned.TypeCounts[id], graph.TypeIndices[totals.Example], pattern.Length, paths, totals.Example, KeyOf(graph, assigned.Signatures, pattern)));
        }

        return new SimilarRetentionSet(groups, ranked.Length, assigned.Unreachable, assigned.UnreachableBytes, otherObjects, otherBytes, assigned.Limited);
    }

    /// <summary>
    /// The members of the group whose pattern has the fingerprint <paramref name="key"/>, ascending; empty when no group
    /// of this set has it. <paramref name="rank"/> is where it was listed, tried first.
    /// </summary>
    public static int[] MembersOf(HeapGraph graph, RootPathTree tree, ReverseReferences reverse, ObjectSet set, string key, int rank = -1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var assigned = Assign(graph, tree, reverse, set, cancellationToken);
        var ranked = assigned.Ranked();
        var id = -1;
        if (rank >= 0 && rank < ranked.Length && KeyOf(graph, assigned.Signatures, assigned.Patterns[ranked[rank]]) == key)
        {
            id = ranked[rank];
        }
        else
        {
            for (var g = 0; g < assigned.Patterns.Count && id < 0; g++)
            {
                if (KeyOf(graph, assigned.Signatures, assigned.Patterns[g]) == key)
                {
                    id = g;
                }
            }
        }

        if (id < 0)
        {
            return [];
        }

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

    /// <summary>
    /// A pattern's fingerprint: the first 8 bytes, in hexadecimal, of the SHA-256 of its paths as text (each path's root
    /// group key and step type names with their module files), sorted. The same in every snapshot where the pattern's roots and types are the same.
    /// </summary>
    private static string KeyOf(HeapGraph graph, PathSignatures signatures, int[] pattern)
    {
        // Types by name and module file (ADR-018's key), so same-named types of two modules make different patterns.
        var texts = pattern.Select(s => signatures.RootKeyOf(s) + "|" + string.Join(">", signatures.Steps(s).Select(t => graph.Types.GetName(t) + "@" + TypeGroups.ModuleKey(graph.Types.GetModule(t))))).Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", texts))), 0, 8).ToLower(CultureInfo.InvariantCulture);
    }

    private static Assignment Assign(HeapGraph graph, RootPathTree tree, ReverseReferences reverse, ObjectSet set, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(reverse);
        ArgumentNullException.ThrowIfNull(set);
        var assignment = new Assignment(new PathSignatures(graph, tree), new int[set.Members.Length]);
        var patternIds = new Dictionary<int[], int>(PatternComparer.Instance);
        var lookup = patternIds.GetAlternateLookup<ReadOnlySpan<int>>();
        var types = new HashSet<long>();
        var pattern = new int[MaxReferrers + 1];
        long checkedReferrers = 0;
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

            var count = 0;
            pattern[count++] = own;
            var parent = tree.Parent(o);
            var referrers = reverse.GetReferrers(o);
            for (var r = 0; r < referrers.Length; r++)
            {
                if ((++checkedReferrers & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var referrer = referrers[r];
                if ((r > 0 && referrers[r - 1] == referrer) || referrer == o || referrer == parent || !tree.IsReachable(referrer) || PassesThrough(tree, referrer, o))
                {
                    continue;
                }

                if (count == MaxReferrers)
                {
                    assignment.Limited++;
                    break;
                }

                pattern[count++] = assignment.Signatures.Of(referrer);
            }

            // Sorted and deduplicated in place; looked up as a span, so only a new pattern allocates (the 3.2 review).
            var span = pattern.AsSpan(0, count);
            span.Sort();
            var distinct = 1;
            for (var k = 1; k < span.Length; k++)
            {
                if (span[k] != span[distinct - 1])
                {
                    span[distinct++] = span[k];
                }
            }

            span = span[..distinct];
            if (!lookup.TryGetValue(span, out var id))
            {
                if (assignment.Patterns.Count == MaxPatterns)
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The set's objects are retained in more than {MaxPatterns:N0} different ways. Open a smaller set (one type, or a group of a larger set) and group it again."));
                }

                var key = span.ToArray();
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

    /// <summary>
    /// Whether the shortest path to <paramref name="referrer"/> runs through <paramref name="member"/> within
    /// <see cref="MaxBackPointerDepth"/> steps: then the referrer is held by the member and retains nothing of it.
    /// </summary>
    private static bool PassesThrough(RootPathTree tree, int referrer, int member)
    {
        var current = tree.Parent(referrer);
        for (var step = 0; step < MaxBackPointerDepth && current >= 0; step++)
        {
            if (current == member)
            {
                return true;
            }

            current = tree.Parent(current);
        }

        return false;
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
        public int[] Ranked()
        {
            var ranked = new int[Totals.Count];
            for (var i = 0; i < ranked.Length; i++)
            {
                ranked[i] = i;
            }

            var totals = Totals;
            Array.Sort(ranked, (a, b) =>
            {
                var byObjects = totals[b].Objects.CompareTo(totals[a].Objects);
                if (byObjects != 0)
                {
                    return byObjects;
                }

                var byBytes = totals[b].Bytes.CompareTo(totals[a].Bytes);
                return byBytes != 0 ? byBytes : totals[a].Example.CompareTo(totals[b].Example);
            });
            return ranked;
        }
    }

    private sealed class PatternComparer : IEqualityComparer<int[]>, IAlternateEqualityComparer<ReadOnlySpan<int>, int[]>
    {
        public static readonly PatternComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] obj) => GetHashCode((ReadOnlySpan<int>)obj);

        public bool Equals(ReadOnlySpan<int> alternate, int[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<int> alternate)
        {
            var hash = default(HashCode);
            foreach (var value in alternate)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }

        public int[] Create(ReadOnlySpan<int> alternate) => alternate.ToArray();
    }
}
