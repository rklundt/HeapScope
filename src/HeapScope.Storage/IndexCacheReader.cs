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

using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HeapScope.Core.Graph;

namespace HeapScope.Storage;

/// <summary>
/// Opens index cache files (ARCHITECTURE.md 7) in memory or mapped (ADR-015). One file handle is used from the first
/// header byte to the last section byte (and becomes the mapping in mapped mode), so the file that was validated is
/// the file that is used. Every check (magic, version, header checksum, every directory entry, fingerprint, every
/// section checksum) runs before a graph is returned. A damaged, truncated or hostile file returns a reason and
/// never throws or reads outside the file.
/// </summary>
public static class IndexCacheReader
{
    private const int ChunkBytes = 1 << 26;

    /// <summary>
    /// Opens a cache. When <paramref name="expected"/> is given, the cache must have been built from a snapshot with
    /// that fingerprint.
    /// </summary>
    public static IndexCacheOpenResult Open(string path, IndexCacheSigner signer, SnapshotFingerprint? expected, MemoryMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Missing, $"No index cache at {path}.");
        }

        FileStream? stream = null;
        try
        {
            // FileShare.Read: nobody can write, rename over or delete the file while it is being read or mapped.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
            var directory = IndexFileDirectory.Read(stream, signer, IndexFileLayout.Base, expected, 0, out var header, out var failure);
            if (directory is null)
            {
                return failure!;
            }

            if (mode == MemoryMode.InMemory)
            {
                return OpenInMemory(stream, directory, header.HeaderChecksum, cancellationToken);
            }

            // Sequential verification is several times faster than hashing through the mapping and does not grow the
            // worker's working set (gc-specialist review of 0.5).
            if (IndexFileDirectory.VerifySequentially(stream, directory, cancellationToken) is { } corrupted)
            {
                return corrupted;
            }

            var mapped = OpenMapped(stream, directory, header.HeaderChecksum);
            stream = null; // Owned by the mapping now.
            return mapped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.IoError, $"The index cache could not be read: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or JsonException or InvalidDataException)
        {
            return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, $"The index cache is malformed: {ex.Message}");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// Checks a cache without reading its sections: present, header and directory valid, fingerprint equal. Returns
    /// null when it would open (barring section corruption), otherwise the reason it would not.
    /// </summary>
    public static IndexCacheOpenResult? Probe(string path, IndexCacheSigner signer, SnapshotFingerprint expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Missing, $"No index cache at {path}.");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IndexFileDirectory.Read(stream, signer, IndexFileLayout.Base, expected, 0, out _, out var failure) is null ? failure : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.IoError, $"The index cache could not be read: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidDataException)
        {
            return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, $"The index cache is malformed: {ex.Message}");
        }
    }

    /// <summary>Bytes the graph's sections would take in memory, from the directory; null when the cache is invalid.</summary>
    public static long? GetInMemoryBytes(string path, IndexCacheSigner signer) => GetInMemoryBytes(path, signer, out _);

    /// <summary>
    /// Bytes the graph's sections would take in memory, from the directory; null when the cache is missing or invalid, with
    /// <paramref name="failure"/> saying why when it exists but is invalid (3.7 review H3: an untrusted cache is logged).
    /// </summary>
    public static long? GetInMemoryBytes(string path, IndexCacheSigner signer, out IndexCacheOpenResult? failure)
    {
        failure = null;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IndexFileDirectory.Read(stream, signer, IndexFileLayout.Base, expected: null, 0, out _, out failure)?.Values.Sum(e => e.ByteLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.IoError, $"The index cache could not be read: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidDataException)
        {
            failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, $"The index cache is malformed: {ex.Message}");
            return null;
        }
    }

    private static IndexCacheOpenResult OpenInMemory(FileStream stream, Dictionary<IndexSection, SectionEntry> directory, ulong identity, CancellationToken cancellationToken)
    {
        var reader = new ArrayReader(stream.SafeFileHandle, directory, cancellationToken);
        var addresses = reader.Read<ulong>(IndexSection.Addresses);
        var typeIndices = reader.Read<int>(IndexSection.TypeIndices);
        var sizes = reader.Read<uint>(IndexSection.Sizes);
        var heapKinds = reader.Read<HeapKind>(IndexSection.HeapKinds);
        var flags = reader.Read<ObjectFlags>(IndexSection.ObjectFlags);
        var edgeStarts = reader.Read<int>(IndexSection.EdgeStarts);
        var edges = reader.Read<int>(IndexSection.Edges);
        var roots = reader.Read<GcRoot>(IndexSection.Roots);
        var small = new Dictionary<IndexSection, byte[]>();
        foreach (var section in SmallSections)
        {
            small[section] = reader.Read<byte>(section);
        }

        if (reader.Failure is { } failed)
        {
            return failed;
        }

        return Assemble(
            new HeapGraphColumns(
                Column<ulong>.FromArray(addresses),
                Column<int>.FromArray(typeIndices),
                Column<uint>.FromArray(sizes),
                Column<HeapKind>.FromArray(heapKinds),
                Column<ObjectFlags>.FromArray(flags),
                Column<int>.FromArray(edgeStarts),
                Column<int>.FromArray(edges),
                Column<LargeObjectSize>.Empty),
            Column<GcRoot>.FromArray(roots),
            small,
            MemoryMode.InMemory,
            identity,
            owner: null);
    }

    private static unsafe IndexCacheOpenResult OpenMapped(FileStream stream, Dictionary<IndexSection, SectionEntry> directory, ulong identity)
    {
        var file = new MappedIndexFile(stream);
        try
        {
            foreach (var entry in directory.Values)
            {
                // The mapping comes from the validated handle; this guards the pointer arithmetic below regardless.
                if (entry.Offset > file.Length || entry.ByteLength > file.Length - entry.Offset)
                {
                    file.Dispose();
                    return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Truncated, $"The index cache mapping ends inside section {(IndexSection)entry.Id}.");
                }
            }

            Column<T> Map<T>(IndexSection section)
                where T : unmanaged
            {
                var entry = directory[section];
                return entry.ElementCount == 0
                    ? Column<T>.Empty
                    : new Column<T>(new MappedMemoryManager<T>(file.Pointer + entry.Offset, (int)entry.ElementCount).Memory);
            }

            var small = new Dictionary<IndexSection, byte[]>();
            foreach (var section in SmallSections)
            {
                var entry = directory[section];
                small[section] = new ReadOnlySpan<byte>(file.Pointer + entry.Offset, (int)entry.ByteLength).ToArray();
            }

            var result = Assemble(
                new HeapGraphColumns(
                    Map<ulong>(IndexSection.Addresses),
                    Map<int>(IndexSection.TypeIndices),
                    Map<uint>(IndexSection.Sizes),
                    Map<HeapKind>(IndexSection.HeapKinds),
                    Map<ObjectFlags>(IndexSection.ObjectFlags),
                    Map<int>(IndexSection.EdgeStarts),
                    Map<int>(IndexSection.Edges),
                    Column<LargeObjectSize>.Empty),
                Map<GcRoot>(IndexSection.Roots),
                small,
                MemoryMode.Mapped,
                identity,
                file);
            if (!result.Success)
            {
                file.Dispose();
            }

            return result;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static IndexSection[] SmallSections { get; } =
    [
        IndexSection.LargeSizes, IndexSection.TypeNameIds, IndexSection.TypeMethodTables, IndexSection.TypeBaseSizes,
        IndexSection.TypeComponentSizes, IndexSection.TypeModuleIds, IndexSection.TypeKinds, IndexSection.Strings,
        IndexSection.Metadata, IndexSection.DependentHandles,
    ];

    /// <summary>Builds the graph from columns plus the small sections (always copied into managed memory).</summary>
    private static IndexCacheOpenResult Assemble(
        HeapGraphColumns columns,
        Column<GcRoot> roots,
        Dictionary<IndexSection, byte[]> small,
        MemoryMode mode,
        ulong identity,
        IDisposable? owner)
    {
        var strings = DecodeStrings(small[IndexSection.Strings]);
        if (strings is null)
        {
            return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, "The index cache string section is malformed.");
        }

        var document = JsonSerializer.Deserialize<MetadataDocument>(small[IndexSection.Metadata]);
        if (document?.Metadata?.Info is null)
        {
            return IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, "The index cache metadata section is malformed.");
        }

        var types = new TypeTable(
            Column<int>.FromArray(MemoryMarshal.Cast<byte, int>(small[IndexSection.TypeNameIds]).ToArray()),
            Column<ulong>.FromArray(MemoryMarshal.Cast<byte, ulong>(small[IndexSection.TypeMethodTables]).ToArray()),
            Column<uint>.FromArray(MemoryMarshal.Cast<byte, uint>(small[IndexSection.TypeBaseSizes]).ToArray()),
            Column<uint>.FromArray(MemoryMarshal.Cast<byte, uint>(small[IndexSection.TypeComponentSizes]).ToArray()),
            Column<int>.FromArray(MemoryMarshal.Cast<byte, int>(small[IndexSection.TypeModuleIds]).ToArray()),
            Column<TypeKind>.FromArray(MemoryMarshal.Cast<byte, TypeKind>(small[IndexSection.TypeKinds]).ToArray()),
            strings);
        var largeSizes = MemoryMarshal.Cast<byte, LargeObjectSize>(small[IndexSection.LargeSizes]).ToArray();
        var graph = HeapGraph.FromColumns(
            columns with { LargeSizes = Column<LargeObjectSize>.FromArray(largeSizes) },
            types,
            new RootTable(roots),
            strings,
            document.DroppedReferenceCount,
            document.DroppedRootCount,
            owner,
            Column<DependentHandle>.FromArray(MemoryMarshal.Cast<byte, DependentHandle>(small[IndexSection.DependentHandles]).ToArray()));
        return IndexCacheOpenResult.Opened(graph, document.Metadata, mode, identity);
    }

    /// <summary>Decodes count (int32), count + 1 byte offsets (int32) and UTF-8 bytes; null when inconsistent.</summary>
    internal static StringTable? DecodeStrings(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length < sizeof(int))
        {
            return null;
        }

        var count = MemoryMarshal.Read<int>(span);
        var dataStart = sizeof(int) * ((long)count + 2);
        if (count < 0 || dataStart > span.Length)
        {
            return null;
        }

        var offsets = MemoryMarshal.Cast<byte, int>(span[sizeof(int)..(int)dataStart]);
        var data = span[(int)dataStart..];
        var strings = new string[count];
        for (var i = 0; i < count; i++)
        {
            if (offsets[i] < 0 || offsets[i + 1] < offsets[i] || offsets[i + 1] > data.Length)
            {
                return null;
            }

            strings[i] = Encoding.UTF8.GetString(data[offsets[i]..offsets[i + 1]]);
        }

        return new StringTable(strings);
    }

    /// <summary>
    /// Reads sections into exact-size arrays through the validated handle, verifying each checksum. After the first
    /// failure it allocates nothing more.
    /// </summary>
    private sealed class ArrayReader(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Dictionary<IndexSection, SectionEntry> directory, CancellationToken cancellationToken)
    {
        public IndexCacheOpenResult? Failure { get; private set; }

        public T[] Read<T>(IndexSection section)
            where T : unmanaged
        {
            if (Failure is not null)
            {
                return [];
            }

            var entry = directory[section];
            var array = GC.AllocateUninitializedArray<T>((int)(entry.ByteLength / Unsafe.SizeOf<T>()));

            // Chunks hold whole elements (12 byte roots do not divide a power of two) and stay far below 2 GB.
            var elementsPerChunk = Math.Max(1, ChunkBytes / Unsafe.SizeOf<T>());
            var hash = new XxHash3();
            var fileOffset = entry.Offset;
            for (var start = 0; start < array.Length; start += elementsPerChunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = MemoryMarshal.AsBytes(array.AsSpan(start, Math.Min(elementsPerChunk, array.Length - start)));
                var read = 0;
                while (read < target.Length)
                {
                    var count = RandomAccess.Read(handle, target[read..], fileOffset + read);
                    if (count == 0)
                    {
                        Failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Truncated, $"The index cache ends inside section {section}.");
                        return [];
                    }

                    read += count;
                }

                hash.Append(target);
                fileOffset += target.Length;
            }

            if (hash.GetCurrentHashAsUInt64() != entry.Checksum)
            {
                Failure = IndexFileDirectory.ChecksumFailure(section);
                return [];
            }

            return array;
        }
    }
}
