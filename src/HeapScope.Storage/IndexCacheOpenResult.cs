using HeapScope.Core.Graph;

namespace HeapScope.Storage;

/// <summary>How the graph's columns are backed (ADR-015).</summary>
public enum MemoryMode
{
    /// <summary>Managed arrays: fastest, uses private memory.</summary>
    InMemory,

    /// <summary>Read-only views of the index cache file: pages are loaded on demand and can be dropped by the OS.</summary>
    Mapped,
}

/// <summary>Why an index cache cannot be used; the cache is then rebuilt, never patched (ARCHITECTURE.md 7).</summary>
public enum IndexCacheInvalidReason
{
    None,
    Missing,
    Truncated,
    BadMagic,
    VersionMismatch,
    HeaderCorrupted,
    Malformed,
    FingerprintMismatch,
    ChecksumMismatch,
    IoError,
}

/// <summary>The outcome of opening an index cache. Invalid caches never throw; they report a reason.</summary>
public sealed class IndexCacheOpenResult
{
    private IndexCacheOpenResult(HeapGraph? graph, IndexCacheMetadata? metadata, MemoryMode mode, IndexCacheInvalidReason reason, string? detail, ulong identity = 0)
    {
        Identity = identity;
        Graph = graph;
        Metadata = metadata;
        Mode = mode;
        Reason = reason;
        Detail = detail;
    }

    public bool Success => Graph is not null;

    /// <summary>The graph; the caller owns it (disposing it releases a mapped file).</summary>
    public HeapGraph? Graph { get; }

    public IndexCacheMetadata? Metadata { get; }

    public MemoryMode Mode { get; }

    public IndexCacheInvalidReason Reason { get; }

    /// <summary>
    /// Identity of the opened cache file (its header checksum, which covers every section checksum). Optional index
    /// files (reverse references, dominators; sprint 2.1 and later) record it to name the base cache they extend.
    /// </summary>
    public ulong Identity { get; }

    /// <summary>A plain-language explanation when the cache is invalid.</summary>
    public string? Detail { get; }

    internal static IndexCacheOpenResult Opened(HeapGraph graph, IndexCacheMetadata metadata, MemoryMode mode, ulong identity) =>
        new(graph, metadata, mode, IndexCacheInvalidReason.None, null, identity);

    internal static IndexCacheOpenResult Invalid(IndexCacheInvalidReason reason, string detail) =>
        new(null, null, MemoryMode.InMemory, reason, detail);
}
