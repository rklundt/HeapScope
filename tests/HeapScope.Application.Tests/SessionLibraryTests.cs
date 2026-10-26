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

using System.Text.Json.Nodes;
using HeapScope.Application.Sessions;

namespace HeapScope.Application.Tests;

public sealed class SessionLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_current_session_is_created_on_first_use_and_records_snapshots()
    {
        var library = new SessionLibrary(_root, _time);
        library.HasCurrent.ShouldBeFalse();
        var folder = library.Current.Folder;
        var dump = Path.Combine(folder, "app.dmp");
        File.WriteAllBytes(dump, new byte[1000]);

        var session = library.AddToCurrent(Snapshot(dump, label: "baseline"));

        session.Folder.ShouldBe(folder);
        library.HasCurrent.ShouldBeTrue();
        var read = SessionLibrary.TryRead(folder)!;
        var recorded = read.Document.Snapshots.ShouldHaveSingleItem();
        recorded.File.ShouldBe("app.dmp", "inside the session folder the path is relative");
        read.PathOf(recorded).ShouldBe(dump);
        recorded.Label.ShouldBe("baseline");
        recorded.Id.ShouldNotBe(Guid.Empty);
        File.ReadAllText(Path.Combine(folder, SessionLibrary.DocumentName)).ShouldContain("\"FullDump\"", customMessage: "kinds are written as names");
    }

    [Fact]
    public void Session_folders_and_documents_are_the_users_alone_off_windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps the user profile's ACL.");
        var library = new SessionLibrary(_root, _time);

        var folder = library.Current.Folder;
        library.AddToCurrent(Snapshot(Path.Combine(folder, "app.dmp"), label: null));

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(_root).ShouldBe(HeapScope.Core.PrivateFiles.FolderMode);
            File.GetUnixFileMode(folder).ShouldBe(HeapScope.Core.PrivateFiles.FolderMode);
            File.GetUnixFileMode(Path.Combine(folder, SessionLibrary.DocumentName)).ShouldBe(HeapScope.Core.PrivateFiles.FileMode);
        }
    }

    [Fact]
    public void Old_half_written_dumps_and_gcdumps_are_swept_and_fresh_ones_kept()
    {
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        var old = _time.GetUtcNow().UtcDateTime.AddHours(-1);
        string[] stale = [Path.Combine(folder, "a.dmp.partial"), Path.Combine(folder, "b.gcdump.partial")];
        foreach (var file in stale)
        {
            File.WriteAllBytes(file, new byte[10]);
            File.SetLastWriteTimeUtc(file, old);
        }

        var fresh = Path.Combine(folder, "c.gcdump.partial");
        File.WriteAllBytes(fresh, new byte[10]);
        File.SetLastWriteTimeUtc(fresh, _time.GetUtcNow().UtcDateTime);

        library.SweepPartialDumps().ShouldBe(2);

        stale.ShouldAllBe(f => !File.Exists(f));
        File.Exists(fresh).ShouldBeTrue();
    }

    [Fact]
    public void Snapshots_outside_the_session_folder_keep_their_full_path()
    {
        var library = new SessionLibrary(_root, _time);
        var outside = Path.Combine(_root, "elsewhere.dmp");

        var session = library.AddToCurrent(Snapshot(outside));

        session.Document.Snapshots[0].File.ShouldBe(outside);
    }

    [Fact]
    public void Recent_sessions_are_listed_by_last_opened_with_their_size()
    {
        var library = new SessionLibrary(_root, _time);
        var older = library.CreateSession("first try");
        _time.Advance(TimeSpan.FromHours(1));
        var newer = library.CreateSession("second");
        File.WriteAllBytes(Path.Combine(newer.Folder, "a.dmp"), new byte[2048]);
        _time.Advance(TimeSpan.FromHours(1));
        library.Touch(older.Folder);

        var recent = library.ListRecent();

        recent.Select(s => s.Name).ShouldBe(["first try", "second"]);
        recent[1].SizeBytes.ShouldBeGreaterThan(2048);
        Path.GetFileName(older.Folder).ShouldEndWith("first_try");
    }

    [Fact]
    public void Damaged_or_foreign_folders_are_skipped()
    {
        var library = new SessionLibrary(_root, _time);
        library.CreateSession("good");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-session"));
        var damaged = Path.Combine(_root, "damaged");
        Directory.CreateDirectory(damaged);
        File.WriteAllText(Path.Combine(damaged, SessionLibrary.DocumentName), "{ not json");

        library.ListRecent().Select(s => s.Name).ShouldBe(["good"]);
    }

    [Fact]
    public void Two_runs_in_the_same_second_get_distinct_folders()
    {
        // Two libraries stand for two HeapScope processes with the same root and clock.
        var first = new SessionLibrary(_root, _time).Current.Folder;
        var second = new SessionLibrary(_root, _time).Current.Folder;

        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task Concurrent_records_and_touches_never_lose_a_snapshot()
    {
        var library = new SessionLibrary(_root, _time);
        var other = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        other.Open(folder).ShouldNotBeNull();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            if (i % 3 == 0)
            {
                library.Touch(folder);
            }
            else
            {
                (i % 2 == 0 ? library : other).AddToCurrent(Snapshot(Path.Combine(folder, $"{i}.dmp")));
            }
        })));

        SessionLibrary.TryRead(folder)!.Document.Snapshots.Count.ShouldBe(Enumerable.Range(0, 40).Count(i => i % 3 != 0));
        Directory.EnumerateFiles(folder, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Members_written_by_a_newer_version_survive_a_save()
    {
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        var path = Path.Combine(folder, SessionLibrary.DocumentName);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["FutureField"] = "kept";
        File.WriteAllText(path, json.ToJsonString());

        library.AddToCurrent(Snapshot(Path.Combine(folder, "a.dmp")));

        JsonNode.Parse(File.ReadAllText(path))!["FutureField"]!.GetValue<string>().ShouldBe("kept");
    }

    [Fact]
    public void Opening_a_session_makes_it_current()
    {
        var library = new SessionLibrary(_root, _time);
        var existing = library.CreateSession("earlier");

        library.Open(Path.GetFileName(existing.Folder))!.Folder.ShouldBe(existing.Folder);
        library.Current.Folder.ShouldBe(existing.Folder);
        library.Open("missing").ShouldBeNull();
    }

    [Fact]
    public void An_empty_session_left_by_a_failed_capture_is_removed()
    {
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;

        library.DeleteIfEmpty(folder);

        Directory.Exists(folder).ShouldBeFalse();
        library.HasCurrent.ShouldBeFalse();
    }

    [Fact]
    public void A_relative_root_is_under_HeapScope_data_not_the_current_directory()
    {
        var library = new SessionLibrary("custom-sessions");

        library.Root.ShouldBe(Path.Combine(Path.GetDirectoryName(SessionLibrary.DefaultRoot)!, "custom-sessions"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private SessionSnapshot Snapshot(string file, string? label = null) =>
        new(Guid.NewGuid(), file, "App", 42, "10.0.13", _time.GetUtcNow(), 1000, Label: label);

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
