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

using HeapScope.Application;
using HeapScope.Application.Bookmarks;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Tests;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>Object bookmarks on the Snapshots page (sprint 2.7).</summary>
public sealed class BookmarksViewModelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-bookmarks-").FullName;
    private readonly FakeWorkerClient _worker = new();
    private readonly RecordingLogger<BookmarksViewModel> _logger = new();

    public BookmarksViewModelTests()
    {
        _worker.Service.Rows.Add(["App.Order", "2", "80"]);
        _worker.Service.InstanceRows.Add(["0x1000", "App.Order", "40", "Gen2", ""]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task The_shown_object_or_a_selected_row_is_bookmarked_and_toggled_off()
    {
        var snapshots = Create();
        await OpenAsync(snapshots, "app.dmp");
        var handle = snapshots.Selected!.Handle;

        (await snapshots.ToggleBookmarkAsync()).ShouldBeFalse("the types grid shows no object");
        var row = new Link(0x2000, "App.Customer");
        (await snapshots.ToggleBookmarkAsync(row)).ShouldBeTrue();
        snapshots.Navigator.OpenObject(handle, 0x1000);
        await ((ObjectViewModel)snapshots.Navigator.Current).LoadTask;
        await snapshots.ToggleBookmarkAsync();

        snapshots.Bookmarks.Items.Select(i => (i.Address, i.TypeName, i.Color)).ShouldBe([(0x2000UL, "App.Customer", BookmarkColor.Red), (0x1000UL, "App.Order", BookmarkColor.Orange)]);
        await snapshots.ToggleBookmarkAsync(row);
        snapshots.Bookmarks.Items.ShouldHaveSingleItem().Address.ShouldBe(0x1000UL);
    }

    [Fact]
    public async Task Bookmarks_come_back_when_the_snapshot_is_opened_again_in_a_new_run()
    {
        var first = Create();
        await OpenAsync(first, "app.dmp");
        await first.ToggleBookmarkAsync(new Link(0x1000, "App.Order"));
        await first.Bookmarks.CycleColorCommand.ExecuteAsync(first.Bookmarks.Items[0]);

        var second = Create();
        await OpenAsync(second, "app.dmp");
        await WaitUntilAsync(() => second.Bookmarks.Items.Count == 1);

        second.Bookmarks.Items[0].Color.ShouldBe(BookmarkColor.Orange);
        second.Bookmarks.Items[0].ColorKey.ShouldBe("Brush.Mark.Orange");
        _logger.Lines.ShouldContain(l => l.Contains("restored", StringComparison.Ordinal));
        _logger.Lines.ShouldAllBe(l => !l.Contains("0x1000", StringComparison.Ordinal) && !l.Contains("App.Order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opening_a_bookmark_selects_its_snapshot_and_shows_the_object_and_closing_hides_its_marks()
    {
        var snapshots = Create();
        await OpenAsync(snapshots, "app.dmp");
        var marked = snapshots.Selected!;
        await snapshots.ToggleBookmarkAsync(new Link(0x1000, "App.Order"));
        await OpenAsync(snapshots, "other.dmp");
        snapshots.Tab = SnapshotTabs.Compare;

        snapshots.Bookmarks.OpenCommand.Execute(snapshots.Bookmarks.Items[0]);

        snapshots.Selected.ShouldBe(marked);
        snapshots.Tab.ShouldBe(SnapshotTabs.Snapshot);
        snapshots.Navigator.Current.ShouldBeOfType<ObjectViewModel>().Address.ShouldBe(0x1000UL);

        await snapshots.CloseCommand.ExecuteAsync(marked);
        snapshots.Bookmarks.Items.ShouldBeEmpty();
    }

    private SnapshotsViewModel Create()
    {
        var operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        var loggers = new SingleLoggerFactory(_logger);
        return new SnapshotsViewModel(new SnapshotSession(_worker, operations.Jobs), operations, new ImmediateUiDispatcher(), new FakeUserInteraction(), loggers, new BookmarkStore(Path.Combine(_folder, "data")));
    }

    private async Task OpenAsync(SnapshotsViewModel snapshots, string name)
    {
        var path = Path.Combine(_folder, name);
        if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(path, [1], Ct);
        }

        await snapshots.OpenPathAsync(path);
        await snapshots.Types.QueryTask;
        await WaitUntilAsync(() => true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(10, Ct);
        }
    }

    private sealed record Link(ulong LinkAddress, string? LinkTypeName) : IObjectLink;

    /// <summary>Hands the bookmarks logger out for its category and a null logger for the others.</summary>
    private sealed class SingleLoggerFactory(RecordingLogger<BookmarksViewModel> bookmarks) : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(BookmarksViewModel).FullName ? bookmarks : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}
