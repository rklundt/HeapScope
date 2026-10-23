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

/// <summary>Size of an object of 4 GiB or more (its <c>Sizes</c> entry holds <see cref="uint.MaxValue"/>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly record struct LargeObjectSize(int ObjectIndex, ulong Size);
