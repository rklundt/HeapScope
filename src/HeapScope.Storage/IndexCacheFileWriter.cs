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

namespace HeapScope.Storage;

/// <summary>
/// Writes one index file (a base cache or a sidecar, ARCHITECTURE.md 7) section by section (sprint 2.1). Sections go to a
/// temporary file in the target folder, each page aligned with an XxHash3 checksum; <see cref="Commit"/> writes the header
/// last, flushes to disk and renames the file over the target. Disposing a writer that was not committed deletes the
/// temporary file, so a canceled or failed write leaves the previous file and nothing else.
/// </summary>
public sealed class IndexCacheFileWriter : IDisposable
{
    /// <summary>Elements per write and hash chunk, so no span exceeds 2 GB of bytes.</summary>
    private const int ChunkElements = 1 << 24;

    private readonly string _path;
    private readonly string _temp;
    private readonly IProgress<long>? _progress;
    private readonly CancellationToken _cancellationToken;
    private readonly List<SectionEntry> _entries = [];
    private FileStream? _stream;
    private long _position = IndexCacheFormat.PageSize;
    private bool _committed;

    private IndexCacheFileWriter(string path, string temp, FileStream stream, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        _path = path;
        _temp = temp;
        _stream = stream;
        _progress = progress;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Starts writing the file at <paramref name="path"/>. Temporary files of this file older than
    /// <see cref="IndexCacheWriter.StaleTemporaryAge"/> (left by a writer that died) are removed first. Progress reports
    /// bytes written.
    /// </summary>
    public static IndexCacheFileWriter Create(string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var (fullPath, temp) = PrepareTemporary(path);
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        return new IndexCacheFileWriter(fullPath, temp, stream, progress, cancellationToken);
    }

    /// <summary>Appends a section; each section id may be written once.</summary>
    public void WriteSection<T>(IndexSection section, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        var stream = _stream ?? throw new InvalidOperationException("The index file was already committed or disposed.");
        if (_entries.Exists(e => e.Id == (int)section))
        {
            throw new ArgumentException($"The section {section} was already written.", nameof(section));
        }

        _cancellationToken.ThrowIfCancellationRequested();
        _position = IndexCacheFormat.Align(_position);
        stream.Position = _position;
        var hash = new XxHash3();
        for (var start = 0; start < data.Length; start += ChunkElements)
        {
            var bytes = MemoryMarshal.AsBytes(data.Slice(start, Math.Min(ChunkElements, data.Length - start)));
            stream.Write(bytes);
            hash.Append(bytes);
            _cancellationToken.ThrowIfCancellationRequested();
        }

        var elementSize = Unsafe.SizeOf<T>();
        var byteLength = (long)data.Length * elementSize;
        _entries.Add(new SectionEntry
        {
            Id = (int)section,
            ElementSize = elementSize,
            Offset = _position,
            ByteLength = byteLength,
            ElementCount = data.Length,
            Checksum = hash.GetCurrentHashAsUInt64(),
        });
        _position += byteLength;
        _progress?.Report(_position);
    }

    /// <summary>
    /// Writes the header, flushes to disk and renames the file over the target. Returns the file's identity (its header
    /// checksum, which covers every section checksum). <paramref name="baseIdentity"/> names the base cache a sidecar
    /// extends; 0 for a base cache. Throws <see cref="IndexCacheInUseException"/> when the existing file is open (for
    /// example mapped); the previous file is then kept and, once the writer is disposed, no temporary file remains.
    /// <paramref name="semanticsVersion"/> is the layout's <see cref="IndexFileLayout.SemanticsVersion"/>.
    /// </summary>
    public ulong Commit(SnapshotFingerprint fingerprint, ulong baseIdentity = 0, int semanticsVersion = 0)
    {
        var stream = _stream ?? throw new InvalidOperationException("The index file was already committed or disposed.");
        _cancellationToken.ThrowIfCancellationRequested();
        ulong identity;
        try
        {
            // An empty last section is aligned past the bytes written; the file must reach every section's offset.
            if (stream.Length < _position)
            {
                stream.SetLength(_position);
            }

            stream.Flush();
            IndexCacheWriter.BeforeHeaderForTesting?.Invoke(_temp);
            identity = WriteHeader(stream, _entries, fingerprint, baseIdentity, semanticsVersion);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            stream.Dispose();
            _stream = null;
        }

        MoveOver(_temp, _path);
        _committed = true;
        return identity;
    }

    /// <summary>Renames a written temporary file over the target; an open target is <see cref="IndexCacheInUseException"/>.</summary>
    internal static void MoveOver(string temp, string path)
    {
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IndexCacheInUseException($"The index cache {path} is in use and cannot be replaced; close the snapshot first.", ex);
        }
    }

    /// <summary>
    /// Prepares a temporary file name next to <paramref name="path"/>, after removing stale temporary files of it; returns
    /// the full target path and the temporary path.
    /// </summary>
    internal static (string FullPath, string Temp) PrepareTemporary(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        DeleteStaleTemporaryFiles(directory, Path.GetFileName(fullPath));
        return (fullPath, Path.Combine(directory, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp"));
    }

    /// <summary>Deletes the temporary file unless the write was committed.</summary>
    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        if (!_committed)
        {
            TryDelete(_temp);
        }
    }

    /// <summary>Writes the header region (header and section directory) at offset 0; returns the header checksum.</summary>
    internal static ulong WriteHeader(FileStream stream, List<SectionEntry> entries, SnapshotFingerprint fingerprint, ulong baseIdentity, int semanticsVersion)
    {
        var region = new byte[IndexCacheFormat.PageSize];
        var header = new FileHeader
        {
            Magic = IndexCacheFormat.Magic,
            FormatVersion = IndexCacheFormat.Version,
            HeaderRegionLength = IndexCacheFormat.PageSize,
            SectionCount = entries.Count,
            SemanticsVersion = semanticsVersion,
            SourceLength = fingerprint.Length,
            SourceLastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
            SourceContentHash = fingerprint.ContentHash,
            BaseIdentity = baseIdentity,
        };
        var headerSize = Unsafe.SizeOf<FileHeader>();
        var directory = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(entries));
        MemoryMarshal.Write(region, in header);
        directory.CopyTo(region.AsSpan(headerSize));
        header.HeaderChecksum = XxHash3.HashToUInt64(region.AsSpan(0, headerSize + directory.Length));
        MemoryMarshal.Write(region, in header);

        stream.Position = 0;
        stream.Write(region);
        return header.HeaderChecksum;
    }

    /// <summary>
    /// Removes temporary files of this file left by a process that died while writing (they can be gigabytes). Only files
    /// older than <see cref="IndexCacheWriter.StaleTemporaryAge"/> are removed, so a concurrent writer is never disturbed.
    /// </summary>
    private static void DeleteStaleTemporaryFiles(string directory, string fileName)
    {
        foreach (var stale in Directory.EnumerateFiles(directory, fileName + ".*.tmp"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > IndexCacheWriter.StaleTemporaryAge)
            {
                TryDelete(stale);
            }
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
