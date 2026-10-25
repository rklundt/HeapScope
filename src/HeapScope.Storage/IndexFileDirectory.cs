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

namespace HeapScope.Storage;

/// <summary>
/// Reads and validates the header and section directory of an index file (a base cache or a sidecar, ARCHITECTURE.md 7)
/// against its <see cref="IndexFileLayout"/>: magic, version, header checksum, every directory entry (known ids of the
/// layout only, no repeats, element size, counts, page-aligned offsets past the header, overflow-safe bounds against the
/// file length), every section of the layout present, the fingerprint and the base identity. Nothing past the header is
/// read here.
/// </summary>
internal static class IndexFileDirectory
{
    private const int VerifyBufferBytes = 4 << 20;

    /// <summary>The directory by section, or null with <paramref name="failure"/> set.</summary>
    /// <param name="expectedBaseIdentity">The base cache a sidecar must extend; 0 for a base cache (whose field must be 0).</param>
    public static Dictionary<IndexSection, SectionEntry>? Read(
        FileStream stream,
        IndexFileLayout layout,
        SnapshotFingerprint? expected,
        ulong expectedBaseIdentity,
        out FileHeader header,
        out IndexCacheOpenResult? failure)
    {
        header = default;
        var fileLength = stream.Length;
        if (fileLength < IndexCacheFormat.PageSize)
        {
            failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Truncated, "The index cache is shorter than its header.");
            return null;
        }

        var region = new byte[IndexCacheFormat.PageSize];
        stream.Position = 0;
        stream.ReadExactly(region);
        header = MemoryMarshal.Read<FileHeader>(region);
        if (header.Magic != IndexCacheFormat.Magic)
        {
            failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.BadMagic, "The file is not a complete HeapScope index cache (it may have been interrupted while being written).");
            return null;
        }

        if (header.FormatVersion != IndexCacheFormat.Version)
        {
            failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.VersionMismatch, $"The index cache has format version {header.FormatVersion}; this HeapScope reads version {IndexCacheFormat.Version}.");
            return null;
        }

        var headerSize = Unsafe.SizeOf<FileHeader>();
        var entrySize = Unsafe.SizeOf<SectionEntry>();
        var maxSections = (IndexCacheFormat.PageSize - headerSize) / entrySize;
        if (header.HeaderRegionLength != IndexCacheFormat.PageSize || header.SectionCount < 0 || header.SectionCount > maxSections)
        {
            failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.HeaderCorrupted, "The index cache header is damaged.");
            return null;
        }

        var directoryBytes = header.SectionCount * entrySize;
        var unchecked_ = header;
        unchecked_.HeaderChecksum = 0;
        MemoryMarshal.Write(region, in unchecked_);
        if (XxHash3.HashToUInt64(region.AsSpan(0, headerSize + directoryBytes)) != header.HeaderChecksum)
        {
            failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.HeaderCorrupted, "The index cache header is damaged (checksum mismatch).");
            return null;
        }

        // Every entry is validated, known or not, with overflow-safe arithmetic, before any offset is used.
        var directory = new Dictionary<IndexSection, SectionEntry>();
        foreach (var entry in MemoryMarshal.Cast<byte, SectionEntry>(region.AsSpan(headerSize, directoryBytes)))
        {
            var section = (IndexSection)entry.Id;
            if (!layout.ElementSizes.TryGetValue(section, out var elementSize) || !directory.TryAdd(section, entry))
            {
                failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, $"The index cache directory has an unknown or repeated section id {entry.Id}.");
                return null;
            }

            if (entry.ElementSize != elementSize || entry.ElementCount < 0 || entry.ElementCount > int.MaxValue
                || entry.ByteLength != entry.ElementCount * elementSize
                || entry.Offset < IndexCacheFormat.PageSize || entry.Offset % IndexCacheFormat.PageSize != 0)
            {
                failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, $"The index cache section {section} is malformed.");
                return null;
            }

            if (entry.Offset > fileLength || entry.ByteLength > fileLength - entry.Offset)
            {
                failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Truncated, $"The index cache ends inside section {section}.");
                return null;
            }
        }

        if (directory.Count != layout.ElementSizes.Count)
        {
            var missing = layout.ElementSizes.Keys.First(s => !directory.ContainsKey(s));
            failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.Malformed, $"The index cache section {missing} is missing.");
            return null;
        }

        if (expected is { } fingerprint)
        {
            var built = new SnapshotFingerprint(header.SourceLength, header.SourceLastWriteUtcTicks, header.SourceContentHash);
            if (built.DescribeDifference(fingerprint) is { } difference)
            {
                failure = IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.FingerprintMismatch, $"The index cache is out of date: {difference}.");
                return null;
            }
        }

        if (header.SemanticsVersion != layout.SemanticsVersion)
        {
            failure = IndexCacheOpenResult.Invalid(
                IndexCacheInvalidReason.VersionMismatch,
                $"The {layout.Name} index was computed under rules version {header.SemanticsVersion}; this HeapScope uses version {layout.SemanticsVersion}.");
            return null;
        }

        if (header.BaseIdentity != expectedBaseIdentity)
        {
            failure = IndexCacheOpenResult.Invalid(
                IndexCacheInvalidReason.BaseMismatch,
                expectedBaseIdentity == 0
                    ? "The file is an optional index of a snapshot, not its index cache."
                    : $"The {layout.Name} index was built for another version of the snapshot's index cache.");
            return null;
        }

        failure = null;
        return directory;
    }

    /// <summary>Hashes every section with sequential reads (pooled buffer); returns the failure of the first corrupted one.</summary>
    public static IndexCacheOpenResult? VerifySequentially(FileStream stream, Dictionary<IndexSection, SectionEntry> directory, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(VerifyBufferBytes);
        try
        {
            foreach (var entry in directory.Values.OrderBy(e => e.Offset))
            {
                var hash = new XxHash3();
                stream.Position = entry.Offset;
                for (var remaining = entry.ByteLength; remaining > 0;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining));
                    stream.ReadExactly(chunk);
                    hash.Append(chunk);
                    remaining -= chunk.Length;
                }

                if (hash.GetCurrentHashAsUInt64() != entry.Checksum)
                {
                    return ChecksumFailure((IndexSection)entry.Id);
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static IndexCacheOpenResult ChecksumFailure(IndexSection section) =>
        IndexCacheOpenResult.Invalid(IndexCacheInvalidReason.ChecksumMismatch, $"The index cache section {section} is corrupted (checksum mismatch).");
}
