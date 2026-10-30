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

using System.Buffers;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using HeapScope.Core.Graph;

namespace HeapScope.Storage;

/// <summary>The outcome of opening an index file with <see cref="IndexCacheFile.Open"/>; invalid files report a reason.</summary>
public sealed record IndexFileOpenResult(IndexCacheFile? File, IndexCacheInvalidReason Reason, string? Detail)
{
    public bool Success => File is not null;
}

/// <summary>
/// An opened index file of any <see cref="IndexFileLayout"/> (sprint 2.1; the optional indices' sidecars), validated like
/// the base cache before it is returned: header, directory, fingerprint, base identity and every section checksum. In
/// memory, each section is read once into an array of its element size (so a section holds up to 2^31 - 1 elements, like
/// the graph's columns, review M1) and verified in the same pass; mapped, the sections are verified with sequential reads
/// and then handed out as views of a read-only mapping. Disposing releases the mapping, so columns
/// must not be used after it (the owner disposes it with the graph, after the last lease: <see cref="HeapGraph.Attach"/>).
/// </summary>
public sealed class IndexCacheFile : IDisposable
{
    private const int ChunkBytes = 1 << 26;

    private readonly Dictionary<IndexSection, SectionEntry> _directory;
    private readonly Dictionary<IndexSection, Array>? _arrays;
    private readonly MappedIndexFile? _mapping;

    private IndexCacheFile(FileHeader header, Dictionary<IndexSection, SectionEntry> directory, MemoryMode mode, Dictionary<IndexSection, Array>? arrays, MappedIndexFile? mapping)
    {
        _directory = directory;
        _arrays = arrays;
        _mapping = mapping;
        Mode = mode;
        Identity = header.HeaderChecksum;
        BaseIdentity = header.BaseIdentity;
        Fingerprint = new SnapshotFingerprint(header.SourceLength, header.SourceLastWriteUtcTicks, header.SourceContentHash);
    }

    public MemoryMode Mode { get; }

    /// <summary>The file's identity (its header checksum, which covers every section checksum).</summary>
    public ulong Identity { get; }

    /// <summary>The identity of the base cache this file extends; 0 for a base cache.</summary>
    public ulong BaseIdentity { get; }

    /// <summary>The fingerprint of the snapshot the file was built from.</summary>
    public SnapshotFingerprint Fingerprint { get; }

    /// <summary>Bytes held by the sections (in memory, or mapped).</summary>
    public long SectionBytes => _directory.Values.Sum(e => e.ByteLength);

    /// <summary>
    /// Opens the file at <paramref name="path"/>, which must have <paramref name="layout"/>'s sections, extend the base
    /// cache <paramref name="baseIdentity"/> (0 for a base cache) and, when given, come from a snapshot with fingerprint
    /// <paramref name="expected"/>. Never throws for a damaged, truncated or hostile file.
    /// </summary>
    public static IndexFileOpenResult Open(
        string path,
        IndexCacheSigner signer,
        IndexFileLayout layout,
        ulong baseIdentity,
        SnapshotFingerprint? expected,
        MemoryMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(layout);
        if (!System.IO.File.Exists(path))
        {
            return new IndexFileOpenResult(null, IndexCacheInvalidReason.Missing, $"No {layout.Name} index at {path}.");
        }

        FileStream? stream = null;
        try
        {
            // FileShare.Read: nobody can write, rename over or delete the file while it is being read or mapped.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
            var directory = IndexFileDirectory.Read(stream, signer, layout, expected, baseIdentity, out var header, out var failure);
            if (directory is null)
            {
                return Failed(failure!);
            }

            if (mode == MemoryMode.InMemory)
            {
                var arrays = ReadInMemory(stream, directory, out var corrupted, cancellationToken);
                return arrays is null
                    ? Failed(corrupted!)
                    : new IndexFileOpenResult(new IndexCacheFile(header, directory, mode, arrays, null), IndexCacheInvalidReason.None, null);
            }

            if (IndexFileDirectory.VerifySequentially(stream, directory, cancellationToken) is { } bad)
            {
                return Failed(bad);
            }

            var mapping = new MappedIndexFile(stream);
            stream = null; // Owned by the mapping now.
            if (directory.Values.Any(e => e.Offset > mapping.Length || e.ByteLength > mapping.Length - e.Offset))
            {
                mapping.Dispose();
                return new IndexFileOpenResult(null, IndexCacheInvalidReason.Truncated, $"The {layout.Name} index mapping ends inside a section.");
            }

            return new IndexFileOpenResult(new IndexCacheFile(header, directory, mode, null, mapping), IndexCacheInvalidReason.None, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IndexFileOpenResult(null, IndexCacheInvalidReason.IoError, $"The {layout.Name} index could not be read: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or JsonException or InvalidDataException)
        {
            return new IndexFileOpenResult(null, IndexCacheInvalidReason.Malformed, $"The {layout.Name} index is malformed: {ex.Message}");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// The section as a column of <typeparamref name="T"/>; false when the file has no such section or its element size
    /// differs from <typeparamref name="T"/>'s.
    /// </summary>
    public unsafe bool TryGetSection<T>(IndexSection section, out Column<T> column)
        where T : unmanaged
    {
        column = Column<T>.Empty;
        if (!_directory.TryGetValue(section, out var entry) || entry.ElementSize != Unsafe.SizeOf<T>())
        {
            return false;
        }

        if (entry.ElementCount == 0)
        {
            return true;
        }

        if (_arrays is null)
        {
            // Mapped: _arrays is null exactly when the file was opened mapped.
            column = new Column<T>(new MappedMemoryManager<T>(_mapping!.Pointer + entry.Offset, (int)entry.ElementCount).Memory);
            return true;
        }

        column = _arrays[section] switch
        {
            byte[] bytes => new Column<T>(new ArrayMemoryManager<byte, T>(bytes).Memory),
            ushort[] shorts => new Column<T>(new ArrayMemoryManager<ushort, T>(shorts).Memory),
            int[] ints => new Column<T>(new ArrayMemoryManager<int, T>(ints).Memory),
            long[] longs => new Column<T>(new ArrayMemoryManager<long, T>(longs).Memory),
            _ => throw new InvalidOperationException($"Unexpected array for section {section}."),
        };
        return true;
    }

    /// <summary>Releases the mapping (mapped mode); columns obtained from this file must not be used afterwards.</summary>
    public void Dispose() => _mapping?.Dispose();

    private static IndexFileOpenResult Failed(IndexCacheOpenResult failure) => new(null, failure.Reason, failure.Detail);

    /// <summary>
    /// Reads every section into an array of its element size (byte, ushort, int or long; other sizes as bytes), verifying
    /// checksums in the same pass; null after the first failure (nothing more allocated).
    /// </summary>
    private static Dictionary<IndexSection, Array>? ReadInMemory(FileStream stream, Dictionary<IndexSection, SectionEntry> directory, out IndexCacheOpenResult? failure, CancellationToken cancellationToken)
    {
        var arrays = new Dictionary<IndexSection, Array>();
        foreach (var (section, entry) in directory)
        {
            var count = (int)entry.ElementCount;
            Array? array = entry.ElementSize switch
            {
                sizeof(ushort) => GC.AllocateUninitializedArray<ushort>(count),
                sizeof(int) => GC.AllocateUninitializedArray<int>(count),
                sizeof(long) => GC.AllocateUninitializedArray<long>(count),
                _ when entry.ByteLength <= Array.MaxLength => GC.AllocateUninitializedArray<byte>((int)entry.ByteLength),
                _ => null,
            };
            if (array is null)
            {
                failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, $"The index file section {section} is too large to read into memory; open it mapped.");
                return null;
            }

            var read = array switch
            {
                ushort[] shorts => ReadSection(stream, entry, shorts, cancellationToken),
                int[] ints => ReadSection(stream, entry, ints, cancellationToken),
                long[] longs => ReadSection(stream, entry, longs, cancellationToken),
                _ => ReadSection(stream, entry, (byte[])array, cancellationToken),
            };
            if (read is { } bad)
            {
                failure = bad == IndexCacheInvalidReason.Truncated
                    ? IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Truncated, $"The index file ends inside section {section}.")
                    : IndexFileDirectory.ChecksumFailure(section);
                return null;
            }

            arrays[section] = array;
        }

        failure = null;
        return arrays;
    }

    /// <summary>Reads one section into <paramref name="array"/> in chunks of whole elements (no span over 2 GB of bytes) and checks its hash.</summary>
    private static IndexCacheInvalidReason? ReadSection<TElement>(FileStream stream, SectionEntry entry, TElement[] array, CancellationToken cancellationToken)
        where TElement : unmanaged
    {
        var elementsPerChunk = Math.Max(1, ChunkBytes / Unsafe.SizeOf<TElement>());
        var hash = new XxHash3();
        var fileOffset = entry.Offset;
        for (var start = 0; start < array.Length; start += elementsPerChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = MemoryMarshal.AsBytes(array.AsSpan(start, Math.Min(elementsPerChunk, array.Length - start)));
            var read = 0;
            while (read < target.Length)
            {
                var count = RandomAccess.Read(stream.SafeFileHandle, target[read..], fileOffset + read);
                if (count == 0)
                {
                    return IndexCacheInvalidReason.Truncated;
                }

                read += count;
            }

            hash.Append(target);
            fileOffset += target.Length;
        }

        return hash.GetCurrentHashAsUInt64() == entry.Checksum ? null : IndexCacheInvalidReason.ChecksumMismatch;
    }

    /// <summary>An array of <typeparamref name="TSource"/> seen as <typeparamref name="T"/> elements of the same size; it keeps the array alive while in use.</summary>
    private sealed unsafe class ArrayMemoryManager<TSource, T>(TSource[] array) : MemoryManager<T>
        where TSource : unmanaged
        where T : unmanaged
    {
        public override Span<T> GetSpan() => MemoryMarshal.Cast<TSource, T>(array.AsSpan());

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            return new MemoryHandle((T*)handle.AddrOfPinnedObject() + elementIndex, handle, this);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
