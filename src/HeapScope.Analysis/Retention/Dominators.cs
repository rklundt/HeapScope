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
/// A snapshot's computed dominators (sprint 2.3): each object's immediate dominator and retained size
/// (<see cref="DominatorTree"/>). Like the reverse references, the columns are managed arrays or views of a sidecar file;
/// <see cref="Dispose"/> releases what owns them.
/// </summary>
public sealed class Dominators : IDisposable
{
    private readonly IDisposable? _owner;
    private int _disposed;

    private Dominators(Column<int> immediateDominators, Column<ulong> retainedSizes, Column<int> childStarts, Column<int> children, IDisposable? owner)
    {
        ImmediateDominators = immediateDominators;
        RetainedSizes = retainedSizes;
        ChildStarts = childStarts;
        Children = children;
        _owner = owner;
    }

    /// <summary>Per object: the immediate dominator's index, <see cref="DominatorTree.VirtualRoot"/> or <see cref="DominatorTree.Unreachable"/>.</summary>
    public Column<int> ImmediateDominators { get; }

    /// <summary>Per object: the retained size in bytes (0 for an object no root reaches).</summary>
    public Column<ulong> RetainedSizes { get; }

    /// <summary>The tree's row offsets (sprint 2.4): n + 2 elements, vertex n is the virtual root (<see cref="DominatorTree.BuildChildren"/>).</summary>
    public Column<int> ChildStarts { get; }

    /// <summary>The tree's children, each vertex's slice largest retained size first.</summary>
    public Column<int> Children { get; }

    public int ObjectCount => ImmediateDominators.Length;

    /// <summary>The vertex of the virtual root, whose children are the top-level dominators.</summary>
    public int Root => ObjectCount;

    /// <summary>Bytes held by the columns.</summary>
    public long ColumnBytes => ImmediateDominators.ByteLength + RetainedSizes.ByteLength + ChildStarts.ByteLength + Children.ByteLength;

    /// <summary>Computes the dominators of <paramref name="graph"/> into managed arrays.</summary>
    public static Dominators Compute(HeapGraph graph, ReverseReferences reverse, IProgress<DominatorProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var idom = GC.AllocateUninitializedArray<int>(graph.ObjectCount);
        var retained = GC.AllocateUninitializedArray<ulong>(graph.ObjectCount);
        DominatorTree.Compute(graph, reverse, idom, retained, progress, cancellationToken);
        var starts = new int[graph.ObjectCount + 2];
        var children = new int[graph.ObjectCount];
        DominatorTree.BuildChildren(idom, retained, starts, children, cancellationToken);
        return new Dominators(Column<int>.FromArray(idom), Column<ulong>.FromArray(retained), Column<int>.FromArray(starts), Column<int>.FromArray(children), owner: null);
    }

    /// <summary>
    /// Dominators from existing columns (a sidecar), checked against <paramref name="graph"/>'s object count; contents are
    /// guaranteed by the file's checksums. <paramref name="owner"/> is disposed with the result.
    /// </summary>
    public static Dominators FromColumns(HeapGraph graph, Column<int> immediateDominators, Column<ulong> retainedSizes, Column<int> childStarts, Column<int> children, IDisposable? owner)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (immediateDominators.Length != graph.ObjectCount || retainedSizes.Length != graph.ObjectCount
            || childStarts.Length != graph.ObjectCount + 2 || children.Length != graph.ObjectCount)
        {
            throw new ArgumentException("The dominators do not match the graph's object count.");
        }

        return new Dominators(immediateDominators, retainedSizes, childStarts, children, owner);
    }

    /// <summary>
    /// The children of <paramref name="vertex"/> in the dominator tree (an object index, or <see cref="Root"/> for the
    /// top-level dominators), largest retained size first. Sprint 2.4.
    /// </summary>
    public ReadOnlySpan<int> ChildrenOf(int vertex)
    {
        var starts = ChildStarts.Span;
        return Children.Span[starts[vertex]..starts[vertex + 1]];
    }

    /// <summary>How many children <paramref name="vertex"/> has.</summary>
    public int ChildCount(int vertex)
    {
        var starts = ChildStarts.Span;
        return starts[vertex + 1] - starts[vertex];
    }

    /// <summary>The immediate dominator of <paramref name="objectIndex"/> (an object index, or a marker of <see cref="DominatorTree"/>).</summary>
    public int ImmediateDominator(int objectIndex) => ImmediateDominators[objectIndex];

    /// <summary>The retained size of <paramref name="objectIndex"/> in bytes.</summary>
    public ulong RetainedSize(int objectIndex) => RetainedSizes[objectIndex];

    public bool IsReachable(int objectIndex) => ImmediateDominators[objectIndex] != DominatorTree.Unreachable;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner?.Dispose();
        }
    }
}
