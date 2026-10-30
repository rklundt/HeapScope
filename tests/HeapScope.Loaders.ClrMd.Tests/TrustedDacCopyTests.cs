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

using System.Security.Cryptography;

namespace HeapScope.Loaders.ClrMd.Tests;

/// <summary>
/// The private copy a trusted DAC is loaded from (sprint 3.6, ADR-035): the file loaded is the file hashed, never the one
/// next to the dump.
/// </summary>
public sealed class TrustedDacCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    public TrustedDacCopyTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void The_copy_is_made_under_the_hash_and_reused()
    {
        var dac = Planted([1, 2, 3]);
        var sha = Sha(dac);
        var copies = Path.Combine(_root, "copies");

        var copy = ClrMdDump.CopyTrusted(dac, sha, copies);

        copy.ShouldBe(Path.Combine(copies, sha, "mscordaccore.dll"));
        File.ReadAllBytes(copy).ShouldBe([1, 2, 3]);
        var written = File.GetLastWriteTimeUtc(copy);
        ClrMdDump.CopyTrusted(dac, sha, copies).ShouldBe(copy);
        File.GetLastWriteTimeUtc(copy).ShouldBe(written, "a good copy is reused");
    }

    [Fact]
    public void A_tampered_copy_is_replaced_from_the_trusted_file()
    {
        var dac = Planted([1, 2, 3]);
        var sha = Sha(dac);
        var copies = Path.Combine(_root, "copies");
        var copy = ClrMdDump.CopyTrusted(dac, sha, copies);
        File.WriteAllBytes(copy, [9, 9, 9]);

        ClrMdDump.CopyTrusted(dac, sha, copies).ShouldBe(copy);

        File.ReadAllBytes(copy).ShouldBe([1, 2, 3]);
        Directory.EnumerateFiles(Path.GetDirectoryName(copy)!, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void A_file_that_changed_after_it_was_trusted_is_not_loaded()
    {
        // The user trusted one content; the file next to the dump now holds another.
        var dac = Planted([1, 2, 3]);
        var sha = Sha(dac);
        File.WriteAllBytes(dac, [4, 5, 6]);

        Should.Throw<IOException>(() => ClrMdDump.CopyTrusted(dac, sha, Path.Combine(_root, "copies")))
            .Message.ShouldContain("does not match");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Planted(byte[] content)
    {
        var path = Path.Combine(_root, "beside-the-dump", "mscordaccore.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
