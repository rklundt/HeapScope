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

namespace HeapScope.Capture.Tests;

/// <summary>The partial file both capturers write through (sprint 1.9).</summary>
public sealed class PartialSnapshotFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Reserving_creates_the_folder_and_commit_renames_the_partial_file()
    {
        var path = Path.Combine(_folder, "sub", "app.gcdump");

        var file = PartialSnapshotFile.Reserve(path, "gcdump");
        File.WriteAllText(file.PartialPath, "data");
        file.Commit();

        File.ReadAllText(path).ShouldBe("data");
        File.Exists(file.PartialPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_existing_file_or_partial_file_is_never_overwritten(bool partial)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "app.dmp");
        File.WriteAllText(partial ? path + DumpCapturer.PartialSuffix : path, "mine");

        Should.Throw<DumpFileExistsException>(() => PartialSnapshotFile.Reserve(path, "dump")).Message.ShouldContain("never overwrites a dump");
    }

    [Fact]
    public void Committing_without_a_written_file_fails()
    {
        var file = PartialSnapshotFile.Reserve(Path.Combine(_folder, "app.dmp"), "dump");

        Should.Throw<DumpWriteFailedException>(file.Commit).Message.ShouldContain("no dump was written");
    }

    [Fact]
    public void A_file_that_appears_meanwhile_is_kept_and_the_new_one_removed()
    {
        var path = Path.Combine(_folder, "app.dmp");
        var file = PartialSnapshotFile.Reserve(path, "dump");
        File.WriteAllText(file.PartialPath, "new");
        File.WriteAllText(path, "theirs");

        Should.Throw<DumpFileExistsException>(file.Commit);

        File.ReadAllText(path).ShouldBe("theirs");
        File.Exists(file.PartialPath).ShouldBeFalse();
    }

    [Fact]
    public void Discard_removes_the_partial_file()
    {
        var file = PartialSnapshotFile.Reserve(Path.Combine(_folder, "app.dmp"), "dump");
        File.WriteAllText(file.PartialPath, "half");

        file.Discard();

        File.Exists(file.PartialPath).ShouldBeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
