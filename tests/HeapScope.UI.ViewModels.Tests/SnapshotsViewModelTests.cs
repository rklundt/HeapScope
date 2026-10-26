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
using HeapScope.Application.Snapshots;
using HeapScope.UI.Services;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

public sealed class SnapshotsViewModelTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly FakeWorkerClient _worker = new();
    private readonly FakeUserInteraction _interaction = new();
    private readonly WorkerOperations _operations;
    private readonly SnapshotsViewModel _snapshots;
    private PhysicalMemory _memory = new(32 * Gb, 20 * Gb);

    public SnapshotsViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        _operations = new WorkerOperations(_worker, new JobTracker(TimeProvider.System));
        _snapshots = new SnapshotsViewModel(new SnapshotSession(_worker, _operations.Jobs, () => _memory), _operations, new ImmediateUiDispatcher(), _interaction);
        _worker.Service.Rows.Add(["System.String", "600", "36000"]);
        _worker.Service.Rows.Add(["System.Byte[]", "400", "12000"]);
    }

    [Fact]
    public async Task Opening_a_dump_lists_and_selects_it_and_loads_its_largest_types()
    {
        var dump = Dump("app.dmp", 1024);

        await _snapshots.OpenPathAsync(dump);
        await _snapshots.Types.QueryTask;
        var rows = _snapshots.Types.Rows;
        rows.GetRow(0);
        await rows.WhenLoadedAsync();

        var item = _snapshots.Snapshots.ShouldHaveSingleItem();
        _snapshots.Selected.ShouldBeSameAs(item);
        item.Title.ShouldBe("app.dmp");
        item.Process.ShouldStartWith("App (pid 42), .NET 10.0.13");
        item.Source.ShouldContain("from the dump");
        item.Notes.ShouldBe(["A note."]);
        rows.Count.ShouldBe(2);
        (rows.GetRow(0).Name, rows.GetRow(0).Objects, rows.GetRow(0).Bytes).ShouldBe(("System.String", 600L, 36000L));
        (rows.GetRow(1).Name, rows.GetRow(1).Bytes).ShouldBe(("System.Byte[]", 12000L));
        _interaction.Questions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_load_that_may_not_fit_asks_first_and_declining_opens_nothing()
    {
        _memory = new PhysicalMemory(16 * Gb, Gb / 2);
        _interaction.Answer = false;

        await _snapshots.OpenPathAsync(Dump("big.dmp", 1024));

        _interaction.Questions.ShouldHaveSingleItem().ShouldContain("only 0.5 GB of 16.0 GB");
        _worker.Service.Opened.ShouldBeEmpty();
        _snapshots.Status.ShouldBe("Open canceled.");
    }

    [Fact]
    public async Task No_memory_question_when_an_index_cache_exists()
    {
        _memory = new PhysicalMemory(16 * Gb, 1 * Gb);
        var dump = Dump("cached.dmp", 1024);
        await File.WriteAllBytesAsync(dump + ".hsidx", [1], TestContext.Current.CancellationToken);

        await _snapshots.OpenPathAsync(dump);

        _interaction.Questions.ShouldBeEmpty();
        _worker.Service.Opened.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Open_dump_command_uses_the_file_dialog_and_does_nothing_when_canceled()
    {
        _interaction.NextFile = null;
        await _snapshots.OpenDumpCommand.ExecuteAsync(null);
        _worker.Service.Opened.ShouldBeEmpty();

        _interaction.NextFile = Dump("picked.dmp", 10);
        await _snapshots.OpenDumpCommand.ExecuteAsync(null);
        _worker.Service.Opened.ShouldHaveSingleItem().Path.ShouldBe(_interaction.NextFile);
    }

    [Fact]
    public async Task Closing_removes_the_snapshot_and_closes_it_in_the_worker()
    {
        await _snapshots.OpenPathAsync(Dump("a.dmp", 10));
        var item = _snapshots.Selected!;

        await _snapshots.CloseCommand.ExecuteAsync(item);

        _snapshots.Snapshots.ShouldBeEmpty();
        _snapshots.Selected.ShouldBeNull();
        _worker.Service.Closed.ShouldBe([item.Handle.Descriptor.SnapshotId]);
    }

    [Fact]
    public async Task Missing_file_is_reported_and_nothing_is_opened()
    {
        await _snapshots.OpenPathAsync(Path.Combine(_folder, "missing.dmp"));

        _snapshots.Status!.ShouldContain("does not exist");
        _snapshots.IsOpening.ShouldBeFalse();
        _worker.Service.Opened.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_worker_error_while_opening_is_shown_as_the_status()
    {
        _worker.Service.OpenFailure = new HeapScope.Application.Workers.WorkerOperationException(
            Contracts.HeapScopeErrorCode.SnapshotFormat, "Not a dump.", null);

        await _snapshots.OpenPathAsync(Dump("broken.dmp", 10));

        _snapshots.Status.ShouldBe("Not a dump.");
        _snapshots.Snapshots.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shell_shows_the_snapshots_page_when_a_snapshot_opens()
    {
        var processes = new ProcessesViewModel(new FakeCaptureService(Path.Combine(_folder, "sessions"), _operations.Jobs), _snapshots, _interaction);
        using var shell = new ShellViewModel(_operations, new ImmediateUiDispatcher(), new HomeViewModel(_operations, _snapshots), _snapshots, processes, new AboutViewModel(_operations));

        await _snapshots.OpenPathAsync(Dump("b.dmp", 10));

        shell.SelectedItem.Page.ShouldBeSameAs(_snapshots);
        shell.Home.OpenDumpCommand.ShouldBeSameAs(_snapshots.OpenDumpCommand);
    }

    [Theory]
    [InlineData(512L, "512 bytes")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(3L * 1024 * 1024, "3.0 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.00 GB")]
    public void Sizes_are_humanized(long bytes, string expected)
    {
        using var culture = new InvariantCultureScope();

        Format.Bytes(bytes).ShouldBe(expected);
    }

    public void Dispose()
    {
        _snapshots.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Dump(string name, int bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private sealed class InvariantCultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public InvariantCultureScope() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
