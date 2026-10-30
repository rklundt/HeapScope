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
    public void Deleting_a_snapshot_removes_its_file_caches_and_sidecars_next_to_it_and_in_the_fallback_folder()
    {
        // Sprint 3.4 (decision 6).
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        var dump = Path.Combine(folder, "app.dmp");
        var other = Path.Combine(folder, "app.dmp.other.dmp");
        File.WriteAllBytes(dump, new byte[100]);
        File.WriteAllBytes(other, new byte[10]);
        var fallback = Path.Combine(_root, "cache");
        Directory.CreateDirectory(fallback);
        string[] caches = [dump + ".hsidx", dump + ".rev.hsidx", HeapScope.Core.IndexCachePaths.InFallback(dump, fallback), HeapScope.Core.IndexCachePaths.Sidecar(HeapScope.Core.IndexCachePaths.InFallback(dump, fallback), "dom")];
        foreach (var cache in caches)
        {
            File.WriteAllBytes(cache, new byte[7]);
        }

        File.WriteAllBytes(other + ".hsidx", new byte[3]);
        var snapshot = Snapshot(dump);
        library.AddToCurrent(snapshot);
        library.AddToCurrent(Snapshot(other));

        var deletion = library.DeleteSnapshot(folder, snapshot.Id, fallback);

        deletion.Files.Order().ShouldBe([dump, .. caches], ignoreOrder: true);
        deletion.Bytes.ShouldBe(100 + (4 * 7));
        File.Exists(other).ShouldBeTrue();
        File.Exists(other + ".hsidx").ShouldBeTrue("another dump's cache that shares the name's start is left alone");
        SessionLibrary.TryRead(folder)!.Document.Snapshots.ShouldHaveSingleItem().File.ShouldBe("app.dmp.other.dmp");
    }

    [Fact]
    public void A_session_is_deleted_whole_but_never_the_current_one_and_old_ones_are_listed_oldest_first()
    {
        var library = new SessionLibrary(_root, _time);
        var old = library.CreateSession("old");
        File.WriteAllBytes(Path.Combine(old.Folder, "a.dmp"), new byte[50]);
        library.AddTo(old.Folder, Snapshot(Path.Combine(old.Folder, "a.dmp")));
        _time.Advance(TimeSpan.FromDays(40));
        var recent = library.CreateSession("recent");
        library.Open(recent.Folder).ShouldNotBeNull();
        _time.Advance(TimeSpan.FromDays(1));

        library.ListOlderThan(TimeSpan.FromDays(30)).Select(s => s.Name).ShouldBe(["old"]);
        Should.Throw<InvalidOperationException>(() => library.DeleteSession(recent.Folder));
        Should.Throw<ArgumentException>(() => library.DeleteSession(Path.GetTempPath()));

        var deletion = library.DeleteSession(old.Folder, Path.Combine(_root, "cache"));

        Directory.Exists(old.Folder).ShouldBeFalse();
        deletion.Bytes.ShouldBeGreaterThanOrEqualTo(50);
        library.ListOlderThan(TimeSpan.FromDays(30)).ShouldBeEmpty();
    }

    [Fact]
    public void Deletion_never_reaches_outside_the_session_folder()
    {
        // Review H2 of 3.4: a dump captured to a path of the user's choosing, an entry climbing out with "..", a folder that
        // only shares the root's name start, and a sibling dump's caches are never deleted.
        var library = new SessionLibrary(Path.Combine(_root, "sessions"), _time);
        var session = library.CreateSession("s");
        var outside = Path.Combine(_root, "elsewhere", "mine.dmp");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, new byte[20]);
        File.WriteAllBytes(outside + ".hsidx", new byte[5]);
        var external = Snapshot(outside);
        library.AddTo(session.Folder, external);
        var climbing = Snapshot(Path.Combine(session.Folder, "..", "..", "elsewhere", "mine.dmp"));
        library.AddTo(session.Folder, climbing with { File = Path.Combine("..", "..", "elsewhere", "mine.dmp") });

        Should.Throw<InvalidOperationException>(() => library.PlanSnapshotDeletion(session.Folder, external.Id)).Message.ShouldContain("outside its session folder");
        Should.Throw<InvalidOperationException>(() => library.DeleteSnapshot(session.Folder, external.Id));
        Should.Throw<InvalidOperationException>(() => library.DeleteSnapshot(session.Folder, climbing.Id));
        File.Exists(outside).ShouldBeTrue();

        var lookalike = Path.Combine(_root, "sessions-old", "s");
        Directory.CreateDirectory(lookalike);
        File.Copy(Path.Combine(session.Folder, SessionLibrary.DocumentName), Path.Combine(lookalike, SessionLibrary.DocumentName));
        Should.Throw<ArgumentException>(() => library.DeleteSession(lookalike), "sessions-old is not under sessions");
        Should.Throw<ArgumentException>(() => library.DeleteSession(Path.Combine(session.Folder, "..", "..")));

        var dump = Path.Combine(session.Folder, "app.dmp");
        File.WriteAllBytes(dump, new byte[10]);
        File.WriteAllBytes(Path.Combine(session.Folder, "app.dmp.bak"), new byte[1]);
        File.WriteAllBytes(Path.Combine(_root, "app.dmp.bak.hsidx"), new byte[1]);
        File.WriteAllBytes(dump + ".bak.hsidx", new byte[2]);
        File.WriteAllBytes(dump + ".hsidx." + Guid.NewGuid().ToString("N") + ".tmp", new byte[3]);
        File.WriteAllBytes(dump + ".hsidx.notmine.tmp", new byte[4]);
        var inside = Snapshot(dump);
        library.AddTo(session.Folder, inside);
        library.PlanSnapshotDeletion(session.Folder, inside.Id).Files.Select(Path.GetFileName).Order().ShouldBe(["app.dmp", Path.GetFileName(Directory.GetFiles(session.Folder, "*.tmp").Single(f => !f.Contains("notmine", StringComparison.Ordinal)))]);

        var whole = library.PlanSessionDeletion(session.Folder);
        whole.Kept.ShouldNotBeNull().ShouldContain(Path.GetFullPath(outside));
        library.DeleteSession(session.Folder).Kept.ShouldNotBeNull().Count.ShouldBe(2);
        (Directory.Exists(session.Folder), File.Exists(outside), File.Exists(outside + ".hsidx")).ShouldBe((false, true, true));
    }

    [Fact]
    public void A_session_something_writes_into_is_not_deleted_and_a_failed_deletion_keeps_it_listed()
    {
        // Reviews H1 and H4 of 3.4.
        var library = new SessionLibrary(_root, _time);
        var session = library.CreateSession("busy");
        var dump = Path.Combine(session.Folder, "a.dmp");
        File.WriteAllBytes(dump, new byte[10]);
        library.AddTo(session.Folder, Snapshot(dump));

        using (library.MarkInUse(session.Folder, "a monitoring run"))
        {
            library.InUseBy(session.Folder).ShouldBe("a monitoring run");
            Should.Throw<InvalidOperationException>(() => library.DeleteSession(session.Folder)).Message.ShouldContain("monitoring run");
        }

        library.InUseBy(session.Folder).ShouldBeNull();
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows refuses to delete a file another handle holds open; the partial failure cannot happen elsewhere.
            library.DeleteSession(session.Folder).Complete.ShouldBeTrue();
            return;
        }

        Deletion failed;
        using (new FileStream(dump, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = library.DeleteSession(session.Folder);
        }

        failed.Complete.ShouldBeFalse();
        failed.Failed.ShouldNotBeNull().ShouldHaveSingleItem().ShouldStartWith(dump);
        SessionLibrary.TryRead(session.Folder).ShouldNotBeNull("session.json goes last, so the session is still listed");
        library.ListRecent().ShouldContain(s => s.Name == "busy");

        var again = library.DeleteSession(session.Folder);
        again.Complete.ShouldBeTrue();
        again.Files.ShouldContain(Path.Combine(session.Folder, SessionLibrary.DocumentName));
        Directory.Exists(session.Folder).ShouldBeFalse();
    }

    [Fact]
    public void A_run_record_keeps_members_a_newer_build_wrote()
    {
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        var run = new SessionRun(Guid.NewGuid(), "app", 7, 1, "none", HeapScope.Contracts.SnapshotKindDto.FullDump, _time.GetUtcNow(), null, "counters.csv", null, []);
        library.RecordRun(folder, run);
        var path = Path.Combine(folder, SessionLibrary.DocumentName);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["Runs"]![0]!["FutureField"] = 3;
        File.WriteAllText(path, json.ToJsonString());

        library.RecordRun(folder, run with { EndedUtc = _time.GetUtcNow() });

        var saved = JsonNode.Parse(File.ReadAllText(path))!["Runs"]!.AsArray().ShouldHaveSingleItem()!;
        saved["FutureField"]!.GetValue<int>().ShouldBe(3, "a newer build's member survives an update by id");
        SessionLibrary.TryRead(folder)!.Document.Runs!.Single().EndedUtc.ShouldNotBeNull();
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
    public async Task Readers_never_make_a_save_fail()
    {
        // Peer review P1: readers took no lock and opened without delete sharing, so a save's replace failed on Windows
        // while any reader held the document, and the snapshot was lost from the session.
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        using var stop = new CancellationTokenSource();
        using var reading = new CountdownEvent(3);
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                if (reads == 1)
                {
                    reading.Signal();
                }

                SessionLibrary.TryRead(folder).ShouldNotBeNull("a reader never sees a missing or half-written document");
                library.ListRecent();
                reads++;
            }

            return reads;
        })).ToArray();

        try
        {
            // Every reader is reading before the first save (review: on a busy machine they could start after the saves).
            if (!reading.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
            {
                await Task.WhenAll(readers.Where(r => r.IsFaulted));
                Assert.Fail("The readers did not start within 30 s.");
            }

            for (var i = 0; i < 200; i++)
            {
                library.AddTo(folder, Snapshot(Path.Combine(folder, $"{i}.dmp")));
            }
        }
        finally
        {
            await stop.CancelAsync();
        }

        (await Task.WhenAll(readers)).ShouldAllBe(n => n > 0);
        SessionLibrary.TryRead(folder)!.Document.Snapshots.Count.ShouldBe(200);
        Directory.EnumerateFiles(folder, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_save_waits_for_a_reader_that_does_not_share_delete()
    {
        // An older build, a virus scanner or an indexer may hold the document without delete sharing for a moment.
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        Task<Session> save;
        using (new FileStream(Path.Combine(folder, SessionLibrary.DocumentName), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            save = Task.Run(() => library.AddTo(folder, Snapshot(Path.Combine(folder, "a.dmp"))));
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        (await save).Document.Snapshots.ShouldHaveSingleItem();
        SessionLibrary.TryRead(folder)!.Document.Snapshots.ShouldHaveSingleItem();
    }

    [Fact]
    public void An_unreadable_current_session_fails_instead_of_starting_a_new_one()
    {
        // Peer review P1: any failed read used to move the next captures into a brand-new session.
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        File.WriteAllText(Path.Combine(folder, SessionLibrary.DocumentName), "{ not json");

        var ex = Should.Throw<SessionUnreadableException>(() => library.Current);
        ex.Message.ShouldContain(folder);
        SessionLibrary.Read(folder).Status.ShouldBe(SessionReadStatus.Unreadable);
        Directory.EnumerateDirectories(_root).ShouldHaveSingleItem("no new session was created");
        Should.Throw<SessionUnreadableException>(() => library.AddToCurrent(Snapshot(Path.Combine(folder, "a.dmp"))));

        // A document of another format version is not this build's to rewrite either.
        File.WriteAllText(Path.Combine(folder, SessionLibrary.DocumentName), """{ "FormatVersion": 2, "Name": "x", "Snapshots": [] }""");
        Should.Throw<SessionUnreadableException>(() => library.Current).Message.ShouldContain("format version 2");

        // A session folder that is gone is replaced by a new one, as before (same clock, so the same name).
        Directory.Delete(folder, recursive: true);
        var created = library.Current;
        created.Document.Snapshots.ShouldBeEmpty();
        File.Exists(Path.Combine(created.Folder, SessionLibrary.DocumentName)).ShouldBeTrue();
    }

    [Fact]
    public void An_empty_new_session_is_kept_while_another_capture_or_run_writes_into_it()
    {
        // Sprint 3.6 review: a failed capture removed the session it created even when another capture had taken it.
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        using var mine = library.MarkInUse(folder, "a capture");
        using (library.MarkInUse(folder, "a monitoring run"))
        {
            library.DeleteIfEmpty(folder, "a capture");
            Directory.Exists(folder).ShouldBeTrue("the run writes into it");
            library.CurrentFolder.ShouldBe(folder);
        }

        library.DeleteIfEmpty(folder, "a capture");
        Directory.Exists(folder).ShouldBeFalse("only the failed capture's own claim was left");
        library.HasCurrent.ShouldBeFalse();
    }

    [Fact]
    public void Opening_a_session_that_cannot_be_read_says_why()
    {
        // Sprint 3.6 review: unreadable is not missing, for the app's and the CLI's Open as for the current session.
        var library = new SessionLibrary(_root, _time);
        var folder = library.CreateSession("damaged").Folder;
        File.WriteAllText(Path.Combine(folder, SessionLibrary.DocumentName), "{ not json");

        var ex = Should.Throw<SessionUnreadableException>(() => library.Open(folder));
        ex.Message.ShouldContain("damaged");
        ex.Message.ShouldContain("Open another session");
        library.HasCurrent.ShouldBeFalse("a failed open changes nothing");
        library.Open(Path.Combine(_root, "no-such-session")).ShouldBeNull();
    }

    [Fact]
    public async Task The_current_folder_answers_while_an_update_waits_for_the_session_lock()
    {
        // Peer review P1: the update held the same gate as CurrentFolder across its wait for session.lock (up to 30 s),
        // so the Live page's refresh on the UI thread could block.
        var library = new SessionLibrary(_root, _time);
        var folder = library.Current.Folder;
        Task<Session> update;
        using (new FileStream(Path.Combine(folder, SessionLibrary.LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            update = Task.Run(() => library.AddTo(folder, Snapshot(Path.Combine(folder, "a.dmp"))));
            await Task.Delay(200, TestContext.Current.CancellationToken);
            update.IsCompleted.ShouldBeFalse("the update waits for the lock another process holds");

            var answered = Task.Run(() => (library.CurrentFolder, library.HasCurrent));
            (await answered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ShouldBe((folder, true));
        }

        (await update).Document.Snapshots.ShouldHaveSingleItem();
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
