using HeapScope.Contracts;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The rows of a result set, kept as compact typed data and formatted to string cells only for the page a client
/// asks for (ARCHITECTURE.md 4.6). Never one string per cell for the whole set.
/// </summary>
internal interface IResultSource
{
    IReadOnlyList<ResultColumn> Columns { get; }

    int Count { get; }

    /// <summary>Approximate bytes held by this source, used by the store's byte budget.</summary>
    long EstimatedBytes { get; }

    /// <summary>Writes the cells of row <paramref name="index"/> into <paramref name="cells"/> (one per column).</summary>
    void FormatRow(int index, string?[] cells);
}
