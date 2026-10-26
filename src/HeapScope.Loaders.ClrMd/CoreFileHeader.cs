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


using System.Buffers.Binary;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// The truncation check of a Linux or macOS core file (sprint 2.3.5 review H2), the counterpart of
/// <see cref="MinidumpHeader.IsTruncated"/>: a core cut short by a size limit (<c>ulimit -c</c>, systemd-coredump) or an
/// interrupted copy keeps its headers but loses the memory at its end, on Linux x64 typically the thread stacks, so roots
/// go missing without any error from ClrMD.
/// </summary>
public static class CoreFileHeader
{
    private const uint ElfLoad = 1;
    private const uint ElfNote = 4;
    private const ushort ElfExtendedCount = 0xFFFF;
    private const int ElfProgramHeaderSize = 56;
    private const uint MachOSegment64 = 0x19;
    private const int MachOHeaderSize = 32;

    /// <summary>
    /// True when the file is shorter than its own headers say: the program header table (ELF) or the load commands
    /// (Mach-O) run past the end, or a loaded segment's or note's file range does. A file that is neither a 64-bit
    /// little-endian ELF core nor a 64-bit Mach-O core is reported truncated, so it never claims complete answers.
    /// </summary>
    public static bool IsTruncated(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[64];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
        {
            return true;
        }

        if (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' && header[4] == 2 && header[5] == 1)
        {
            return IsElfTruncated(stream, header);
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xFEEDFACF || IsMachOTruncated(stream, header);
    }

    private static bool IsElfTruncated(FileStream stream, ReadOnlySpan<byte> header)
    {
        var length = (ulong)stream.Length;
        var tableOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[0x20..]);
        var entrySize = BinaryPrimitives.ReadUInt16LittleEndian(header[0x36..]);
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(header[0x38..]);
        if (entrySize < ElfProgramHeaderSize)
        {
            return true;
        }

        // More than 65,534 program headers (a process with many mappings): the real count is section header 0's sh_info.
        if (count == ElfExtendedCount)
        {
            var sectionTable = BinaryPrimitives.ReadUInt64LittleEndian(header[0x28..]);
            Span<byte> info = stackalloc byte[4];
            if (sectionTable > length - 0x30 || !ReadAt(stream, sectionTable + 0x2C, info))
            {
                return true;
            }

            count = BinaryPrimitives.ReadUInt32LittleEndian(info);
        }

        if (tableOffset > length || count > (length - tableOffset) / entrySize)
        {
            return true;
        }

        Span<byte> entry = stackalloc byte[ElfProgramHeaderSize];
        for (var i = 0UL; i < count; i++)
        {
            if (!ReadAt(stream, tableOffset + (i * entrySize), entry))
            {
                return true;
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x08..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x20..]);
            if ((type == ElfLoad || type == ElfNote) && size > 0 && (offset > length || size > length - offset))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMachOTruncated(FileStream stream, ReadOnlySpan<byte> header)
    {
        var length = (ulong)stream.Length;
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var commandsSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (MachOHeaderSize + (ulong)commandsSize > length)
        {
            return true;
        }

        var position = (ulong)MachOHeaderSize;
        var end = position + commandsSize;
        Span<byte> command = stackalloc byte[72];
        for (var i = 0u; i < commands; i++)
        {
            if (position + 8 > end || !ReadAt(stream, position, command[..8]))
            {
                return true;
            }

            var kind = BinaryPrimitives.ReadUInt32LittleEndian(command);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
            if (size < 8 || position + size > end)
            {
                return true;
            }

            if (kind == MachOSegment64)
            {
                if (size < command.Length || !ReadAt(stream, position, command))
                {
                    return true;
                }

                var offset = BinaryPrimitives.ReadUInt64LittleEndian(command[40..]);
                var fileSize = BinaryPrimitives.ReadUInt64LittleEndian(command[48..]);
                if (fileSize > 0 && (offset > length || fileSize > length - offset))
                {
                    return true;
                }
            }

            position += size;
        }

        return false;
    }

    private static bool ReadAt(FileStream stream, ulong offset, Span<byte> buffer)
    {
        if (offset > (ulong)stream.Length)
        {
            return false;
        }

        stream.Position = (long)offset;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }
}
