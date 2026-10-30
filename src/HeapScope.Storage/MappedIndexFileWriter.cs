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
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using HeapScope.Core;

namespace HeapScope.Storage;

/// <summary>A section of a <see cref="MappedIndexFileWriter"/>: its id, element size and element count.</summary>
public readonly record struct PlannedSection(IndexSection Section, int ElementSize, int ElementCount);

/// <summary>
/// Writes an index file whose sections are computed in place (sprint 2.3, the file-backed column allocator of
/// ARCHITECTURE.md 6.3, from the 2.1 review M2): the sections are planned up front, a temporary file of the final size is
/// mapped read-write, and the caller fills each section through <see cref="GetSection{T}"/>. The pages written belong to
/// the file, not to the process, so the OS can write them out under memory pressure and the computation holds no managed
/// copy of its results. <see cref="Commit"/> unmaps the file, hashes the sections, writes the header last, flushes and
/// renames, with the crash safety of <see cref="IndexCacheFileWriter"/>; disposing an uncommitted writer deletes the
/// temporary file.
/// </summary>
public sealed unsafe class MappedIndexFileWriter : IDisposable
{
    private const int HashBufferBytes = 4 << 20;

    private readonly string _path;
    private readonly string _temp;
    private readonly IndexCacheSigner _signer;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<IndexSection, SectionEntry> _sections = [];
    private FileStream? _stream;
    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private byte* _pointer;
    private bool _committed;

    private MappedIndexFileWriter(string path, string temp, IndexCacheSigner signer, CancellationToken cancellationToken)
    {
        _signer = signer;
        _path = path;
        _temp = temp;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The file's size: the header region and every section, each starting on a page.</summary>
    public long Length { get; private set; }

    /// <summary>
    /// Creates the temporary file for <paramref name="path"/> with room for <paramref name="sections"/> (each id once) and
    /// maps it read-write. Temporary files of this file left by a writer that died are removed first.
    /// </summary>
    public static MappedIndexFileWriter Create(string path, IndexCacheSigner signer, IReadOnlyList<PlannedSection> sections, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(sections);
        var (fullPath, temp) = IndexCacheFileWriter.PrepareTemporary(path);
        var writer = new MappedIndexFileWriter(fullPath, temp, signer, cancellationToken);
        try
        {
            writer.Map(sections);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>The section's bytes as <typeparamref name="T"/>, writable until <see cref="Commit"/> or <see cref="Dispose"/>.</summary>
    public Span<T> GetSection<T>(IndexSection section)
        where T : unmanaged
    {
        if (_pointer is null)
        {
            throw new InvalidOperationException("The index file was already committed or disposed.");
        }

        if (!_sections.TryGetValue(section, out var entry) || entry.ElementSize != Unsafe.SizeOf<T>())
        {
            throw new ArgumentException($"The section {section} of {typeof(T).Name} elements was not planned.", nameof(section));
        }

        return new Span<T>(_pointer + entry.Offset, (int)entry.ElementCount);
    }

    /// <summary>
    /// Unmaps the file, hashes every section, writes the header last, flushes to disk and renames the file over the target.
    /// Returns the file's identity. Spans from <see cref="GetSection{T}"/> must not be used afterwards.
    /// </summary>
    public ulong Commit(SnapshotFingerprint fingerprint, ulong baseIdentity = 0, int semanticsVersion = 0)
    {
        var stream = _stream ?? throw new InvalidOperationException("The index file was already committed or disposed.");
        _cancellationToken.ThrowIfCancellationRequested();
        Unmap();
        var entries = _sections.Values.OrderBy(e => e.Offset).ToList();
        ulong identity;
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(HashBufferBytes);
            try
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    var hash = new XxHash3();
                    stream.Position = entry.Offset;
                    for (var remaining = entry.ByteLength; remaining > 0;)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var chunk = buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining));
                        stream.ReadExactly(chunk);
                        hash.Append(chunk);
                        remaining -= chunk.Length;
                    }

                    entry.Checksum = hash.GetCurrentHashAsUInt64();
                    entries[i] = entry;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            IndexCacheWriter.BeforeHeaderForTesting?.Invoke(_temp);
            identity = IndexCacheFileWriter.WriteHeader(stream, _signer, entries, fingerprint, baseIdentity, semanticsVersion);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            stream.Dispose();
            _stream = null;
        }

        IndexCacheFileWriter.MoveOver(_temp, _path);
        _committed = true;
        return identity;
    }

    /// <summary>Unmaps and deletes the temporary file unless the write was committed.</summary>
    public void Dispose()
    {
        Unmap();
        _stream?.Dispose();
        _stream = null;
        if (!_committed)
        {
            IndexCacheFileWriter.TryDelete(_temp);
        }
    }

    private void Map(IReadOnlyList<PlannedSection> sections)
    {
        long position = IndexCacheFormat.PageSize;
        foreach (var planned in sections)
        {
            if (planned.ElementSize <= 0 || planned.ElementCount < 0)
            {
                throw new ArgumentException($"The section {planned.Section} has no valid size.", nameof(sections));
            }

            position = IndexCacheFormat.Align(position);
            var entry = new SectionEntry
            {
                Id = (int)planned.Section,
                ElementSize = planned.ElementSize,
                Offset = position,
                ByteLength = (long)planned.ElementCount * planned.ElementSize,
                ElementCount = planned.ElementCount,
            };
            if (!_sections.TryAdd(planned.Section, entry))
            {
                throw new ArgumentException($"The section {planned.Section} is planned twice.", nameof(sections));
            }

            position += entry.ByteLength;
        }

        // The file ends at the last section's end (an empty last section's offset included), as a streamed file does.
        Length = position;
        // The space is reserved before mapping: a mapped write past the free space would kill the worker (SIGBUS) on Linux
        // and macOS, where setting the length alone leaves a sparse file (sprint 2.3.6 review).
        _stream = PrivateFiles.Open(_temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1, preallocationSize: Length);
        _stream.SetLength(Length);
        _file = MemoryMappedFile.CreateFromFile(_stream, mapName: null, Length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        _view = _file.CreateViewAccessor(0, Length, MemoryMappedFileAccess.ReadWrite);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer + _view.PointerOffset;
    }

    private void Unmap()
    {
        if (_view is { } view)
        {
            // No FlushViewOfFile: the written pages stay in the file's cache, where the hashing reads them, and the final
            // flush to disk covers them.
            if (_pointer is not null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                _pointer = null;
            }

            view.Dispose();
            _view = null;
        }

        _file?.Dispose();
        _file = null;
    }
}
