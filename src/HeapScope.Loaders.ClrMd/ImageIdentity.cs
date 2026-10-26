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
using System.Text;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// The identity a Linux or macOS dump records for a native image (sprint 2.3.5): an ELF image's GNU build id, a Mach-O
/// image's LC_UUID. A DAC found on this machine is used only when its identity equals the dump's, as a Windows DAC must
/// match the dump's PE time stamp and size (ADR-017), so a DAC of another build is never loaded. ClrMD's own readers of
/// these formats are internal, so the two small readers live here.
/// </summary>
public static class ImageIdentity
{
    private const uint ElfMagic = 0x464C457F; // 0x7F 'E' 'L' 'F', little-endian
    private const uint MachOMagic64 = 0xFEEDFACF;
    private const uint ProgramHeaderNote = 4;
    private const uint NoteGnuBuildId = 3;
    private const uint LoadCommandUuid = 0x1B;
    private const int MaxProgramHeaders = 256;
    private const int MaxNoteBytes = 1 << 16;
    private const int MaxLoadCommands = 4096;

    /// <summary>
    /// The version string every CoreCLR native binary embeds after <c>@(#)Version </c> (for example
    /// <c>10.0.1426.52326 @Commit: ...</c>), or null. Two binaries with the same string come from the same runtime build
    /// (sprint 2.3.5 review M1: a DAC paired with its runtime library is checked this way outside installed runtimes).
    /// </summary>
    public static string? ReadVersionString(string path)
    {
        ReadOnlySpan<byte> marker = "@(#)Version "u8;
        const int Chunk = 1 << 20;
        const int MaxLength = 256;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            var buffer = new byte[Chunk + MaxLength + marker.Length];
            var kept = 0;
            while (true)
            {
                var read = stream.ReadAtLeast(buffer.AsSpan(kept, Chunk), Chunk, throwOnEndOfStream: false);
                var filled = kept + read;
                var span = buffer.AsSpan(0, filled);
                var at = span.IndexOf(marker);
                if (at >= 0)
                {
                    var rest = span[(at + marker.Length)..];
                    var end = rest[..Math.Min(rest.Length, MaxLength)].IndexOf((byte)0);
                    if (end >= 0)
                    {
                        return Encoding.ASCII.GetString(rest[..end]).Trim();
                    }

                    if (read < Chunk || rest.Length >= MaxLength)
                    {
                        return null;
                    }

                    // The string continues in the next chunk: keep it from the marker on.
                    kept = filled - at;
                    buffer.AsSpan(at, kept).CopyTo(buffer);
                    continue;
                }

                if (read < Chunk)
                {
                    return null;
                }

                kept = marker.Length - 1;
                buffer.AsSpan(filled - kept, kept).CopyTo(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The image's build id (ELF) or UUID (Mach-O); null when the file is neither or has none.</summary>
    public static byte[]? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[4];
            if (stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4)
            {
                return null;
            }

            stream.Position = 0;
            return BinaryPrimitives.ReadUInt32LittleEndian(magic) switch
            {
                ElfMagic => ReadElfBuildId(stream),
                MachOMagic64 => ReadMachOUuid(stream),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>True when the file at <paramref name="path"/> has exactly <paramref name="expected"/> as its identity.</summary>
    public static bool Matches(string path, ReadOnlySpan<byte> expected) =>
        !expected.IsEmpty && Read(path) is { } actual && actual.AsSpan().SequenceEqual(expected);

    /// <summary>A 64-bit little-endian ELF image's GNU build id: the NT_GNU_BUILD_ID note of a PT_NOTE program header.</summary>
    private static byte[]? ReadElfBuildId(Stream stream)
    {
        Span<byte> header = stackalloc byte[64];
        stream.ReadExactly(header);
        if (header[4] != 2 || header[5] != 1)
        {
            return null; // not ELF64 little-endian
        }

        var programHeaders = BinaryPrimitives.ReadInt64LittleEndian(header[0x20..]);
        var entrySize = BinaryPrimitives.ReadUInt16LittleEndian(header[0x36..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header[0x38..]);
        if (programHeaders <= 0 || entrySize < 56)
        {
            return null;
        }

        Span<byte> entry = stackalloc byte[56];
        for (var i = 0; i < Math.Min((int)count, MaxProgramHeaders); i++)
        {
            stream.Position = programHeaders + ((long)i * entrySize);
            stream.ReadExactly(entry);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != ProgramHeaderNote)
            {
                continue;
            }

            var offset = BinaryPrimitives.ReadInt64LittleEndian(entry[0x08..]);
            var size = BinaryPrimitives.ReadInt64LittleEndian(entry[0x20..]);
            if (offset <= 0 || size <= 0 || size > MaxNoteBytes)
            {
                continue;
            }

            var notes = new byte[size];
            stream.Position = offset;
            stream.ReadExactly(notes);
            if (FindBuildIdNote(notes) is { } buildId)
            {
                return buildId;
            }
        }

        return null;
    }

    /// <summary>The descriptor of the "GNU" note of type NT_GNU_BUILD_ID among <paramref name="notes"/>.</summary>
    public static byte[]? FindBuildIdNote(ReadOnlySpan<byte> notes)
    {
        ReadOnlySpan<byte> gnu = [(byte)'G', (byte)'N', (byte)'U', 0];
        var position = 0;
        while (position + 12 <= notes.Length)
        {
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(notes[position..]);
            var descSize = BinaryPrimitives.ReadUInt32LittleEndian(notes[(position + 4)..]);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(notes[(position + 8)..]);
            var name = position + 12L;
            var desc = name + Align4(nameSize);
            if (desc + descSize > notes.Length)
            {
                return null;
            }

            if (type == NoteGnuBuildId && nameSize == 4 && notes.Slice((int)name, 4).SequenceEqual(gnu))
            {
                return notes.Slice((int)desc, (int)descSize).ToArray();
            }

            position = (int)(desc + Align4(descSize));
        }

        return null;
    }

    /// <summary>A 64-bit little-endian Mach-O image's LC_UUID load command, if any.</summary>
    private static byte[]? ReadMachOUuid(Stream stream)
    {
        // mach_header_64: magic, cputype, cpusubtype, filetype, ncmds, sizeofcmds, flags, reserved (32 bytes).
        Span<byte> header = stackalloc byte[32];
        stream.ReadExactly(header);
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        Span<byte> command = stackalloc byte[24];
        long position = 32;
        for (var i = 0; i < Math.Min(commands, (uint)MaxLoadCommands); i++)
        {
            stream.Position = position;
            stream.ReadExactly(command[..8]);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(command);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
            if (kind == LoadCommandUuid && size >= 24)
            {
                stream.ReadExactly(command[8..24]);
                return command[8..24].ToArray();
            }

            if (size < 8)
            {
                return null;
            }

            position += size;
        }

        return null;
    }

    private static long Align4(uint value) => (value + 3L) & ~3L;
}
