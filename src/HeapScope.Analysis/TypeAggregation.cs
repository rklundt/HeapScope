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
/// Per-type totals of a graph (sprint 1.3): object count, shallow bytes, and bytes and counts per heap kind. Computed once
/// per snapshot in one parallel pass over the objects; every grouping, filter and sort works on these small arrays.
/// </summary>
public sealed class TypeAggregation
{
    /// <summary>Heap kinds indexed by their numeric value (Unknown = 0 to Frozen = 6).</summary>
    public const int HeapKindSlots = 7;

    private const int PartitionSize = 1 << 20;

    // Each parallel worker has its own arrays (72 bytes per type); their total stays under this.
    private const long LocalArraysBudgetBytes = 256L * 1024 * 1024;

    private TypeAggregation(int typeCount)
    {
        Counts = new long[typeCount];
        Bytes = new ulong[typeCount];
        BytesByKind = new ulong[typeCount * HeapKindSlots];
        KindObjects = new long[HeapKindSlots];
        KindBytes = new ulong[HeapKindSlots];
    }

    public int TypeCount => Counts.Length;

    /// <summary>Objects per type index.</summary>
    public long[] Counts { get; }

    /// <summary>Shallow bytes per type index.</summary>
    public ulong[] Bytes { get; }

    /// <summary>Shallow bytes per type and heap kind: index <c>type * HeapKindSlots + kind</c>.</summary>
    public ulong[] BytesByKind { get; }

    /// <summary>Objects per heap kind over the whole graph.</summary>
    public long[] KindObjects { get; }

    /// <summary>Bytes per heap kind over the whole graph.</summary>
    public ulong[] KindBytes { get; }

    public long TotalObjects { get; private set; }

    public ulong TotalBytes { get; private set; }

    /// <summary>Bytes of type <paramref name="typeIndex"/> in heap kind <paramref name="kind"/>.</summary>
    public ulong BytesIn(int typeIndex, HeapKind kind) => BytesByKind[(typeIndex * HeapKindSlots) + (int)kind];

    /// <summary>
    /// Aggregates <paramref name="graph"/>. Partitions of 1M objects run in parallel with per-thread arrays merged at the
    /// end, so the pass allocates only a few arrays per thread; the degree of parallelism is capped so those arrays stay
    /// within 256 MB however many types the graph has. Reports progress in objects done.
    /// </summary>
    public static TypeAggregation Compute(HeapGraph graph, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var typeCount = graph.Types.Count;
        var result = new TypeAggregation(typeCount);
        var objectCount = graph.ObjectCount;
        var partitions = (int)(((long)objectCount + PartitionSize - 1) / PartitionSize);
        var perWorker = ((long)typeCount * ((2 * sizeof(long)) + (HeapKindSlots * sizeof(ulong)))) + 1;
        var degree = (int)Math.Clamp(LocalArraysBudgetBytes / perWorker, 1, Environment.ProcessorCount);
        var gate = new object();
        long done = 0;

        Parallel.For(
            0,
            partitions,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = degree },
            () => new TypeAggregation(typeCount),
            (partition, _, local) =>
            {
                var start = (int)((long)partition * PartitionSize);
                var end = (int)Math.Min(objectCount, (long)start + PartitionSize);
                var types = graph.TypeIndices.Span;
                var sizes = graph.Sizes.Span;
                var kinds = graph.HeapKinds.Span;
                for (var i = start; i < end; i++)
                {
                    if ((i & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var type = types[i];
                    var size = sizes[i] == HeapGraph.LargeSizeMarker ? graph.GetSize(i) : sizes[i];
                    var kind = (int)kinds[i];
                    local.Counts[type]++;
                    local.Bytes[type] += size;
                    local.BytesByKind[(type * HeapKindSlots) + kind] += size;
                    local.KindObjects[kind]++;
                    local.KindBytes[kind] += size;
                }

                progress?.Report(Interlocked.Add(ref done, end - start));
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    result.Merge(local);
                }
            });

        result.TotalObjects = objectCount;
        foreach (var bytes in result.Bytes)
        {
            result.TotalBytes += bytes;
        }

        return result;
    }

    /// <summary>
    /// Aggregates the members of <paramref name="set"/> only (sprint 3.1): a set's Types and Generations views. One pass over
    /// the members; the totals are the set's.
    /// </summary>
    public static TypeAggregation Compute(HeapGraph graph, ObjectSet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(set);
        var result = new TypeAggregation(graph.Types.Count);
        var types = graph.TypeIndices.Span;
        var kinds = graph.HeapKinds.Span;
        var members = set.Members;
        for (var m = 0; m < members.Length; m++)
        {
            if ((m & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var i = members[m];
            var type = types[i];
            var size = graph.GetSize(i);
            var kind = (int)kinds[i];
            result.Counts[type]++;
            result.Bytes[type] += size;
            result.BytesByKind[(type * HeapKindSlots) + kind] += size;
            result.KindObjects[kind]++;
            result.KindBytes[kind] += size;
            result.TotalBytes += size;
        }

        result.TotalObjects = members.Length;
        return result;
    }

    private void Merge(TypeAggregation other)
    {
        for (var t = 0; t < Counts.Length; t++)
        {
            Counts[t] += other.Counts[t];
            Bytes[t] += other.Bytes[t];
        }

        for (var i = 0; i < BytesByKind.Length; i++)
        {
            BytesByKind[i] += other.BytesByKind[i];
        }

        for (var k = 0; k < HeapKindSlots; k++)
        {
            KindObjects[k] += other.KindObjects[k];
            KindBytes[k] += other.KindBytes[k];
        }
    }
}
