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
/// What a set of objects retains together, exactly (sprint 2.4; 2.3 decision 7): the objects that are reachable now and
/// would not be without the set, its own members included. One reachability pass from the roots
/// (<see cref="PathRoots.Sources"/>, as the dominators use) that never enters the set; whatever the dominators reach and
/// the pass does not is retained. A group's retained column is only a lower bound (ADR-024) because objects held by
/// several members together belong to no single member; this counts them.
/// </summary>
public static class SetRetention
{
    /// <summary>Bytes per object of working memory: a bit per object, and the search stack (an int per object at worst).</summary>
    public const int WorkingBytesPerObject = 5;

    private const int Chunk = 1 << 16;

    /// <summary>
    /// The objects the set (every object whose type is marked in <paramref name="typeInSet"/>) retains, by type.
    /// <paramref name="dominators"/> says what is reachable now.
    /// </summary>
    public static SetRetentionResult Compute(
        HeapGraph graph,
        Dominators dominators,
        bool[] typeInSet,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dominators);
        ArgumentNullException.ThrowIfNull(typeInSet);
        if (typeInSet.Length != graph.Types.Count || dominators.ObjectCount != graph.ObjectCount)
        {
            throw new ArgumentException("The set or the dominators do not match the graph.");
        }

        var n = graph.ObjectCount;
        var types = graph.TypeIndices.Span;
        var starts = graph.EdgeStarts.Span;
        var edges = graph.Edges.Span;
        var visited = new ulong[(n + 63) / 64];
        var stack = new List<int>();
        long done = 0;

        foreach (var source in PathRoots.Sources(graph))
        {
            if (!typeInSet[types[source]] && Mark(visited, source))
            {
                stack.Add(source);
            }
        }

        while (stack.Count > 0)
        {
            var current = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            for (var e = starts[current]; e < starts[current + 1]; e++)
            {
                var target = edges[e];
                if (!typeInSet[types[target]] && Mark(visited, target))
                {
                    stack.Add(target);
                }
            }

            if ((++done & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(done);
            }
        }

        var objectsByType = new long[graph.Types.Count];
        var bytesByType = new ulong[graph.Types.Count];
        long setObjects = 0;
        ulong setBytes = 0;
        long retainedObjects = 0;
        ulong retainedBytes = 0;
        for (var i = 0; i < n; i++)
        {
            if (!dominators.IsReachable(i))
            {
                continue;
            }

            var size = graph.GetSize(i);
            if (typeInSet[types[i]])
            {
                setObjects++;
                setBytes += size;
            }

            if ((visited[i >> 6] & (1UL << (i & 63))) == 0)
            {
                retainedObjects++;
                retainedBytes += size;
                objectsByType[types[i]]++;
                bytesByType[types[i]] += size;
            }

            if ((i & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        progress?.Report(done);
        return new SetRetentionResult(setObjects, setBytes, retainedObjects, retainedBytes, objectsByType, bytesByType);
    }

    private static bool Mark(ulong[] visited, int index)
    {
        ref var word = ref visited[index >> 6];
        var bit = 1UL << (index & 63);
        if ((word & bit) != 0)
        {
            return false;
        }

        word |= bit;
        return true;
    }
}

/// <summary>
/// What a set retains (sprint 2.4): the set's reachable members and their bytes, the objects it retains (members included)
/// and their bytes, and the retained objects and bytes per type table entry.
/// </summary>
public sealed record SetRetentionResult(long SetObjects, ulong SetBytes, long RetainedObjects, ulong RetainedBytes, long[] ObjectsByType, ulong[] BytesByType);
