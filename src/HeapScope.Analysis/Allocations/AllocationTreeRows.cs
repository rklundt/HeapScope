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

using System.Buffers;
using System.Globalization;
using HeapScope.Core.Allocations;

namespace HeapScope.Analysis.Allocations;

/// <summary>How a call stack tree is read.</summary>
public enum AllocationTreeKind
{
    /// <summary>Top-down: the outermost frames first, then what they call; Own is what a frame allocated itself.</summary>
    CallTree = 0,

    /// <summary>Bottom-up: the allocating frames first, then their callers.</summary>
    BackTraces = 1,
}

/// <summary>What a row of an allocation tree stands for.</summary>
public enum AllocationRowKind
{
    Frame = 0,

    /// <summary>The rest of a list past the rows shown; expanding it shows the next batch.</summary>
    More = 1,
}

/// <summary>
/// A row of a flattened allocation tree (sprint 2.5): its key and its parent's, its depth and kind, the frame id (or -1),
/// its name, the sampled bytes of the stacks through it (Total), those allocated in the frame itself (Own; -1 where it
/// has no meaning, below the first level of back traces), the samples, how many children it has, and whether it is open.
/// </summary>
public readonly record struct AllocationRow(
    string Key,
    string ParentKey,
    int Depth,
    AllocationRowKind Kind,
    int Frame,
    string Name,
    long Total,
    long Own,
    int Samples,
    int Children,
    bool Expanded);

/// <summary>The rows of one answer, whether the row limit cut them, and the totals of the samples asked about.</summary>
public sealed record AllocationTreeView(IReadOnlyList<AllocationRow> Rows, bool Truncated, long TotalBytes, int TotalSamples);

/// <summary>
/// The call tree and back traces of an interval, flattened from the open keys like the dominator tree (ADR-027): each
/// list's children by Total, a page at a time with a "more" row for the rest, and limits on rows and depth.
/// </summary>
public static class AllocationTreeRows
{
    /// <summary>The most rows one answer holds.</summary>
    public const int MaxRows = 50_000;

    /// <summary>The deepest level that opens.</summary>
    public const int MaxDepth = 1_000;

    /// <summary>The key the top level's rows have as their parent.</summary>
    public const string RootKey = "root";

    /// <summary>A call tree row's key: its stack node.</summary>
    public static string NodeKey(int node) => "n:" + node.ToString(CultureInfo.InvariantCulture);

    /// <summary>A back-trace row's key: its parent's key and its frame.</summary>
    public static string BackTraceKey(string parentKey, int frame) =>
        (parentKey == RootKey ? "b:" : parentKey + "/") + frame.ToString(CultureInfo.InvariantCulture);

    /// <summary>The key of a list's "more" row after <paramref name="shown"/> children.</summary>
    public static string MoreKey(string listKey, int shown) => listKey + "|m:" + shown.ToString(CultureInfo.InvariantCulture);

    public static AllocationTreeView Build(
        AllocationTrace trace,
        AllocationFilter filter,
        AllocationTreeKind kind,
        IReadOnlySet<string> expanded,
        int maxChildren,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(expanded);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChildren, 1);

        // Working arrays sized by the stack nodes come from the pool: every expansion is a query (the 2.5 review).
        var count = trace.StackFrames.Length;
        var own = ArrayPool<long>.Shared.Rent(Math.Max(count, 1));
        var ownSamples = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        try
        {
            Array.Clear(own, 0, count);
            Array.Clear(ownSamples, 0, count);
            AllocationProfile.Own(trace, filter, own.AsSpan(0, count), ownSamples.AsSpan(0, count), cancellationToken);
            long totalBytes = 0;
            var totalSamples = 0;
            for (var n = 0; n < count; n++)
            {
                totalBytes += own[n];
                totalSamples += ownSamples[n];
            }

            var builder = new Builder(trace, expanded, maxChildren, cancellationToken);
            if (kind == AllocationTreeKind.CallTree)
            {
                builder.CallTree(own, ownSamples, count);
            }
            else
            {
                builder.BackTraces(own, ownSamples, count);
            }

            return new AllocationTreeView(builder.Rows, builder.Truncated, totalBytes, totalSamples);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(own);
            ArrayPool<int>.Shared.Return(ownSamples);
        }
    }

    private sealed class Builder(AllocationTrace trace, IReadOnlySet<string> expanded, int maxChildren, CancellationToken cancellationToken)
    {
        public List<AllocationRow> Rows { get; } = [];

        public bool Truncated { get; private set; }

        public void CallTree(long[] own, int[] ownSamples, int count)
        {
            var total = ArrayPool<long>.Shared.Rent(Math.Max(count, 1));
            var samples = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
            var starts = ArrayPool<int>.Shared.Rent(count + 2);
            var fill = ArrayPool<int>.Shared.Rent(count + 2);
            var children = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
            try
            {
                // A parent comes before its children (AllocationTrace checks it), so one pass from the end sums each subtree.
                Array.Copy(own, total, count);
                Array.Copy(ownSamples, samples, count);
                for (var n = count - 1; n >= 0; n--)
                {
                    var parent = trace.StackParents[n];
                    if (parent >= 0)
                    {
                        total[parent] += total[n];
                        samples[parent] += samples[n];
                    }
                }

                // Children lists of the nodes the interval reached: node p's in starts[p + 1] to starts[p + 2].
                Array.Clear(starts, 0, count + 2);
                for (var n = 0; n < count; n++)
                {
                    if (samples[n] > 0)
                    {
                        starts[trace.StackParents[n] + 2]++;
                    }
                }

                for (var i = 1; i < count + 2; i++)
                {
                    starts[i] += starts[i - 1];
                }

                Array.Copy(starts, fill, count + 2);
                for (var n = 0; n < count; n++)
                {
                    if (samples[n] > 0)
                    {
                        children[fill[trace.StackParents[n] + 1]++] = n;
                    }
                }

                List(RootKey, -1, 0);
            }
            finally
            {
                ArrayPool<long>.Shared.Return(total);
                ArrayPool<int>.Shared.Return(samples);
                ArrayPool<int>.Shared.Return(starts);
                ArrayPool<int>.Shared.Return(fill);
                ArrayPool<int>.Shared.Return(children);
            }

            int ChildCount(int node) => starts[node + 2] - starts[node + 1];

            void List(string listKey, int node, int depth)
            {
                var ordered = children.AsSpan(starts[node + 1], ChildCount(node)).ToArray();
                Array.Sort(ordered, (a, b) =>
                    total[a] != total[b] ? total[b].CompareTo(total[a])
                    : string.CompareOrdinal(trace.Frames[trace.StackFrames[a]], trace.Frames[trace.StackFrames[b]]) is var byName and not 0 ? byName
                    : a.CompareTo(b));
                var shown = Math.Min(ordered.Length, Batches(listKey) * maxChildren);
                for (var i = 0; i < shown && !Truncated; i++)
                {
                    var child = ordered[i];
                    var key = NodeKey(child);
                    var childCount = ChildCount(child);
                    var isExpanded = depth < MaxDepth && childCount > 0 && expanded.Contains(key);
                    var frame = trace.StackFrames[child];
                    if (Add(new AllocationRow(key, listKey, depth, AllocationRowKind.Frame, frame, trace.Frames[frame], total[child], own[child], samples[child], childCount, isExpanded)) && isExpanded)
                    {
                        List(key, child, depth + 1);
                    }
                }

                long moreBytes = 0;
                var moreSamples = 0;
                for (var i = shown; i < ordered.Length; i++)
                {
                    moreBytes += total[ordered[i]];
                    moreSamples += samples[ordered[i]];
                }

                More(listKey, depth, ordered.Length, shown, moreBytes, moreSamples);
            }
        }

        public void BackTraces(long[] own, int[] ownSamples, int count)
        {
            // A back-trace row stands for positions in the stack tree: at the first level the nodes that allocated, one
            // level down their callers, and so on; each carries the bytes of the samples that allocated below it.
            var leaves = new List<Position>();
            for (var n = 0; n < count; n++)
            {
                if (ownSamples[n] > 0)
                {
                    leaves.Add(new Position(n, own[n], ownSamples[n]));
                }
            }

            List(RootKey, Group(leaves, p => p.Node), 0);
        }

        private void List(string listKey, List<Group> groups, int depth)
        {
            var ordered = groups
                .OrderByDescending(g => g.Bytes).ThenBy(g => trace.Frames[g.Frame], StringComparer.Ordinal).ThenBy(g => g.Frame)
                .ToArray();
            var shown = Math.Min(ordered.Length, Batches(listKey) * maxChildren);
            for (var i = 0; i < shown && !Truncated; i++)
            {
                var group = ordered[i];
                var key = BackTraceKey(listKey, group.Frame);
                var callers = Callers(group);
                var isExpanded = depth < MaxDepth && callers.Count > 0 && expanded.Contains(key);
                if (Add(new AllocationRow(key, listKey, depth, AllocationRowKind.Frame, group.Frame, trace.Frames[group.Frame], group.Bytes, depth == 0 ? group.Bytes : -1, group.Samples, callers.Count, isExpanded)) && isExpanded)
                {
                    List(key, callers, depth + 1);
                }
            }

            long moreBytes = 0;
            var moreSamples = 0;
            for (var i = shown; i < ordered.Length; i++)
            {
                moreBytes += ordered[i].Bytes;
                moreSamples += ordered[i].Samples;
            }

            More(listKey, depth, ordered.Length, shown, moreBytes, moreSamples);
        }

        // The callers of a back-trace row, grouped by frame (stacks that start at this row's frame end here).
        private List<Group> Callers(Group group)
        {
            var callers = group.Positions.Where(p => trace.StackParents[p.Node] >= 0).ToList();
            return Group(callers, p => trace.StackParents[p.Node]);
        }

        private List<Group> Group(List<Position> positions, Func<Position, int> next)
        {
            var groups = new Dictionary<int, Group>();
            var nodes = new Dictionary<int, int>();
            foreach (var position in positions)
            {
                var node = next(position);
                var frame = trace.StackFrames[node];
                if (!groups.TryGetValue(frame, out var group))
                {
                    group = new Group(frame);
                    groups.Add(frame, group);
                }

                // Positions of one node merge, so a group never holds more positions than the tree has nodes.
                if (nodes.TryGetValue(node, out var at) && group.Positions[at].Node == node)
                {
                    var merged = group.Positions[at];
                    group.Positions[at] = merged with { Bytes = merged.Bytes + position.Bytes, Samples = merged.Samples + position.Samples };
                }
                else
                {
                    nodes[node] = group.Positions.Count;
                    group.Positions.Add(new Position(node, position.Bytes, position.Samples));
                }

                group.Bytes += position.Bytes;
                group.Samples += position.Samples;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return [.. groups.Values];
        }

        private void More(string listKey, int depth, int count, int shown, long bytes, int samples)
        {
            if (count <= shown || Truncated)
            {
                return;
            }

            var remaining = count - shown;
            Add(new AllocationRow(
                MoreKey(listKey, shown),
                listKey,
                depth,
                AllocationRowKind.More,
                -1,
                string.Create(CultureInfo.InvariantCulture, $"{remaining:N0} more frames"),
                bytes,
                -1,
                samples,
                Math.Min(remaining, maxChildren),
                false));
        }

        // One batch, plus one more for each of the list's "more" rows that is expanded (never past the row limit).
        private int Batches(string listKey)
        {
            var batches = 1;
            while ((long)batches * maxChildren < MaxRows && expanded.Contains(MoreKey(listKey, batches * maxChildren)))
            {
                batches++;
            }

            return batches;
        }

        private bool Add(AllocationRow row)
        {
            if (Rows.Count >= MaxRows)
            {
                Truncated = true;
                return false;
            }

            Rows.Add(row);
            if ((Rows.Count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return true;
        }
    }

    private readonly record struct Position(int Node, long Bytes, int Samples);

    private sealed class Group(int frame)
    {
        public int Frame { get; } = frame;

        public long Bytes { get; set; }

        public int Samples { get; set; }

        public List<Position> Positions { get; } = [];
    }
}
