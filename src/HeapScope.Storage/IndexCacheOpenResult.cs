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

    /// <summary>A sidecar extends another base cache than the one open (sprint 2.1), or a sidecar was opened as a base cache.</summary>
    BaseMismatch,
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
