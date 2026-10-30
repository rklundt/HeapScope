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

using System.Runtime.InteropServices;
using HeapScope.Core;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// The fixed header of a Windows minidump (MINIDUMP_HEADER): signature, time stamp and the MINIDUMP_TYPE flags. Read
/// before ClrMD opens the file, so a file that is not a minidump, or a minidump without the managed heap, fails with a
/// clear message instead of producing an empty or partial graph (sprint 1.2 review).
/// </summary>
public readonly record struct MinidumpHeader(uint Signature, DateTimeOffset? CapturedAtUtc, ulong Flags)
{
    /// <summary>"MDMP" in little-endian.</summary>
    public const uint MinidumpSignature = 0x504D444D;

    /// <summary>MiniDumpWithFullMemory: every readable page of the process.</summary>
    public const ulong WithFullMemory = 0x2;

    /// <summary>MiniDumpWithPrivateReadWriteMemory: private read-write pages, which hold the GC heap (heap dumps).</summary>
    public const ulong WithPrivateReadWriteMemory = 0x200;

    private const int Length = 32;

    /// <summary>True when the dump contains the pages the GC heap lives in.</summary>
    public bool HasHeapMemory => (Flags & (WithFullMemory | WithPrivateReadWriteMemory)) != 0;

    /// <summary>Reads and checks the header.</summary>
    /// <exception cref="SnapshotFormatException">Not a minidump, or a minidump without the managed heap.</exception>
    public static MinidumpHeader Read(string path)
    {
        Span<byte> bytes = stackalloc byte[Length];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = stream.ReadAtLeast(bytes, Length, throwOnEndOfStream: false);
        }

        var signature = read >= 4 ? MemoryMarshal.Read<uint>(bytes) : 0;
        if (read < Length || signature != MinidumpSignature)
        {
            throw new SnapshotFormatException(
                $"{path} is not a Windows memory dump. HeapScope reads full dumps (.dmp) of .NET processes here; gcdumps are read by its gcdump loader.");
        }

        var seconds = MemoryMarshal.Read<uint>(bytes[20..]);
        var header = new MinidumpHeader(signature, seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds), MemoryMarshal.Read<ulong>(bytes[24..]));
        if (!header.HasHeapMemory)
        {
            throw new SnapshotFormatException(
                $"{path} is a minidump without the managed heap, so there are no objects to analyze. Capture a full dump instead: " +
                "dotnet-dump collect --type Full (or Heap), procdump -ma, or Task Manager's \"Create dump file\".");
        }

        return header;
    }

    /// <summary>
    /// True when the file is shorter than its own directory says (sprint 1.8): a stream, or the memory a memory list
    /// describes, lies past the end of the file. A dump cut short while written or copied loses whole memory ranges,
    /// including the GC's own bookkeeping, so its heap can look complete while objects are missing.
    /// </summary>
    /// <remarks>
    /// Each memory list is read once (sprint 3.7, peer review P13): a crafted directory repeating one huge list made the
    /// check quadratic; a real dump has one of each. Cancellable every 64K ranges.
    /// </remarks>
    public static bool IsTruncated(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = (ulong)stream.Length;
        using var reader = new BinaryReader(stream);
        if (stream.Length < Length)
        {
            return true;
        }

        stream.Position = 8;
        var streams = reader.ReadUInt32();
        var directory = reader.ReadUInt32();
        if ((ulong)directory + ((ulong)streams * 12) > length)
        {
            return true;
        }

        var memoryListsRead = new HashSet<uint>();
        for (var i = 0u; i < streams; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = directory + (i * 12);
            var type = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var rva = reader.ReadUInt32();
            if ((ulong)rva + size > length)
            {
                return true;
            }

            // Memory data must lie inside the file too (review M3): the full-memory list keeps it in one block after
            // the descriptors; the plain list (heap dumps without full memory) gives each range its own location. Counts
            // are bounded by the stream's size, sums by the file length, so a damaged directory cannot overflow them.
            if (type is Memory64ListStream or MemoryListStream && !memoryListsRead.Add(type))
            {
                continue;
            }

            if (type == Memory64ListStream && size >= 16)
            {
                stream.Position = rva;
                var ranges = Math.Min(reader.ReadUInt64(), (size - 16) / 16UL);
                var dataRva = reader.ReadUInt64();
                if (dataRva > length)
                {
                    return true;
                }

                var total = 0UL;
                for (var r = 0UL; r < ranges; r++)
                {
                    if ((r & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    reader.ReadUInt64();
                    total += Math.Min(reader.ReadUInt64(), length);
                    if (dataRva + total > length)
                    {
                        return true;
                    }
                }
            }
            else if (type == MemoryListStream && size >= 4)
            {
                stream.Position = rva;
                var ranges = Math.Min(reader.ReadUInt32(), (size - 4) / 16U);
                for (var r = 0u; r < ranges; r++)
                {
                    if ((r & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    reader.ReadUInt64();
                    var dataSize = reader.ReadUInt32();
                    var dataRva = reader.ReadUInt32();
                    if ((ulong)dataRva + dataSize > length)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>MINIDUMP_STREAM_TYPE of the full-memory list.</summary>
    private const uint Memory64ListStream = 9;

    /// <summary>MINIDUMP_STREAM_TYPE of the memory list with a location per range.</summary>
    private const uint MemoryListStream = 5;
}
