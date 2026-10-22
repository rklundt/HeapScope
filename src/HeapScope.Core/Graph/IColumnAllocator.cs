namespace HeapScope.Core.Graph;

/// <summary>
/// Provides the memory for the final, exact-size columns of a graph being built (ARCHITECTURE.md 6.3). The
/// managed allocator returns arrays; a file-backed allocator can write columns straight into index cache sections
/// without changing the builder.
/// </summary>
public interface IColumnAllocator
{
    /// <summary>Allocates a writable column. Contents are unspecified; the builder overwrites every element.</summary>
    Memory<T> Allocate<T>(string columnName, int length)
        where T : unmanaged;

    /// <summary>
    /// Called once when the graph is built. Returns the object that owns the allocated memory (the graph disposes
    /// it), or null when the memory is garbage collected.
    /// </summary>
    IDisposable? DetachOwner();
}

/// <summary>Allocates columns as managed arrays.</summary>
public sealed class ManagedColumnAllocator : IColumnAllocator
{
    public static ManagedColumnAllocator Instance { get; } = new();

    public Memory<T> Allocate<T>(string columnName, int length)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return length == 0 ? Memory<T>.Empty : GC.AllocateUninitializedArray<T>(length);
    }

    public IDisposable? DetachOwner() => null;
}
