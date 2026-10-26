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

/// <summary>Sprint 2.3.5: a Linux or macOS DAC is matched by the build id or UUID the dump records.</summary>
public sealed class ImageIdentityTests : IDisposable
{
    private static readonly byte[] s_id = [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    public ImageIdentityTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void An_elf_images_gnu_build_id_is_read_and_matched()
    {
        var path = Write("libmscordaccore.so", Elf(s_id));

        ImageIdentity.Read(path).ShouldBe(s_id);
        ImageIdentity.Matches(path, s_id).ShouldBeTrue();
        ImageIdentity.Matches(path, s_id[..^1]).ShouldBeFalse("a different id never matches");
        ImageIdentity.Matches(path, []).ShouldBeFalse("no id recorded means no match");
    }

    [Fact]
    public void A_mach_o_images_uuid_is_read_after_other_load_commands()
    {
        var uuid = s_id[..16];
        var path = Write("libmscordaccore.dylib", MachO(uuid));

        ImageIdentity.Read(path).ShouldBe(uuid);
        ImageIdentity.Matches(path, uuid).ShouldBeTrue();
    }

    [Fact]
    public void Other_files_and_damaged_images_have_no_identity()
    {
        var truncated = Elf(s_id);
        Array.Resize(ref truncated, 100);

        ImageIdentity.Read(Write("mscordaccore.dll", [(byte)'M', (byte)'Z', 0, 0, 0, 0])).ShouldBeNull();
        ImageIdentity.Read(Write("short", [0x7F])).ShouldBeNull();
        ImageIdentity.Read(Write("truncated.so", truncated)).ShouldBeNull();
        ImageIdentity.Read(Path.Combine(_folder, "missing.so")).ShouldBeNull();
    }

    [Fact]
    public void A_note_list_skips_other_notes_and_stops_at_a_damaged_one()
    {
        var other = Note("GNU\0"u8.ToArray(), 1, [1, 2, 3, 4]);
        var build = Note("GNU\0"u8.ToArray(), 3, s_id);

        ImageIdentity.FindBuildIdNote([.. other, .. build]).ShouldBe(s_id);
        ImageIdentity.FindBuildIdNote(other).ShouldBeNull();
        ImageIdentity.FindBuildIdNote(build[..^4]).ShouldBeNull("the descriptor runs past the end");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A minimal ELF64 little-endian image: a header, two program headers (a load and a note) and the notes.</summary>
    private static byte[] Elf(byte[] buildId)
    {
        var notes = Note("GNU\0"u8.ToArray(), 3, buildId);
        const int programHeaders = 64;
        var noteOffset = programHeaders + (2 * 56);
        var image = new byte[noteOffset + notes.Length];
        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2; // 64-bit
        image[5] = 1; // little-endian
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(0x20), programHeaders);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(programHeaders), 1); // PT_LOAD, ignored
        var note = programHeaders + 56;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(note), 4); // PT_NOTE
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(note + 0x08), noteOffset);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(note + 0x20), notes.Length);
        notes.CopyTo(image, noteOffset);
        return image;
    }

    private static byte[] Note(byte[] name, uint type, byte[] descriptor)
    {
        static int Align(int value) => (value + 3) & ~3;
        var note = new byte[12 + Align(name.Length) + Align(descriptor.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(note, (uint)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(4), (uint)descriptor.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(8), type);
        name.CopyTo(note, 12);
        descriptor.CopyTo(note, 12 + Align(name.Length));
        return note;
    }

    /// <summary>A minimal 64-bit Mach-O image: a header, a segment command to skip, then LC_UUID.</summary>
    private static byte[] MachO(byte[] uuid)
    {
        var image = new byte[32 + 72 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 2); // two load commands
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), 0x19); // LC_SEGMENT_64
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(104), 0x1B); // LC_UUID
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(108), 24);
        uuid.CopyTo(image, 112);
        return image;
    }
}
