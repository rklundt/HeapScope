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

using HeapScope.Application.Bookmarks;

namespace HeapScope.Application.Tests;

/// <summary>Object bookmarks kept across runs (sprint 2.7).</summary>
public sealed class BookmarkStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-bookmarks-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Bookmarks_come_back_for_the_same_file_in_a_new_store()
    {
        var key = KeyOf("app.dmp");
        new BookmarkStore(_folder).Set(Mark(key, 0x10, BookmarkColor.Blue));
        new BookmarkStore(_folder).Set(Mark(key, 0x20, BookmarkColor.Red));

        var read = new BookmarkStore(_folder).For(key);

        read.Select(b => (b.Address, b.TypeName, b.Color)).ShouldBe([(0x10UL, "App.Order", BookmarkColor.Blue), (0x20UL, "App.Order", BookmarkColor.Red)]);
        File.ReadAllText(Path.Combine(_folder, BookmarkStore.FileName)).ShouldContain("\"Blue\"", customMessage: "colors are written by name");
    }

    [Fact]
    public void A_file_replaced_at_the_same_path_does_not_inherit_the_marks()
    {
        var key = KeyOf("app.dmp");
        var store = new BookmarkStore(_folder);
        store.Set(Mark(key, 0x10));

        File.WriteAllBytes(Path.Combine(_folder, "app.dmp"), [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(Path.Combine(_folder, "app.dmp"), DateTime.UtcNow.AddMinutes(5));

        store.For(KeyOf("app.dmp")).ShouldBeEmpty();
        store.For(key).ShouldHaveSingleItem();
    }

    [Fact]
    public void Setting_an_address_again_replaces_it_and_remove_removes_it()
    {
        var key = KeyOf("app.dmp");
        var store = new BookmarkStore(_folder);
        store.Set(Mark(key, 0x10, BookmarkColor.Red));
        store.Set(Mark(key, 0x10, BookmarkColor.Green));

        store.For(key).ShouldHaveSingleItem().Color.ShouldBe(BookmarkColor.Green);
        store.Remove(key, 0x10).ShouldBeTrue();
        store.Remove(key, 0x10).ShouldBeFalse();
        new BookmarkStore(_folder).For(key).ShouldBeEmpty();
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_a_newer_format_is_never_overwritten()
    {
        var key = KeyOf("app.dmp");
        var file = Path.Combine(_folder, BookmarkStore.FileName);
        File.WriteAllText(file, "{ not json");
        var store = new BookmarkStore(_folder);

        store.For(key).ShouldBeEmpty();
        store.Set(Mark(key, 0x10));
        File.Exists(file + ".damaged").ShouldBeTrue();

        File.WriteAllText(file, "{\"FormatVersion\": 99, \"Bookmarks\": []}");
        var newer = new BookmarkStore(_folder);
        newer.For(key).ShouldBeEmpty();
        Should.Throw<IOException>(() => newer.Set(Mark(key, 0x20)));
        File.ReadAllText(file).ShouldContain("99");
    }

    [Fact]
    public void A_missing_file_has_no_key()
    {
        SnapshotKey.Of(Path.Combine(_folder, "gone.dmp")).ShouldBeNull();
    }

    private SnapshotKey KeyOf(string name)
    {
        var path = Path.Combine(_folder, name);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, [1]);
        }

        return SnapshotKey.Of(path).ShouldNotBeNull();
    }

    private static Bookmark Mark(SnapshotKey key, ulong address, BookmarkColor color = BookmarkColor.Red) =>
        new(key, address, "App.Order", color, DateTimeOffset.UnixEpoch);
}
