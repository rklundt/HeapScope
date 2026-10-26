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


namespace HeapScope.Core.Tests;

/// <summary>Sprint 2.3.6 (2.3.5 review L5): HeapScope's own folders and files are the user's alone off Windows.</summary>
public sealed class PrivateFilesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-private-").FullName;

    [Fact]
    public void Folders_and_files_it_creates_are_owner_only_off_windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps the user profile's ACL.");

        var nested = Path.Combine(_folder, "sessions", "one");
        PrivateFiles.CreateDirectory(nested);
        using (PrivateFiles.Open(Path.Combine(nested, "session.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(Path.Combine(_folder, "sessions")).ShouldBe(PrivateFiles.FolderMode);
            File.GetUnixFileMode(nested).ShouldBe(PrivateFiles.FolderMode);
            File.GetUnixFileMode(Path.Combine(nested, "session.json")).ShouldBe(PrivateFiles.FileMode);
        }
    }

    [Fact]
    public void Opening_an_existing_file_keeps_it_and_appending_works_everywhere()
    {
        var path = Path.Combine(_folder, "log.txt");
        File.WriteAllText(path, "first ");
        using (var stream = PrivateFiles.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write("second"u8);
        }

        File.ReadAllText(path).ShouldBe("first second");
    }

    [Fact]
    public void Space_reserved_up_front_fails_at_open_when_the_disk_cannot_hold_it()
    {
        // Sprint 2.3.6 review: a mapped write past the free space kills the process on Linux and macOS; reserving first
        // turns a full disk into an IOException the caller handles.
        var path = Path.Combine(_folder, "huge.tmp");

        Should.Throw<IOException>(() => PrivateFiles.Open(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, preallocationSize: 1L << 50));
        File.Exists(path).ShouldBeFalse("a failed reservation leaves no file");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
