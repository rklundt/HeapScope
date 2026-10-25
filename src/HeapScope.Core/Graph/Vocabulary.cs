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

using System.Runtime.InteropServices;

namespace HeapScope.Core.Graph;

// The types in this file are stored in graph columns and persisted in the index cache in their in-memory layout
// (ARCHITECTURE.md 7). Their underlying types, numeric values and field layouts must never change.

/// <summary>Where an object lives in the managed heap.</summary>
public enum HeapKind : byte
{
    Unknown = 0,
    Gen0 = 1,
    Gen1 = 2,
    Gen2 = 3,
    LargeObjectHeap = 4,
    PinnedObjectHeap = 5,
    Frozen = 6,
}

/// <summary>Per-object flags.</summary>
[Flags]
public enum ObjectFlags : byte
{
    None = 0,

    /// <summary>
    /// The object is registered in the finalization queue and finalization was not suppressed: registered in the
    /// runtime's finalizable-object list and bit 0x40000000 of the object header clear (GC.SuppressFinalize sets it
    /// without removing the object from the queue). Not a root by itself (ARCHITECTURE.md 6.1).
    /// </summary>
    HasFinalizer = 1 << 0,
}

/// <summary>Kind of a type in the type table.</summary>
[Flags]
public enum TypeKind : ushort
{
    None = 0,
    Array = 1 << 0,
    String = 1 << 1,
    ValueTypeBox = 1 << 2,
    Delegate = 1 << 3,
    Exception = 1 << 4,

    /// <summary>
    /// An array whose elements are references (sprint 1.6): its non-null slots are its outgoing references. Index caches
    /// written before 1.6 lack the flag.
    /// </summary>
    ReferenceArray = 1 << 5,
}

/// <summary>Why a root exists (ARCHITECTURE.md 6.1). Dependent handles are edges, never roots.</summary>
public enum RootKind : byte
{
    Other = 0,
    StaticField = 1,
    ThreadStack = 2,
    StrongHandle = 3,
    PinnedHandle = 4,
    AsyncPinnedHandle = 5,
    RefCountedHandle = 6,
    FinalizerQueue = 7,
    WeakHandle = 8,
}

/// <summary>Root properties.</summary>
[Flags]
public enum RootFlags : byte
{
    None = 0,

    /// <summary>The root keeps its object alive. Weak handles never have it; ref-counted handles only while strong.</summary>
    Retaining = 1 << 0,

    /// <summary>The root pins its object.</summary>
    Pinned = 1 << 1,

    /// <summary>The root pointer may point inside its object (resolved with a floor search).</summary>
    Interior = 1 << 2,
}

/// <summary>
/// A dependent handle the loader merged into the CSR as the edge <see cref="Primary"/> -> <see cref="Secondary"/>
/// (sprint 2.2, ARCHITECTURE.md 6.1): the secondary is kept alive by the handle table while the primary is reachable, not by
/// a field of the primary. 8 bytes, persisted.
/// </summary>
public readonly record struct DependentHandle(int Primary, int Secondary);

/// <summary>One GC root, 12 bytes. Fields are declared explicitly so the persisted layout is fixed.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GcRoot : IEquatable<GcRoot>
{
    private readonly int _objectIndex;
    private readonly RootKind _kind;
    private readonly RootFlags _flags;
    private readonly ushort _reserved;
    private readonly int _detailId;

    public GcRoot(int objectIndex, RootKind kind, RootFlags flags, int detailId)
    {
        _objectIndex = objectIndex;
        _kind = kind;
        _flags = flags;
        _reserved = 0;
        _detailId = detailId;
    }

    /// <summary>The rooted object.</summary>
    public int ObjectIndex => _objectIndex;

    public RootKind Kind => _kind;

    public RootFlags Flags => _flags;

    /// <summary>String table id of the root detail (field name, thread), or -1.</summary>
    public int DetailId => _detailId;

    public bool IsRetaining => (_flags & RootFlags.Retaining) != 0;

    public static bool operator ==(GcRoot left, GcRoot right) => left.Equals(right);

    public static bool operator !=(GcRoot left, GcRoot right) => !left.Equals(right);

    public bool Equals(GcRoot other) =>
        _objectIndex == other._objectIndex && _kind == other._kind && _flags == other._flags && _detailId == other._detailId;

    public override bool Equals(object? obj) => obj is GcRoot other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_objectIndex, _kind, _flags, _detailId);

    public override string ToString() => $"{_kind} -> #{_objectIndex} ({_flags})";
}

/// <summary>
/// Size of an object of 4 GiB or more (its <c>Sizes</c> entry holds <see cref="uint.MaxValue"/>). 16 bytes with an
/// explicit, always zero reserved field, so persisted bytes are deterministic.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct LargeObjectSize : IEquatable<LargeObjectSize>
{
    private readonly int _objectIndex;
    private readonly int _reserved;
    private readonly ulong _size;

    public LargeObjectSize(int objectIndex, ulong size)
    {
        _objectIndex = objectIndex;
        _reserved = 0;
        _size = size;
    }

    public int ObjectIndex => _objectIndex;

    public ulong Size => _size;

    public static bool operator ==(LargeObjectSize left, LargeObjectSize right) => left.Equals(right);

    public static bool operator !=(LargeObjectSize left, LargeObjectSize right) => !left.Equals(right);

    public bool Equals(LargeObjectSize other) => _objectIndex == other._objectIndex && _size == other._size;

    public override bool Equals(object? obj) => obj is LargeObjectSize other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_objectIndex, _size);
}
