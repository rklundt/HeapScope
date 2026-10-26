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

namespace HeapScope.Loaders.ClrMd.Tests;

/// <summary>Sprint 2.3.5 review H2: a Linux or macOS core cut short is found from its own headers.</summary>
public sealed class CoreFileHeaderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-core-").FullName;

    [Fact]
    public void An_elf_core_whose_segments_are_all_in_the_file_is_complete()
    {
        var core = ElfCore(segments: 3, segmentBytes: 100);
        CoreFileHeader.IsTruncated(Write("complete", core)).ShouldBeFalse();
        CoreFileHeader.IsTruncated(Write("cut", core[..^1])).ShouldBeTrue("the last segment lost a byte");
        CoreFileHeader.IsTruncated(Write("headers", core[..100])).ShouldBeTrue("the program headers run past the end");
    }

    [Fact]
    public void An_elf_core_with_more_program_headers_than_its_header_holds_reads_the_count_from_section_zero()
    {
        // PN_XNUM: e_phnum is 0xFFFF and section header 0's sh_info holds the real count.
        var core = ElfCore(segments: 2, segmentBytes: 50, extendedCount: true);
        CoreFileHeader.IsTruncated(Write("xnum", core)).ShouldBeFalse();
        CoreFileHeader.IsTruncated(Write("xnum-cut", core[..^1])).ShouldBeTrue();
    }

    [Fact]
    public void A_macho_core_whose_segments_are_all_in_the_file_is_complete()
    {
        var core = MachOCore(segments: 2, segmentBytes: 80);
        CoreFileHeader.IsTruncated(Write("macho", core)).ShouldBeFalse();
        CoreFileHeader.IsTruncated(Write("macho-cut", core[..^1])).ShouldBeTrue();
    }

    [Fact]
    public void Damaged_headers_and_other_files_are_never_complete()
    {
        var macho = MachOCore(segments: 1, segmentBytes: 8);
        BinaryPrimitives.WriteUInt32LittleEndian(macho.AsSpan(32 + 4), 4); // cmdsize below the minimum of 8
        CoreFileHeader.IsTruncated(Write("cmdsize", macho)).ShouldBeTrue();
        CoreFileHeader.IsTruncated(Write("short", [0x7F, (byte)'E', (byte)'L', (byte)'F'])).ShouldBeTrue();
        CoreFileHeader.IsTruncated(Write("text", new byte[200])).ShouldBeTrue();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // An ELF64 core: header, a note and the load segments' headers, an optional section header 0 (PN_XNUM), then the data.
    private static byte[] ElfCore(int segments, int segmentBytes, bool extendedCount = false)
    {
        const int Header = 64;
        const int Entry = 56;
        var count = segments + 1;
        var sectionTable = Header + (count * Entry);
        var data = sectionTable + (extendedCount ? 64 : 0);
        var image = new byte[data + 16 + (segments * segmentBytes)];
        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2;
        image[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), 4); // ET_CORE
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(0x20), Header);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(0x28), extendedCount ? sectionTable : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), Entry);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), extendedCount ? (ushort)0xFFFF : (ushort)count);
        if (extendedCount)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 0x2C), (uint)count);
        }

        WriteSegment(image, Header, 4, data, 16); // PT_NOTE
        for (var i = 0; i < segments; i++)
        {
            WriteSegment(image, Header + ((i + 1) * Entry), 1, data + 16 + (i * segmentBytes), segmentBytes);
        }

        return image;

        static void WriteSegment(byte[] image, int at, uint type, long offset, long size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), type);
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(at + 0x08), offset);
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(at + 0x20), size);
        }
    }

    // A 64-bit Mach-O core: header, LC_SEGMENT_64 commands, then their data.
    private static byte[] MachOCore(int segments, int segmentBytes)
    {
        const int Header = 32;
        const int Command = 72;
        var data = Header + (segments * Command);
        var image = new byte[data + (segments * segmentBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 4); // MH_CORE
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)segments);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), (uint)(segments * Command));
        for (var i = 0; i < segments; i++)
        {
            var at = Header + (i * Command);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), 0x19); // LC_SEGMENT_64
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 4), Command);
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(at + 40), data + (i * segmentBytes));
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(at + 48), segmentBytes);
        }

        return image;
    }
}
