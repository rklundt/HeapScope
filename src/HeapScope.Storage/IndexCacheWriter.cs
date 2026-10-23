using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HeapScope.Core.Graph;

namespace HeapScope.Storage;

/// <summary>
/// Writes a <see cref="HeapGraph"/> to an index cache file (ARCHITECTURE.md 7). The file is written to a temporary
/// file in the target folder: sections first (page aligned, in the in-memory layout, each with an XxHash3 checksum),
/// then the header last, then it is flushed to disk and renamed over the target. A process that dies at any point
/// leaves either the previous cache or a temporary file without a valid header, never a file that opens.
/// </summary>
public static class IndexCacheWriter
{
    /// <summary>Elements per write and hash chunk, so no span exceeds 2 GB of bytes.</summary>
    private const int ChunkElements = 1 << 24;

    /// <summary>Temporary files older than this are considered left over by a crashed writer.</summary>
    public static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromMinutes(10);

    private static readonly AsyncLocal<Action<string>?> s_beforeHeaderForTesting = new();

    /// <summary>
    /// Test hook: runs after all sections are written and before the header is written. Flow-local, so it only affects
    /// writes started by the test that sets it (tests run in parallel).
    /// </summary>
    internal static Action<string>? BeforeHeaderForTesting
    {
        get => s_beforeHeaderForTesting.Value;
        set => s_beforeHeaderForTesting.Value = value;
    }

    /// <summary>
    /// Writes <paramref name="graph"/> to <paramref name="path"/>. Progress reports bytes written. Throws
    /// <see cref="IndexCacheInUseException"/> when the existing cache is open (for example mapped); the previous cache
    /// is then kept and no temporary file remains.
    /// </summary>
    public static void Write(
        string path,
        HeapGraph graph,
        IndexCacheMetadata metadata,
        SnapshotFingerprint fingerprint,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(metadata);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        DeleteStaleTemporaryFiles(directory, Path.GetFileName(fullPath));
        var temp = Path.Combine(directory, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
            {
                var writer = new SectionWriter(stream, progress, cancellationToken);
                writer.Write(IndexSection.Addresses, graph.Addresses.Span);
                writer.Write(IndexSection.TypeIndices, graph.TypeIndices.Span);
                writer.Write(IndexSection.Sizes, graph.Sizes.Span);
                writer.Write(IndexSection.HeapKinds, graph.HeapKinds.Span);
                writer.Write(IndexSection.ObjectFlags, graph.Flags.Span);
                writer.Write(IndexSection.EdgeStarts, graph.EdgeStarts.Span);
                writer.Write(IndexSection.Edges, graph.Edges.Span);
                writer.Write(IndexSection.LargeSizes, graph.LargeSizes.Span);
                writer.Write(IndexSection.Roots, graph.Roots.Roots.Span);
                writer.Write(IndexSection.TypeNameIds, graph.Types.NameIds.Span);
                writer.Write(IndexSection.TypeMethodTables, graph.Types.MethodTables.Span);
                writer.Write(IndexSection.TypeBaseSizes, graph.Types.BaseSizes.Span);
                writer.Write(IndexSection.TypeComponentSizes, graph.Types.ComponentSizes.Span);
                writer.Write(IndexSection.TypeModuleIds, graph.Types.ModuleIds.Span);
                writer.Write(IndexSection.TypeKinds, graph.Types.Kinds.Span);
                writer.Write<byte>(IndexSection.Strings, EncodeStrings(graph.Strings));
                writer.Write<byte>(IndexSection.Metadata, JsonSerializer.SerializeToUtf8Bytes(
                    new MetadataDocument(metadata, graph.DroppedReferenceCount, graph.DroppedRootCount)));

                stream.Flush();
                BeforeHeaderForTesting?.Invoke(temp);
                WriteHeader(stream, fingerprint, writer.Entries);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, fullPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IndexCacheInUseException($"The index cache {fullPath} is in use and cannot be replaced; close the snapshot first.", ex);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Encodes strings as: count (int32), count + 1 byte offsets (int32), UTF-8 bytes.</summary>
    internal static byte[] EncodeStrings(StringTable strings)
    {
        var encoded = new byte[strings.Count][];
        var total = 0;
        for (var i = 0; i < strings.Count; i++)
        {
            encoded[i] = Encoding.UTF8.GetBytes(strings[i]!);
            total += encoded[i].Length;
        }

        var result = new byte[sizeof(int) * (strings.Count + 2) + total];
        var span = result.AsSpan();
        MemoryMarshal.Write(span, strings.Count);
        var offset = 0;
        var data = sizeof(int) * (strings.Count + 2);
        for (var i = 0; i < strings.Count; i++)
        {
            MemoryMarshal.Write(span[(sizeof(int) * (i + 1))..], offset);
            encoded[i].CopyTo(span[(data + offset)..]);
            offset += encoded[i].Length;
        }

        MemoryMarshal.Write(span[(sizeof(int) * (strings.Count + 1))..], offset);
        return result;
    }

    private static void WriteHeader(FileStream stream, SnapshotFingerprint fingerprint, List<SectionEntry> entries)
    {
        var region = new byte[IndexCacheFormat.PageSize];
        var header = new FileHeader
        {
            Magic = IndexCacheFormat.Magic,
            FormatVersion = IndexCacheFormat.Version,
            HeaderRegionLength = IndexCacheFormat.PageSize,
            SectionCount = entries.Count,
            SourceLength = fingerprint.Length,
            SourceLastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
            SourceContentHash = fingerprint.ContentHash,
        };
        var headerSize = Unsafe.SizeOf<FileHeader>();
        var directory = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(entries));
        MemoryMarshal.Write(region, in header);
        directory.CopyTo(region.AsSpan(headerSize));
        header.HeaderChecksum = XxHash3.HashToUInt64(region.AsSpan(0, headerSize + directory.Length));
        MemoryMarshal.Write(region, in header);

        stream.Position = 0;
        stream.Write(region);
    }

    /// <summary>
    /// Removes temporary files of this cache left by a process that died while writing (they can be gigabytes). Only
    /// files older than <see cref="StaleTemporaryAge"/> are removed, so a concurrent writer is never disturbed.
    /// </summary>
    private static void DeleteStaleTemporaryFiles(string directory, string cacheName)
    {
        foreach (var stale in Directory.EnumerateFiles(directory, cacheName + ".*.tmp"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > StaleTemporaryAge)
            {
                TryDelete(stale);
            }
        }
    }

    private static void TryDelete(string path)
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

    private sealed class SectionWriter(FileStream stream, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        private long _position = IndexCacheFormat.PageSize;

        public List<SectionEntry> Entries { get; } = [];

        public void Write<T>(IndexSection section, ReadOnlySpan<T> data)
            where T : unmanaged
        {
            cancellationToken.ThrowIfCancellationRequested();
            _position = IndexCacheFormat.Align(_position);
            stream.Position = _position;
            var hash = new XxHash3();
            for (var start = 0; start < data.Length; start += ChunkElements)
            {
                var bytes = MemoryMarshal.AsBytes(data.Slice(start, Math.Min(ChunkElements, data.Length - start)));
                stream.Write(bytes);
                hash.Append(bytes);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var elementSize = Unsafe.SizeOf<T>();
            var byteLength = (long)data.Length * elementSize;
            Entries.Add(new SectionEntry
            {
                Id = (int)section,
                ElementSize = elementSize,
                Offset = _position,
                ByteLength = byteLength,
                ElementCount = data.Length,
                Checksum = hash.GetCurrentHashAsUInt64(),
            });
            _position += byteLength;
            progress?.Report(_position);
        }
    }
}
