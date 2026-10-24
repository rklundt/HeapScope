using HeapScope.Core.Graph;

namespace HeapScope.Core.Synthetic;

/// <summary>A hand-made object in a <see cref="SyntheticGraphBuilder"/>.</summary>
/// <param name="Id">Creation order, which is also the object index (objects get ascending addresses).</param>
/// <param name="Address">The synthetic address.</param>
public readonly record struct SyntheticObject(int Id, ulong Address);

/// <summary>
/// Builds small graphs by hand for tests whose right answer is known by construction (DEVELOPER.md 5).
/// Objects get ascending, 8-byte aligned addresses in creation order, so object index equals creation order.
/// </summary>
public sealed class SyntheticGraphBuilder
{
    /// <summary>Address of the first synthetic object.</summary>
    public const ulong BaseAddress = 0x0001_0000;

    private readonly List<(string Type, ulong Size, HeapKind Kind, ObjectFlags Flags, string? Module, ulong MethodTable, TypeKind TypeKind, uint BaseSize, uint ComponentSize)> _objects = [];
    private readonly List<List<ulong>> _references = [];
    private readonly List<(ulong Address, RootKind Kind, RootFlags Flags, string? Detail)> _roots = [];
    private readonly List<(ulong Primary, ulong Secondary)> _dependentHandles = [];
    private ulong _nextAddress = BaseAddress;

    /// <summary>
    /// Creates an object of the given type and shallow size. Types are matched by name, or by
    /// <paramref name="methodTable"/> when it is not zero (distinct types that share a name, for example one assembly
    /// loaded into two load contexts).
    /// </summary>
    public SyntheticObject Object(
        string typeName,
        ulong size = 24,
        HeapKind heapKind = HeapKind.Gen0,
        ObjectFlags flags = ObjectFlags.None,
        string? module = null,
        ulong methodTable = 0,
        TypeKind typeKind = TypeKind.None,
        uint baseSize = 0,
        uint componentSize = 0)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var address = _nextAddress;
        var span = Math.Max(size, 8UL);
        _nextAddress += (span + 7) & ~7UL;
        _objects.Add((typeName, size, heapKind, flags, module, methodTable, typeKind, baseSize == 0 ? (uint)Math.Min(size, uint.MaxValue) : baseSize, componentSize));
        _references.Add([]);
        return new SyntheticObject(_objects.Count - 1, address);
    }

    /// <summary>
    /// Creates an array of <paramref name="length"/> elements of <paramref name="componentSize"/> bytes (sprint 1.6): a
    /// 24-byte base, like x64 arrays; <paramref name="references"/> marks an array of references.
    /// </summary>
    public SyntheticObject Array(string typeName, long length, uint componentSize, bool references = false, HeapKind heapKind = HeapKind.Gen0) =>
        Object(typeName, 24 + ((ulong)length * componentSize), heapKind, typeKind: TypeKind.Array | (references ? TypeKind.ReferenceArray : TypeKind.None), baseSize: 24, componentSize: componentSize);

    /// <summary>Adds a reference from one object to another (duplicates allowed, like repeated fields).</summary>
    public SyntheticGraphBuilder Reference(SyntheticObject from, SyntheticObject to)
    {
        _references[from.Id].Add(to.Address);
        return this;
    }

    /// <summary>Adds a reference to an address that is not an object (it will be dropped and counted).</summary>
    public SyntheticGraphBuilder DanglingReference(SyntheticObject from, ulong address)
    {
        _references[from.Id].Add(address);
        return this;
    }

    /// <summary>Roots an object. Retaining by default.</summary>
    public SyntheticGraphBuilder Root(SyntheticObject target, RootKind kind = RootKind.StaticField, string? detail = null, RootFlags flags = RootFlags.Retaining)
    {
        _roots.Add((target.Address, kind, flags, detail));
        return this;
    }

    /// <summary>Adds a root by raw address (for interior and dangling roots).</summary>
    public SyntheticGraphBuilder RootAddress(ulong address, RootKind kind, RootFlags flags, string? detail = null)
    {
        _roots.Add((address, kind, flags, detail));
        return this;
    }

    /// <summary>Adds a dependent handle: the secondary is kept alive only while the primary is reachable.</summary>
    public SyntheticGraphBuilder DependentHandle(SyntheticObject primary, SyntheticObject secondary)
    {
        _dependentHandles.Add((primary.Address, secondary.Address));
        return this;
    }

    public HeapGraph Build()
    {
        using var builder = new HeapGraphBuilder();
        var address = BaseAddress;
        foreach (var (type, size, kind, flags, module, methodTable, typeKind, baseSize, componentSize) in _objects)
        {
            var typeIndex = builder.Types.GetOrAdd(type, methodTable, baseSize, componentSize, module, typeKind);
            builder.AddObject(address, typeIndex, size, kind, flags);
            address += (Math.Max(size, 8UL) + 7) & ~7UL;
        }

        builder.FreezeObjects();
        foreach (var (primary, secondary) in _dependentHandles)
        {
            builder.AddDependentHandle(primary, secondary);
        }

        for (var i = 0; i < _references.Count; i++)
        {
            builder.AddReferences(i, [.. _references[i]]);
        }

        foreach (var (rootAddress, kind, flags, detail) in _roots)
        {
            builder.AddRoot(rootAddress, kind, flags, detail);
        }

        return builder.Build();
    }
}
