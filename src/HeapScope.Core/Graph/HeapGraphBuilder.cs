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
/// Builds a <see cref="HeapGraph"/> in two phases so peak memory stays near the final size (ARCHITECTURE.md 6.3):
/// <list type="number">
/// <item>Objects are added in any order, then frozen: sorted by address once, duplicates rejected.</item>
/// <item>References are added per source object in strictly increasing index order (walk
/// <see cref="FrozenAddresses"/>); roots and dependent handles are added after freezing.</item>
/// </list>
/// Root and reference semantics follow ARCHITECTURE.md 6.1. Any exception thrown while state is being changed
/// faults the builder: later calls throw instead of producing a corrupted graph. Dispose a builder that does not
/// reach <see cref="Build"/>, so column memory from the allocator is released.
/// </summary>
public sealed class HeapGraphBuilder : IDisposable
{
    private const int CancellationCheckInterval = 1 << 16;

    private readonly IColumnAllocator _allocator;
    private BuildPhase _phase = BuildPhase.Objects;

    // Phase 1 buffers, released at freeze.
    private ChunkedBuffer<ulong>? _addressBuffer = new();
    private ChunkedBuffer<int>? _typeBuffer = new();
    private ChunkedBuffer<uint>? _sizeBuffer = new();
    private ChunkedBuffer<HeapKind>? _kindBuffer = new();
    private ChunkedBuffer<ObjectFlags>? _flagBuffer = new();
    private Dictionary<ulong, ulong>? _largeSizesByAddress;
    private bool _inAddressOrder = true;
    private ulong _lastAddress;

    // Frozen object columns.
    private Memory<ulong> _addresses;
    private Memory<int> _typeIndices;
    private Memory<uint> _sizes;
    private Memory<HeapKind> _heapKinds;
    private Memory<ObjectFlags> _flags;
    private LargeObjectSize[] _largeSizes = [];
    private AddressIndex? _addressIndex;

    // Phase 2.
    private Memory<int> _edgeStarts;
    private ChunkedBuffer<int>? _edgeBuffer;
    private int _nextSource;
    private bool _referencesStarted;
    private readonly List<(int Primary, int Secondary)> _dependentEdges = [];
    private int _dependentCursor;
    private readonly ChunkedBuffer<GcRoot> _roots = new(1 << 14);

    public HeapGraphBuilder(IColumnAllocator? allocator = null)
    {
        _allocator = allocator ?? ManagedColumnAllocator.Instance;
        Strings = new StringTableBuilder();
        Types = new TypeTableBuilder(Strings);
    }

    private enum BuildPhase
    {
        Objects,
        References,
        Built,
        Faulted,
        Disposed,
    }

    public StringTableBuilder Strings { get; }

    public TypeTableBuilder Types { get; }

    /// <summary>Objects added so far (phase 1) or frozen (phase 2).</summary>
    public int ObjectCount => _phase == BuildPhase.Objects ? (int)_addressBuffer!.Count : _addresses.Length;

    /// <summary>
    /// The frozen, ascending object addresses; index i is object index i. Valid after <see cref="FreezeObjects"/>
    /// and until <see cref="Build"/>. Loaders walk it to add references in index order (ARCHITECTURE.md 6.3).
    /// </summary>
    public ReadOnlySpan<ulong> FrozenAddresses
    {
        get
        {
            EnsurePhase(BuildPhase.References);
            return _addresses.Span;
        }
    }

    public long DroppedReferenceCount { get; private set; }

    public long DroppedRootCount { get; private set; }

    /// <summary>Adds one object. Free objects must not be added.</summary>
    public void AddObject(ulong address, int typeIndex, ulong size, HeapKind heapKind, ObjectFlags flags = ObjectFlags.None)
    {
        EnsurePhase(BuildPhase.Objects);
        if (address == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(address), "Object address 0 is not valid.");
        }

        if ((uint)typeIndex >= (uint)Types.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(typeIndex), $"Type index {typeIndex} is not in the type table.");
        }

        if (_addressBuffer!.Count >= Array.MaxLength - 1)
        {
            throw new InvalidOperationException("A graph holds at most Array.MaxLength - 1 objects (ARCHITECTURE.md 6.6).");
        }

        if (address <= _lastAddress)
        {
            _inAddressOrder = false;
        }

        _lastAddress = address;
        _addressBuffer.Add(address);
        _typeBuffer!.Add(typeIndex);
        if (size >= HeapGraph.LargeSizeMarker)
        {
            (_largeSizesByAddress ??= [])[address] = size;
            _sizeBuffer!.Add(HeapGraph.LargeSizeMarker);
        }
        else
        {
            _sizeBuffer!.Add((uint)size);
        }

        _kindBuffer!.Add(heapKind);
        _flagBuffer!.Add(flags);
    }

    /// <summary>
    /// Ends phase 1: copies objects into their final columns, sorts them by address and rejects duplicates.
    /// Returns the object count.
    /// </summary>
    public int FreezeObjects(CancellationToken cancellationToken = default)
    {
        EnsurePhase(BuildPhase.Objects);
        try
        {
            var count = (int)_addressBuffer!.Count;
            _addresses = CopyAndRelease(ref _addressBuffer, "Addresses", count);
            _typeIndices = CopyAndRelease(ref _typeBuffer, "TypeIndices", count);
            _sizes = CopyAndRelease(ref _sizeBuffer, "Sizes", count);
            _heapKinds = CopyAndRelease(ref _kindBuffer, "HeapKinds", count);
            _flags = CopyAndRelease(ref _flagBuffer, "Flags", count);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_inAddressOrder)
            {
                SortByAddress(count, cancellationToken);
                ThrowOnDuplicateAddress(cancellationToken);
            }

            if (_largeSizesByAddress is not null)
            {
                var large = new List<LargeObjectSize>(_largeSizesByAddress.Count);
                foreach (var (address, size) in _largeSizesByAddress)
                {
                    large.Add(new LargeObjectSize(_addresses.Span.BinarySearch(address), size));
                }

                large.Sort((a, b) => a.ObjectIndex.CompareTo(b.ObjectIndex));
                _largeSizes = [.. large];
                _largeSizesByAddress = null;
            }

            _addressIndex = AddressIndex.Build(_addresses.Span);
            _edgeStarts = _allocator.Allocate<int>("EdgeStarts", count + 1);
            _edgeBuffer = new ChunkedBuffer<int>();
            _phase = BuildPhase.References;
            return count;
        }
        catch
        {
            _phase = BuildPhase.Faulted;
            throw;
        }
    }

    /// <summary>Index of the object at exactly <paramref name="address"/>, or -1. Valid after <see cref="FreezeObjects"/>.</summary>
    public int IndexOf(ulong address)
    {
        EnsurePhase(BuildPhase.References);
        return _addressIndex!.IndexOf(_addresses.Span, address);
    }

    /// <summary>
    /// Registers a dependent handle (ARCHITECTURE.md 6.1): an edge primary -> secondary that the builder merges into
    /// the primary's row. Must be called before the first reference is added. This is the only way dependent-handle
    /// edges may enter the graph; <see cref="AddReferences"/> must never include them as well.
    /// </summary>
    public void AddDependentHandle(ulong primaryAddress, ulong secondaryAddress)
    {
        EnsurePhase(BuildPhase.References);
        if (_referencesStarted)
        {
            throw new InvalidOperationException("Dependent handles must be added before the reference pass starts.");
        }

        if (secondaryAddress == 0)
        {
            return;
        }

        var primary = IndexOf(primaryAddress);
        var secondary = IndexOf(secondaryAddress);
        if (primary < 0 || secondary < 0)
        {
            DroppedReferenceCount++;
            return;
        }

        _dependentEdges.Add((primary, secondary));
    }

    /// <summary>
    /// Adds the object's own outgoing references (its fields and array elements, including references inside
    /// embedded value types), by target address. Dependent-handle targets must not be included: they go only
    /// through <see cref="AddDependentHandle"/>. Null targets are ignored; targets that are not objects are dropped
    /// and counted. Source indices must be strictly increasing across calls.
    /// </summary>
    public void AddReferences(int sourceIndex, ReadOnlySpan<ulong> targetAddresses)
    {
        BeginRow(sourceIndex);
        try
        {
            var addresses = _addresses.Span;
            var addressIndex = _addressIndex!;
            foreach (var target in targetAddresses)
            {
                if (target == 0)
                {
                    continue;
                }

                var index = addressIndex.IndexOf(addresses, target);
                if (index >= 0)
                {
                    AppendEdge(index);
                }
                else
                {
                    DroppedReferenceCount++;
                }
            }

            EndRow(sourceIndex);
        }
        catch
        {
            _phase = BuildPhase.Faulted;
            throw;
        }
    }

    /// <summary>Adds the outgoing references of one object, by target index (synthetic graphs, tests).</summary>
    public void AddReferenceIndices(int sourceIndex, ReadOnlySpan<int> targetIndices)
    {
        // Validate the whole row before changing any state, so a bad argument never leaves a partial row.
        var count = _phase == BuildPhase.References ? _addresses.Length : 0;
        foreach (var target in targetIndices)
        {
            if ((uint)target >= (uint)count)
            {
                EnsurePhase(BuildPhase.References);
                throw new ArgumentOutOfRangeException(nameof(targetIndices), $"Target index {target} is not an object.");
            }
        }

        BeginRow(sourceIndex);
        try
        {
            foreach (var target in targetIndices)
            {
                AppendEdge(target);
            }

            EndRow(sourceIndex);
        }
        catch
        {
            _phase = BuildPhase.Faulted;
            throw;
        }
    }

    /// <summary>
    /// Adds a root by address. Roots flagged <see cref="RootFlags.Interior"/> resolve to the object containing the
    /// address; others need an exact match. Address 0 is ignored (for example a weak handle whose object was
    /// collected); other unresolved roots are dropped and counted. Weak handles never retain.
    /// </summary>
    public void AddRoot(ulong address, RootKind kind, RootFlags flags, string? detail = null)
    {
        EnsurePhase(BuildPhase.References);
        if (address == 0)
        {
            return;
        }

        var index = (flags & RootFlags.Interior) != 0 ? FindContaining(address) : IndexOf(address);
        if (index < 0)
        {
            DroppedRootCount++;
            return;
        }

        _roots.Add(new GcRoot(index, kind, Normalize(kind, flags), Strings.GetOrAdd(detail)));
    }

    /// <summary>Adds a root by object index (synthetic graphs, tests). Weak handles never retain.</summary>
    public void AddRootIndex(int objectIndex, RootKind kind, RootFlags flags, string? detail = null)
    {
        EnsurePhase(BuildPhase.References);
        if ((uint)objectIndex >= (uint)_addresses.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(objectIndex));
        }

        _roots.Add(new GcRoot(objectIndex, kind, Normalize(kind, flags), Strings.GetOrAdd(detail)));
    }

    /// <summary>Finishes the graph. Objects without added references get none (plus their dependent-handle edges).</summary>
    public HeapGraph Build(CancellationToken cancellationToken = default)
    {
        if (_phase == BuildPhase.Objects)
        {
            FreezeObjects(cancellationToken);
        }

        EnsurePhase(BuildPhase.References);
        try
        {
            var count = _addresses.Length;
            PrepareDependentEdges();
            FinishRowsBefore(count, cancellationToken);
            _edgeStarts.Span[count] = (int)_edgeBuffer!.Count;
            var edges = CopyAndRelease(ref _edgeBuffer, "Edges", (int)_edgeBuffer!.Count);
            cancellationToken.ThrowIfCancellationRequested();

            var roots = new GcRoot[_roots.Count];
            _roots.CopyTo(roots);
            _roots.Clear();
            Array.Sort(roots, static (a, b) => a.ObjectIndex != b.ObjectIndex
                ? a.ObjectIndex.CompareTo(b.ObjectIndex)
                : a.Kind.CompareTo(b.Kind));

            var strings = Strings.Build();
            var columns = new HeapGraphColumns(
                new Column<ulong>(_addresses),
                new Column<int>(_typeIndices),
                new Column<uint>(_sizes),
                new Column<HeapKind>(_heapKinds),
                new Column<ObjectFlags>(_flags),
                new Column<int>(_edgeStarts),
                new Column<int>(edges),
                Column<LargeObjectSize>.FromArray(_largeSizes));

            _addressIndex = null;
            var graph = HeapGraph.FromColumns(
                columns,
                Types.Build(strings),
                new RootTable(Column<GcRoot>.FromArray(roots)),
                strings,
                DroppedReferenceCount,
                DroppedRootCount,
                _allocator.DetachOwner(),
                Column<DependentHandle>.FromArray([.. _dependentEdges.Distinct().Select(e => new DependentHandle(e.Primary, e.Secondary))]));
            _phase = BuildPhase.Built;
            return graph;
        }
        catch
        {
            _phase = BuildPhase.Faulted;
            throw;
        }
    }

    /// <summary>Releases allocator memory when the graph was not built (failure or cancellation).</summary>
    public void Dispose()
    {
        if (_phase is BuildPhase.Built or BuildPhase.Disposed)
        {
            return;
        }

        _phase = BuildPhase.Disposed;
        _addressBuffer = null;
        _typeBuffer = null;
        _sizeBuffer = null;
        _kindBuffer = null;
        _flagBuffer = null;
        _edgeBuffer = null;
        _roots.Clear();
        _allocator.DetachOwner()?.Dispose();
    }

    private static RootFlags Normalize(RootKind kind, RootFlags flags) =>
        kind == RootKind.WeakHandle ? flags & ~RootFlags.Retaining : flags;

    private void BeginRow(int sourceIndex)
    {
        EnsurePhase(BuildPhase.References);
        if ((uint)sourceIndex >= (uint)_addresses.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        }

        if (sourceIndex < _nextSource)
        {
            throw new InvalidOperationException(
                $"References must be added in strictly increasing source order; got object {sourceIndex} after {_nextSource - 1}.");
        }

        try
        {
            PrepareDependentEdges();
            FinishRowsBefore(sourceIndex, CancellationToken.None);
            _edgeStarts.Span[sourceIndex] = (int)_edgeBuffer!.Count;
        }
        catch
        {
            _phase = BuildPhase.Faulted;
            throw;
        }
    }

    private void EndRow(int sourceIndex)
    {
        AppendDependentEdges(sourceIndex);
        _nextSource = sourceIndex + 1;
    }

    private void PrepareDependentEdges()
    {
        if (_referencesStarted)
        {
            return;
        }

        _referencesStarted = true;
        _dependentEdges.Sort((a, b) => a.Primary != b.Primary ? a.Primary.CompareTo(b.Primary) : a.Secondary.CompareTo(b.Secondary));
    }

    /// <summary>Closes every row before <paramref name="limit"/> that received no explicit references.</summary>
    private void FinishRowsBefore(int limit, CancellationToken cancellationToken)
    {
        var starts = _edgeStarts.Span;
        for (var row = _nextSource; row < limit; row++)
        {
            starts[row] = (int)_edgeBuffer!.Count;
            AppendDependentEdges(row);
            if ((row & (CancellationCheckInterval - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        _nextSource = Math.Max(_nextSource, limit);
    }

    private void AppendDependentEdges(int primary)
    {
        while (_dependentCursor < _dependentEdges.Count && _dependentEdges[_dependentCursor].Primary == primary)
        {
            AppendEdge(_dependentEdges[_dependentCursor].Secondary);
            _dependentCursor++;
        }
    }

    private void AppendEdge(int target)
    {
        if (_edgeBuffer!.Count >= Array.MaxLength)
        {
            throw new InvalidOperationException("A graph holds at most Array.MaxLength references (ARCHITECTURE.md 6.6).");
        }

        _edgeBuffer.Add(target);
    }

    private int FindContaining(ulong address)
    {
        var addresses = _addresses.Span;
        var found = addresses.BinarySearch(address);
        var candidate = found >= 0 ? found : ~found - 1;
        if (candidate < 0)
        {
            return -1;
        }

        var size = _sizes.Span[candidate] == HeapGraph.LargeSizeMarker
            ? FindLargeSize(candidate)
            : _sizes.Span[candidate];
        return address - addresses[candidate] < Math.Max(size, 1UL) ? candidate : -1;
    }

    private ulong FindLargeSize(int objectIndex)
    {
        foreach (var entry in _largeSizes)
        {
            if (entry.ObjectIndex == objectIndex)
            {
                return entry.Size;
            }
        }

        throw new InvalidOperationException($"Object {objectIndex} is marked as large but has no large size entry.");
    }

    private void SortByAddress(int count, CancellationToken cancellationToken)
    {
        var permutation = GC.AllocateUninitializedArray<int>(count);
        for (var i = 0; i < count; i++)
        {
            permutation[i] = i;
        }

        _addresses.Span.Sort(permutation.AsSpan());
        cancellationToken.ThrowIfCancellationRequested();
        PermuteInPlace(_typeIndices.Span, permutation, cancellationToken);
        PermuteInPlace(_sizes.Span, permutation, cancellationToken);
        PermuteInPlace(_heapKinds.Span, permutation, cancellationToken);
        PermuteInPlace(_flags.Span, permutation, cancellationToken);
    }

    /// <summary>
    /// Reorders <paramref name="column"/> so that new[i] = old[permutation[i]], following permutation cycles in place.
    /// Visited entries are marked by complementing them (indices are never negative) and restored afterwards, so the
    /// only extra memory is the permutation itself (no full-length temporary on the LOH).
    /// </summary>
    internal static void PermuteInPlace<T>(Span<T> column, Span<int> permutation, CancellationToken cancellationToken)
        where T : unmanaged
    {
        for (var start = 0; start < permutation.Length; start++)
        {
            if (permutation[start] < 0)
            {
                continue;
            }

            var saved = column[start];
            var current = start;
            while (true)
            {
                var next = permutation[current];
                permutation[current] = ~next;
                if (next == start)
                {
                    column[current] = saved;
                    break;
                }

                column[current] = column[next];
                current = next;
            }

            if ((start & (CancellationCheckInterval - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        for (var i = 0; i < permutation.Length; i++)
        {
            permutation[i] = ~permutation[i];
        }
    }

    private void ThrowOnDuplicateAddress(CancellationToken cancellationToken)
    {
        var addresses = _addresses.Span;
        for (var i = 1; i < addresses.Length; i++)
        {
            if (addresses[i] == addresses[i - 1])
            {
                throw new InvalidOperationException($"Duplicate object address 0x{addresses[i]:X}: each object may be added once.");
            }

            if ((i & (CancellationCheckInterval - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private Memory<T> CopyAndRelease<T>(ref ChunkedBuffer<T>? buffer, string columnName, int count)
        where T : unmanaged
    {
        var column = _allocator.Allocate<T>(columnName, count);
        buffer!.CopyTo(column.Span);
        buffer.Clear();
        buffer = null;
        return column;
    }

    private void EnsurePhase(BuildPhase expected)
    {
        if (_phase == expected)
        {
            return;
        }

        throw _phase switch
        {
            BuildPhase.Faulted => new InvalidOperationException("The builder failed earlier; start a new build."),
            BuildPhase.Disposed => new ObjectDisposedException(nameof(HeapGraphBuilder)),
            _ => new InvalidOperationException($"This operation needs the {expected} phase; the builder is in the {_phase} phase."),
        };
    }
}
