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

/// <summary>The per-object columns of a <see cref="HeapGraph"/>.</summary>
/// <param name="Addresses">Object addresses, strictly ascending.</param>
/// <param name="TypeIndices">Type table index per object.</param>
/// <param name="Sizes">Shallow size per object; <see cref="uint.MaxValue"/> means "see <paramref name="LargeSizes"/>".</param>
/// <param name="HeapKinds">Heap kind per object.</param>
/// <param name="Flags">Flags per object.</param>
/// <param name="EdgeStarts">CSR row offsets, length object count + 1.</param>
/// <param name="Edges">Target object index per reference.</param>
/// <param name="LargeSizes">Sizes of objects of 4 GiB or more, sorted by object index.</param>
public readonly record struct HeapGraphColumns(
    Column<ulong> Addresses,
    Column<int> TypeIndices,
    Column<uint> Sizes,
    Column<HeapKind> HeapKinds,
    Column<ObjectFlags> Flags,
    Column<int> EdgeStarts,
    Column<int> Edges,
    Column<LargeObjectSize> LargeSizes);

/// <summary>
/// The compact object graph: struct-of-arrays columns indexed by object index, CSR adjacency, interned types,
/// roots and strings (ADR-005, ARCHITECTURE.md 6). Never one managed object per heap object.
/// </summary>
public sealed class HeapGraph : IDisposable
{
    /// <summary>Marker in <see cref="Sizes"/> for objects whose size is in <see cref="LargeSizes"/>.</summary>
    public const uint LargeSizeMarker = uint.MaxValue;

    private readonly IDisposable? _owner;
    private readonly ulong _totalSize;
    private readonly Lock _attachedLock = new();
    private List<IDisposable>? _attached;
    private int _disposed;

    private HeapGraph(
        HeapGraphColumns columns,
        TypeTable types,
        RootTable roots,
        StringTable strings,
        long droppedReferenceCount,
        long droppedRootCount,
        IDisposable? owner,
        Column<DependentHandle> dependentHandles)
    {
        DependentHandles = dependentHandles;
        Addresses = columns.Addresses;
        TypeIndices = columns.TypeIndices;
        Sizes = columns.Sizes;
        HeapKinds = columns.HeapKinds;
        Flags = columns.Flags;
        EdgeStarts = columns.EdgeStarts;
        Edges = columns.Edges;
        LargeSizes = columns.LargeSizes;
        Types = types;
        Roots = roots;
        Strings = strings;
        DroppedReferenceCount = droppedReferenceCount;
        DroppedRootCount = droppedRootCount;
        _owner = owner;
        _totalSize = ComputeTotalSize();
    }

    public int ObjectCount => Addresses.Length;

    public int EdgeCount => Edges.Length;

    public Column<ulong> Addresses { get; }

    public Column<int> TypeIndices { get; }

    public Column<uint> Sizes { get; }

    public Column<HeapKind> HeapKinds { get; }

    public Column<ObjectFlags> Flags { get; }

    public Column<int> EdgeStarts { get; }

    public Column<int> Edges { get; }

    public Column<LargeObjectSize> LargeSizes { get; }

    public TypeTable Types { get; }

    public RootTable Roots { get; }

    public StringTable Strings { get; }

    /// <summary>
    /// The dependent handles merged into the CSR as edges, sorted by primary then secondary (sprint 2.2), so an edge can be
    /// told apart from a field reference: <see cref="IsDependentHandleEdge"/>.
    /// </summary>
    public Column<DependentHandle> DependentHandles { get; }

    /// <summary>References whose target was not an object; dropped while building.</summary>
    public long DroppedReferenceCount { get; }

    /// <summary>Roots whose target was not an object; dropped while building.</summary>
    public long DroppedRootCount { get; }

    /// <summary>Bytes held by the graph's columns (types, roots and strings excluded), the per-object budget's basis.</summary>
    public long ColumnBytes =>
        Addresses.ByteLength + TypeIndices.ByteLength + Sizes.ByteLength + HeapKinds.ByteLength + Flags.ByteLength
        + EdgeStarts.ByteLength + Edges.ByteLength + LargeSizes.ByteLength + DependentHandles.ByteLength;

    /// <summary>Sum of all shallow sizes, computed once at construction (immutable, safe for concurrent readers).</summary>
    public ulong TotalSize => _totalSize;

    /// <summary>
    /// Creates a graph from existing columns (builder, index cache). Validates shapes, not contents; content
    /// integrity of persisted columns is guaranteed by checksums (ARCHITECTURE.md 7). <paramref name="dependentHandles"/>
    /// must be sorted by primary, then secondary.
    /// </summary>
    public static HeapGraph FromColumns(
        HeapGraphColumns columns,
        TypeTable types,
        RootTable roots,
        StringTable strings,
        long droppedReferenceCount,
        long droppedRootCount,
        IDisposable? owner,
        Column<DependentHandle> dependentHandles = default)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(strings);
        var count = columns.Addresses.Length;
        if (columns.TypeIndices.Length != count || columns.Sizes.Length != count || columns.HeapKinds.Length != count
            || columns.Flags.Length != count)
        {
            throw new ArgumentException("All per-object columns must have the same length.", nameof(columns));
        }

        if (columns.EdgeStarts.Length != count + 1)
        {
            throw new ArgumentException("EdgeStarts must have one more element than there are objects.", nameof(columns));
        }

        if (columns.EdgeStarts.Span[0] != 0 || columns.EdgeStarts.Span[count] != columns.Edges.Length)
        {
            throw new ArgumentException("EdgeStarts must start at 0 and end at the edge count.", nameof(columns));
        }

        foreach (var handle in dependentHandles.Span)
        {
            if ((uint)handle.Primary >= (uint)count || (uint)handle.Secondary >= (uint)count)
            {
                throw new ArgumentException("A dependent handle names an object outside the graph.", nameof(dependentHandles));
            }
        }

        return new HeapGraph(columns, types, roots, strings, droppedReferenceCount, droppedRootCount, owner, dependentHandles);
    }

    /// <summary>Shallow size of an object, including objects of 4 GiB or more.</summary>
    public ulong GetSize(int objectIndex)
    {
        var size = Sizes[objectIndex];
        return size != LargeSizeMarker ? size : FindLargeSize(objectIndex);
    }

    /// <summary>Outgoing references of an object, as target object indices.</summary>
    public ReadOnlySpan<int> GetReferences(int objectIndex)
    {
        var starts = EdgeStarts.Span;
        return Edges.Span[starts[objectIndex]..starts[objectIndex + 1]];
    }

    /// <summary>Whether the edge <paramref name="primary"/> -> <paramref name="secondary"/> comes from a dependent handle.</summary>
    public bool IsDependentHandleEdge(int primary, int secondary) =>
        DependentHandles.Span.BinarySearch(new DependentHandleKey(primary, secondary)) >= 0;

    /// <summary>Finds the object at exactly <paramref name="address"/>.</summary>
    public bool TryGetIndex(ulong address, out int objectIndex)
    {
        objectIndex = Addresses.Span.BinarySearch(address);
        if (objectIndex >= 0)
        {
            return true;
        }

        objectIndex = -1;
        return false;
    }

    /// <summary>Finds the object containing <paramref name="address"/> (floor search, for interior pointers).</summary>
    public bool TryGetIndexContaining(ulong address, out int objectIndex)
    {
        var addresses = Addresses.Span;
        var found = addresses.BinarySearch(address);
        var candidate = found >= 0 ? found : ~found - 1;
        if (candidate >= 0 && address - addresses[candidate] < Math.Max(GetSize(candidate), 1))
        {
            objectIndex = candidate;
            return true;
        }

        objectIndex = -1;
        return false;
    }

    /// <summary>
    /// Disposes <paramref name="owned"/> together with this graph (sprint 2.1): an optional index over the graph, such as
    /// <see cref="ReverseReferences"/> mapped from a sidecar, then lives exactly as long as the graph, which a snapshot
    /// releases only after its last lease. Returns false, without taking ownership, when the graph is already disposed.
    /// </summary>
    public bool Attach(IDisposable owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        lock (_attachedLock)
        {
            if (_disposed != 0)
            {
                return false;
            }

            (_attached ??= []).Add(owned);
            return true;
        }
    }

    public void Dispose()
    {
        List<IDisposable>? attached;
        lock (_attachedLock)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            attached = _attached;
            _attached = null;
        }

        foreach (var owned in attached ?? [])
        {
            owned.Dispose();
        }

        _owner?.Dispose();
    }

    private readonly struct DependentHandleKey(int primary, int secondary) : IComparable<DependentHandle>
    {
        public int CompareTo(DependentHandle other) =>
            primary != other.Primary ? primary.CompareTo(other.Primary) : secondary.CompareTo(other.Secondary);
    }

    private ulong FindLargeSize(int objectIndex)
    {
        foreach (var entry in LargeSizes.Span)
        {
            if (entry.ObjectIndex == objectIndex)
            {
                return entry.Size;
            }
        }

        throw new InvalidOperationException($"Object {objectIndex} is marked as large but has no large size entry.");
    }

    private ulong ComputeTotalSize()
    {
        ulong total = 0;
        var sizes = Sizes.Span;
        for (var i = 0; i < sizes.Length; i++)
        {
            if (sizes[i] != LargeSizeMarker)
            {
                total += sizes[i];
            }
        }

        foreach (var entry in LargeSizes.Span)
        {
            total += entry.Size;
        }

        return total;
    }
}
