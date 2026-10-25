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

namespace HeapScope.Core.Graph;

/// <summary>
/// Incoming references of a <see cref="HeapGraph"/> (sprint 2.1, ARCHITECTURE.md 6.4): a second CSR whose row for an
/// object lists the objects that reference it, in ascending index order, one entry per reference (an object holding
/// another in two fields appears twice). Costs 4 bytes per object and 4 per reference. Like the graph's columns, the
/// columns are managed arrays or views of an index file; <see cref="Dispose"/> releases what owns them.
/// </summary>
public sealed class ReverseReferences : IDisposable
{
    /// <summary>Sources per cancellation check and progress report while building.</summary>
    private const int Chunk = 1 << 20;

    private readonly IDisposable? _owner;
    private int _disposed;

    private ReverseReferences(Column<int> inEdgeStarts, Column<int> inEdges, IDisposable? owner)
    {
        InEdgeStarts = inEdgeStarts;
        InEdges = inEdges;
        _owner = owner;
    }

    /// <summary>CSR row offsets: the referrers of object <c>i</c> are <c>InEdges[InEdgeStarts[i]..InEdgeStarts[i + 1]]</c>.</summary>
    public Column<int> InEdgeStarts { get; }

    /// <summary>Referrer object index per reference.</summary>
    public Column<int> InEdges { get; }

    public int ObjectCount => Math.Max(0, InEdgeStarts.Length - 1);

    public int EdgeCount => InEdges.Length;

    /// <summary>Bytes held by the two columns.</summary>
    public long ColumnBytes => InEdgeStarts.ByteLength + InEdges.ByteLength;

    /// <summary>
    /// Builds the reverse CSR of <paramref name="graph"/> into managed arrays (<see cref="Fill"/>). Progress reports sources
    /// processed in the fill pass.
    /// </summary>
    public static ReverseReferences Build(HeapGraph graph, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var starts = new int[graph.ObjectCount + 1];
        var inEdges = GC.AllocateUninitializedArray<int>(graph.EdgeCount);
        Fill(graph, starts, inEdges, progress, cancellationToken);
        return new ReverseReferences(Column<int>.FromArray(starts), Column<int>.FromArray(inEdges), owner: null);
    }

    /// <summary>
    /// Writes the reverse CSR of <paramref name="graph"/> into <paramref name="inEdgeStarts"/> (object count plus one) and
    /// <paramref name="inEdges"/> (edge count), for example sections of a mapped sidecar being written (sprint 2.3), in two
    /// passes over its edges: count each object's referrers, then fill them (sources in ascending order, so each row comes
    /// out sorted). No memory beyond the two spans is used. Progress reports sources processed in the fill pass.
    /// </summary>
    public static void Fill(HeapGraph graph, Span<int> inEdgeStarts, Span<int> inEdges, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var objects = graph.ObjectCount;
        var edges = graph.Edges.Span;
        if (inEdgeStarts.Length != objects + 1 || inEdges.Length != edges.Length)
        {
            throw new ArgumentException("The spans do not match the graph's object and reference counts.");
        }

        var starts = inEdgeStarts;
        starts.Clear();

        // starts[t + 1] counts t's referrers; the prefix sum turns starts[t] into the first slot of t's row.
        for (var i = 0; i < edges.Length; i++)
        {
            starts[edges[i] + 1]++;
            if ((i & (Chunk - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        for (var i = 1; i <= objects; i++)
        {
            starts[i] += starts[i - 1];
        }

        // Fill with starts[t] as t's cursor: afterwards starts[t] is the end of t's row, which is the start of t + 1, so
        // shifting the array right by one restores the row offsets without a second array.
        var edgeStarts = graph.EdgeStarts.Span;
        for (var source = 0; source < objects; source++)
        {
            foreach (var target in edges[edgeStarts[source]..edgeStarts[source + 1]])
            {
                inEdges[starts[target]++] = source;
            }

            if ((source & (Chunk - 1)) == Chunk - 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(source + 1L);
            }
        }

        for (var i = objects; i > 0; i--)
        {
            starts[i] = starts[i - 1];
        }

        starts[0] = 0;
        progress?.Report(objects);
    }

    /// <summary>
    /// Reverse references from existing columns (an index file), checked against <paramref name="graph"/>'s shape:
    /// one row per object, one entry per reference, offsets from 0 to the edge count. Contents are guaranteed by the
    /// file's checksums. <paramref name="owner"/> is disposed with the result.
    /// </summary>
    public static ReverseReferences FromColumns(HeapGraph graph, Column<int> inEdgeStarts, Column<int> inEdges, IDisposable? owner)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (inEdgeStarts.Length != graph.ObjectCount + 1 || inEdges.Length != graph.EdgeCount)
        {
            throw new ArgumentException("The reverse references do not match the graph's object and reference counts.");
        }

        if (inEdgeStarts.Span[0] != 0 || inEdgeStarts.Span[graph.ObjectCount] != inEdges.Length)
        {
            throw new ArgumentException("InEdgeStarts must start at 0 and end at the reference count.");
        }

        return new ReverseReferences(inEdgeStarts, inEdges, owner);
    }

    /// <summary>The objects referencing <paramref name="objectIndex"/>, ascending, one entry per reference.</summary>
    public ReadOnlySpan<int> GetReferrers(int objectIndex)
    {
        var starts = InEdgeStarts.Span;
        return InEdges.Span[starts[objectIndex]..starts[objectIndex + 1]];
    }

    /// <summary>Number of references to <paramref name="objectIndex"/>.</summary>
    public int GetReferenceCount(int objectIndex) => InEdgeStarts[objectIndex + 1] - InEdgeStarts[objectIndex];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner?.Dispose();
        }
    }
}
