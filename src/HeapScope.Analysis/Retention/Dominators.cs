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

    private Dominators(Column<int> immediateDominators, Column<ulong> retainedSizes, IDisposable? owner)
    {
        ImmediateDominators = immediateDominators;
        RetainedSizes = retainedSizes;
        _owner = owner;
    }

    /// <summary>Per object: the immediate dominator's index, <see cref="DominatorTree.VirtualRoot"/> or <see cref="DominatorTree.Unreachable"/>.</summary>
    public Column<int> ImmediateDominators { get; }

    /// <summary>Per object: the retained size in bytes (0 for an object no root reaches).</summary>
    public Column<ulong> RetainedSizes { get; }

    public int ObjectCount => ImmediateDominators.Length;

    /// <summary>Bytes held by the two columns.</summary>
    public long ColumnBytes => ImmediateDominators.ByteLength + RetainedSizes.ByteLength;

    /// <summary>Computes the dominators of <paramref name="graph"/> into managed arrays.</summary>
    public static Dominators Compute(HeapGraph graph, ReverseReferences reverse, IProgress<DominatorProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var idom = GC.AllocateUninitializedArray<int>(graph.ObjectCount);
        var retained = GC.AllocateUninitializedArray<ulong>(graph.ObjectCount);
        DominatorTree.Compute(graph, reverse, idom, retained, progress, cancellationToken);
        return new Dominators(Column<int>.FromArray(idom), Column<ulong>.FromArray(retained), owner: null);
    }

    /// <summary>
    /// Dominators from existing columns (a sidecar), checked against <paramref name="graph"/>'s object count; contents are
    /// guaranteed by the file's checksums. <paramref name="owner"/> is disposed with the result.
    /// </summary>
    public static Dominators FromColumns(HeapGraph graph, Column<int> immediateDominators, Column<ulong> retainedSizes, IDisposable? owner)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (immediateDominators.Length != graph.ObjectCount || retainedSizes.Length != graph.ObjectCount)
        {
            throw new ArgumentException("The dominators do not match the graph's object count.");
        }

        return new Dominators(immediateDominators, retainedSizes, owner);
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
