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

namespace HeapScope.Loaders.ClrMd.Tests;

public sealed class MinidumpTruncationTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-minidump-").FullName;

    [Fact]
    public void A_dump_whose_memory_list_fits_the_file_is_not_truncated() =>
        MinidumpHeader.IsTruncated(Write(Dump(memoryBytes: 4096, fileMemoryBytes: 4096))).ShouldBeFalse();

    [Fact]
    public void A_dump_whose_memory_list_reaches_past_the_end_is_truncated() =>
        MinidumpHeader.IsTruncated(Write(Dump(memoryBytes: 4096, fileMemoryBytes: 1000))).ShouldBeTrue();

    [Fact]
    public void A_file_shorter_than_the_header_is_truncated() =>
        MinidumpHeader.IsTruncated(Write("MDMP"u8.ToArray())).ShouldBeTrue();

    [Fact]
    public void A_heap_dump_memory_list_range_past_the_end_is_truncated() =>
        MinidumpHeader.IsTruncated(Write(PlainListDump(declaredBytes: 4096, fileMemoryBytes: 100))).ShouldBeTrue();

    [Fact]
    public void A_heap_dump_memory_list_that_fits_is_not_truncated() =>
        MinidumpHeader.IsTruncated(Write(PlainListDump(declaredBytes: 100, fileMemoryBytes: 100))).ShouldBeFalse();

    [Fact]
    public void A_stream_directory_past_the_end_is_truncated()
    {
        var bytes = Dump(memoryBytes: 16, fileMemoryBytes: 16);
        BitConverter.GetBytes(10_000u).CopyTo(bytes, 12);

        MinidumpHeader.IsTruncated(Write(bytes)).ShouldBeTrue();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // MINIDUMP_HEADER, one directory entry for a MemoryListStream (type 5) with one MINIDUMP_MEMORY_DESCRIPTOR that points
    // at its own data: declaredBytes declared, fileMemoryBytes present.
    private static byte[] PlainListDump(uint declaredBytes, int fileMemoryBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(MinidumpHeader.MinidumpSignature);
        writer.Write(0xA793u);
        writer.Write(1u);
        writer.Write(32u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(MinidumpHeader.WithPrivateReadWriteMemory);
        writer.Write(5u);
        writer.Write(20u);
        writer.Write(44u);
        writer.Write(1u);
        writer.Write(0x10000UL);
        writer.Write(declaredBytes);
        writer.Write(64u);
        writer.Write(new byte[fileMemoryBytes]);
        writer.Flush();
        return stream.ToArray();
    }

    // MINIDUMP_HEADER (32 bytes), a one-entry stream directory (12 bytes), a Memory64 list with one range (32 bytes), then
    // the range's memory: memoryBytes declared, fileMemoryBytes present.
    private static byte[] Dump(ulong memoryBytes, int fileMemoryBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(MinidumpHeader.MinidumpSignature);
        writer.Write(0xA793u);
        writer.Write(1u);
        writer.Write(32u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(MinidumpHeader.WithFullMemory);
        writer.Write(9u);
        writer.Write(32u);
        writer.Write(44u);
        writer.Write(1UL);
        writer.Write(76UL);
        writer.Write(0x10000UL);
        writer.Write(memoryBytes);
        writer.Write(new byte[fileMemoryBytes]);
        writer.Flush();
        return stream.ToArray();
    }

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".dmp");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
