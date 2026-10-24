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

using System.Text;
using HeapScope.Core.Loading;

namespace HeapScope.Core.Tests;

public sealed class SnapshotFileSignatureTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-signature-").FullName;

    [Fact]
    public void A_minidump_is_a_full_dump() =>
        SnapshotFileSignature.Detect(Write("a.bin", [.. "MDMP"u8, 0x93, 0xA7, 0, 0])).ShouldBe(SnapshotKind.FullDump);

    [Fact]
    public void A_fast_serialization_stream_of_a_heap_dump_is_a_gcdump_whatever_its_extension() =>
        SnapshotFileSignature.Detect(Write("snapshot.dmp", GcDumpStart("GCHeapDump"))).ShouldBe(SnapshotKind.GcDump);

    [Fact]
    public void Another_fast_serialization_stream_is_not_a_gcdump() =>
        SnapshotFileSignature.Detect(Write("trace.gcdump", GcDumpStart("TraceLog"))).ShouldBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("MDM")]
    [InlineData("hello, this is a text file")]
    public void Short_empty_and_unknown_files_are_neither(string text) =>
        SnapshotFileSignature.Detect(Write("x.dmp", Encoding.ASCII.GetBytes(text))).ShouldBeNull();

    [Fact]
    public void The_unknown_file_message_names_both_kinds() =>
        SnapshotFileSignature.DescribeUnknown("x.bin").ShouldContain("neither a full memory dump nor a gcdump");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // The start of a FastSerialization stream: the length-prefixed signature, then the first object's type name.
    private static byte[] GcDumpStart(string typeName)
    {
        var signature = "!FastSerialization.1"u8;
        var name = Encoding.UTF8.GetBytes(typeName);
        return [.. BitConverter.GetBytes(signature.Length), .. signature, 5, 5, 5, .. BitConverter.GetBytes(name.Length), .. name, 1, 2, 3];
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
