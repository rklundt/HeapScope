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

using System.IO;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Sessions;
using HeapScope.Application.Snapshots;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>Sprint 3.4 (decision 6): deleting snapshots and sessions from the app, always asked first.</summary>
public sealed class HousekeepingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly FakeUserInteraction _interaction = new();
    private readonly SessionLibrary _library;
    private readonly SnapshotsViewModel _snapshots;
    private readonly HomeViewModel _home;

    public HousekeepingTests()
    {
        _library = new SessionLibrary(_root);
        var operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        _snapshots = new SnapshotsViewModel(new SnapshotSession(_worker, operations.Jobs), operations, new ImmediateUiDispatcher(), _interaction, library: _library);
        _home = new HomeViewModel(operations, _snapshots, _library, _interaction);
    }

    [Fact]
    public async Task Close_and_delete_removes_a_session_snapshot_with_its_cache_after_asking_and_leaves_other_dumps_alone()
    {
        var folder = _library.Current.Folder;
        var dump = Path.Combine(folder, "app.dmp");
        await File.WriteAllBytesAsync(dump, new byte[100], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(dump + ".hsidx", new byte[10], TestContext.Current.CancellationToken);
        _library.AddToCurrent(new SessionSnapshot(Guid.NewGuid(), dump, "app", 1, "10.0", DateTimeOffset.UtcNow, 100));
        await _snapshots.OpenPathAsync(dump);

        _interaction.Answer = false;
        await _snapshots.CloseAndDeleteCommand.ExecuteAsync(null);
        (File.Exists(dump), _snapshots.Snapshots.Count).ShouldBe((true, 1), "declined: nothing happens");
        _interaction.Questions[^1].ShouldContain("110 bytes");

        _interaction.Answer = true;
        await _snapshots.CloseAndDeleteCommand.ExecuteAsync(null);

        (File.Exists(dump), File.Exists(dump + ".hsidx"), _snapshots.Snapshots.Count).ShouldBe((false, false, 0));
        SessionLibrary.TryRead(folder)!.Document.Snapshots.ShouldBeEmpty();
        _snapshots.Status!.ShouldStartWith("Deleted app.dmp");

        var outside = Path.Combine(_root, "elsewhere.dmp");
        await File.WriteAllBytesAsync(outside, new byte[5], TestContext.Current.CancellationToken);
        await _snapshots.OpenPathAsync(outside);
        await _snapshots.CloseAndDeleteCommand.ExecuteAsync(null);
        (File.Exists(outside), _snapshots.Status).ShouldBe((true, "This dump is not in a HeapScope session, so HeapScope does not delete it: close it and delete the file yourself."));
    }

    [Fact]
    public async Task Sessions_with_an_open_snapshot_or_current_are_kept_and_old_ones_are_cleaned_up_after_asking()
    {
        var old = _library.CreateSession("old");
        var dump = Path.Combine(old.Folder, "a.dmp");
        await File.WriteAllBytesAsync(dump, new byte[50], TestContext.Current.CancellationToken);
        _library.AddTo(old.Folder, new SessionSnapshot(Guid.NewGuid(), dump, "a", 1, "10.0", DateTimeOffset.UtcNow, 50));
        _library.Open(old.Folder);
        await _home.RefreshSessionsCommand.ExecuteAsync(null);
        var summary = _home.RecentSessions.Single(s => s.Name == "old");

        await _home.DeleteSessionCommand.ExecuteAsync(summary);
        _home.SessionStatus!.ShouldContain("is the current session");

        var other = _library.CreateSession("other");
        _library.Open(other.Folder);
        await _snapshots.OpenPathAsync(dump);
        await _home.DeleteSessionCommand.ExecuteAsync(summary);
        _home.SessionStatus!.ShouldContain("has a snapshot open");

        await _snapshots.CloseCommand.ExecuteAsync(null);
        _interaction.Answer = true;
        await _home.DeleteSessionCommand.ExecuteAsync(summary);

        Directory.Exists(old.Folder).ShouldBeFalse();
        _home.SessionStatus!.ShouldStartWith("Deleted 1 sessions");
        _home.RecentSessions.ShouldNotContain(s => s.Name == "old");

        _home.CleanupDays = 30;
        await _home.CleanUpCommand.ExecuteAsync(null);
        _home.SessionStatus!.ShouldStartWith("No session was left unopened for 30 days");
    }

    public void Dispose()
    {
        _snapshots.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
