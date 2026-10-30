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

using System.Globalization;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>A small LeakyConsole dump (one leak) shared by the snapshot tests; each test copies it to its own folder.</summary>
public sealed class StaticCacheDump : IAsyncLifetime
{
    public const int Items = 3_000;

    private readonly TempFolder _folder = new();

    public string Path { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Path = _folder.File("static-cache.dmp");
        using var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", Items.ToString(CultureInfo.InvariantCulture), "--payload", "16");
        fixture.CaptureDump(Path);
    }

    public ValueTask DisposeAsync()
    {
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Sprint 1.2: snapshots in the worker, through the Application session, against a real worker process. Alone (sprint
/// 3.6): the cancel test measures a load stopping within 2 s, which other dump loads beside it pushed past 3 s on CI's
/// two-core runners.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(HeavyTestsCollection))]
public sealed class SnapshotTests : IClassFixture<StaticCacheDump>, IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host;
    private readonly JobTracker _jobs = new(TimeProvider.System);
    private readonly SnapshotSession _session;
    private readonly WorkerOperations _operations;
    private readonly string _dump;

    public SnapshotTests(StaticCacheDump shared)
    {
        _host = new WorkerHost(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
        _session = new SnapshotSession(_host, _jobs);
        _operations = new WorkerOperations(_host, _jobs);
        _dump = _folder.File("app.dmp");
        File.Copy(shared.Path, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CachePath => _dump + ".hsidx";

    [Fact]
    public async Task First_open_loads_the_dump_and_writes_the_cache_and_the_next_open_reuses_it()
    {
        var first = await _session.StartOpen(_dump).Result;

        var d = first.Descriptor;
        d.FromCache.ShouldBeFalse();
        d.CachePath.ShouldBe(CachePath);
        File.Exists(CachePath).ShouldBeTrue();
        d.Kind.ShouldBe(SnapshotKindDto.FullDump);
        d.RuntimeVersion.ShouldNotBeNullOrEmpty();
        d.ProcessName.ShouldBe("LeakyConsole");
        d.ObjectCount.ShouldBeGreaterThan(StaticCacheDump.Items * 2);
        d.Features.HasFlag(SnapshotFeatures.Generations).ShouldBeTrue();
        d.Features.HasFlag(SnapshotFeatures.RootDetails).ShouldBeTrue();
        d.Notes.ShouldBeEmpty();

        await _host.RestartAsync(Ct);
        var second = await _session.StartOpen(_dump).Result;

        second.ShouldBeSameAs(first, "one handle per file");
        second.Descriptor.FromCache.ShouldBeTrue();
        second.Descriptor.ObjectCount.ShouldBe(d.ObjectCount);
        second.Descriptor.ReferenceCount.ShouldBe(d.ReferenceCount);
        second.Descriptor.TotalSize.ShouldBe(d.TotalSize);
    }

    [Fact]
    public async Task A_dump_moved_with_its_cache_reports_and_reopens_the_new_path()
    {
        await _session.StartOpen(_dump).Result;
        await _host.RestartAsync(Ct);
        var moved = System.IO.Path.Combine(_folder.Path, "moved");
        Directory.CreateDirectory(moved);
        var movedDump = System.IO.Path.Combine(moved, "app.dmp");
        File.Move(_dump, movedDump);
        File.Move(CachePath, movedDump + ".hsidx");
        var session = new SnapshotSession(_host, _jobs);

        var handle = await session.StartOpen(movedDump).Result;
        var again = await session.StartOpen(movedDump).Result;

        handle.Descriptor.FromCache.ShouldBeTrue("the fingerprint does not depend on the path");
        handle.Path.ShouldBe(movedDump);
        handle.Descriptor.Path.ShouldBe(movedDump);
        again.Descriptor.SnapshotId.ShouldBe(handle.Descriptor.SnapshotId, "the worker recognizes the open file");

        await _host.RestartAsync(Ct);
        (await session.GetTypeSummaryAsync(handle, Ct)).TotalCount.ShouldBe(handle.Descriptor.TypeCount);
        handle.Descriptor.Path.ShouldBe(movedDump);
    }

    [Fact]
    public async Task Type_summary_totals_equal_the_snapshot_and_count_the_leak_items()
    {
        var handle = await _session.StartOpen(_dump).Result;

        var summary = await _session.GetTypeSummaryAsync(handle, Ct);
        var rows = new List<IReadOnlyList<string?>>();
        for (var offset = 0; offset < summary.TotalCount; offset += WorkerProtocol.MaxPageSize)
        {
            rows.AddRange((await _operations.GetRowsAsync(summary.Id, offset, WorkerProtocol.MaxPageSize, Ct)).Rows);
        }

        summary.TotalCount.ShouldBe(handle.Descriptor.TypeCount);
        rows.Sum(r => long.Parse(r[1]!, CultureInfo.InvariantCulture)).ShouldBe(handle.Descriptor.ObjectCount);
        rows.Sum(r => decimal.Parse(r[2]!, CultureInfo.InvariantCulture)).ShouldBe((decimal)handle.Descriptor.TotalSize);
        rows.Single(r => r[0] == "Fixtures.Leaks.StaticCacheEntry")[1].ShouldBe(StaticCacheDump.Items.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Type_summary_totals_equal_the_snapshot_and_the_known_answers_hold()
    {
        var handle = await _session.StartOpen(_dump).Result;

        var summary = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, "Bytes", true, null, Ct);
        var rows = await AllRowsAsync(summary.Rows);

        summary.Result.TotalObjects.ShouldBe(handle.Descriptor.ObjectCount);
        summary.Result.TotalBytes.ShouldBe((long)handle.Descriptor.TotalSize);
        summary.Result.TotalTypes.ShouldBe(handle.Descriptor.TypeCount);
        rows.Sum(r => long.Parse(r[1]!, CultureInfo.InvariantCulture)).ShouldBe(summary.Result.TotalObjects);
        rows.Sum(r => long.Parse(r[2]!, CultureInfo.InvariantCulture)).ShouldBe(summary.Result.TotalBytes);
        summary.Result.HasGenerations.ShouldBeTrue();
        summary.Result.HeapKinds.Sum(k => k.Bytes).ShouldBe(summary.Result.TotalBytes);
        summary.Rows.Info.Columns.Select(c => c.Name).ShouldBe(["Name", "Objects", "Bytes", "Percent", "Gen0", "Gen1", "Gen2", "LargeObjectHeap", "PinnedObjectHeap", "Frozen", "TypeName", "Module"]);
        summary.Rows.Info.Columns[0].IsKey.ShouldBeTrue();
        rows.Select(r => long.Parse(r[2]!, CultureInfo.InvariantCulture)).ShouldBeInOrder(SortDirection.Descending);

        // Known answers (fixtures/KnownAnswers/StaticCache.json): exact per-item counts.
        var answer = KnownAnswer.Load("StaticCache");
        foreach (var type in answer.Types)
        {
            rows.Single(r => r[0] == type.Type)[1].ShouldBe((StaticCacheDump.Items * type.PerItem).ToString(CultureInfo.InvariantCulture), type.Type);
        }

        await summary.Rows.ReleaseAsync(Ct);
    }

    [Fact]
    public async Task Type_summary_filters_groups_and_sorts_in_the_worker()
    {
        var handle = await _session.StartOpen(_dump).Result;

        var filtered = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, "SCE", "Name", false, null, Ct);
        var filteredRows = await AllRowsAsync(filtered.Rows);
        var byNamespace = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Namespace, null, "Objects", true, null, Ct);
        var namespaceRows = await AllRowsAsync(byNamespace.Rows);

        filteredRows.ShouldNotBeEmpty();
        filteredRows.ShouldAllBe(r => NameFilter.IsMatch(r[0]!, "SCE"));
        filteredRows.ShouldContain(r => r[0] == "Fixtures.Leaks.StaticCacheEntry");
        filteredRows.Select(r => r[0]).ShouldBe(filteredRows.Select(r => r[0]).Order(StringComparer.OrdinalIgnoreCase));
        filtered.Result.TotalObjects.ShouldBe(handle.Descriptor.ObjectCount, "totals cover the whole snapshot, not only the filtered rows");
        namespaceRows.ShouldContain(r => r[0] == "Fixtures.Leaks");
        namespaceRows.Sum(r => long.Parse(r[1]!, CultureInfo.InvariantCulture)).ShouldBe(handle.Descriptor.ObjectCount);
    }

    [Fact]
    public async Task A_result_set_that_is_gone_is_queried_again_transparently()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var summary = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, "Bytes", true, null, Ct);
        var first = (await summary.Rows.GetRowsAsync(0, 5, Ct)).Rows;

        // Evicted (released here) in the same worker, then lost with a worker restart.
        await _host.InvokeAsync((service, ct) => service.ReleaseResultSetAsync(summary.Rows.Info.Id, ct), Ct);
        (await summary.Rows.GetRowsAsync(0, 5, Ct)).Rows.Select(r => r[0]).ShouldBe(first.Select(r => r[0]));
        await _host.RestartAsync(Ct);
        (await summary.Rows.GetRowsAsync(0, 5, Ct)).Rows.Select(r => r[0]).ShouldBe(first.Select(r => r[0]));

        summary.Rows.Requeries.ShouldBe(2);

        // Released by its owner: finished, never queried again.
        await summary.Rows.ReleaseAsync(Ct);
        await Should.ThrowAsync<ObjectDisposedException>(() => summary.Rows.GetRowsAsync(0, 5, Ct));
        summary.Rows.Requeries.ShouldBe(2);
    }

    [Fact]
    public async Task After_a_worker_restart_the_session_reopens_the_snapshot_from_the_cache()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var staleId = handle.Descriptor.SnapshotId;

        await _host.RestartAsync(Ct);
        var summary = await _session.GetTypeSummaryAsync(handle, Ct);

        summary.TotalCount.ShouldBe(handle.Descriptor.TypeCount);
        handle.Descriptor.FromCache.ShouldBeTrue();
        handle.Descriptor.SnapshotId.ShouldNotBe(staleId);

        var ex = await Should.ThrowAsync<WorkerOperationException>(
            () => _host.InvokeAsync((service, ct) => service.GetTypeSummaryAsync(staleId, ct), Ct));
        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.SnapshotNotLoaded);
    }

    [Fact]
    public async Task After_a_restart_a_changed_dump_is_not_reloaded_silently()
    {
        var handle = await _session.StartOpen(_dump).Result;
        await _host.RestartAsync(Ct);
        File.Delete(CachePath);

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => _session.GetTypeSummaryAsync(handle, Ct));

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.SnapshotChanged);
        ex.Message.ShouldContain("Open it again");
        File.Exists(CachePath).ShouldBeFalse("the reopen must not load the dump");
    }

    [Fact]
    public async Task A_closed_handle_is_never_reopened()
    {
        var handle = await _session.StartOpen(_dump).Result;
        await _session.CloseAsync(handle, Ct);
        await _host.RestartAsync(Ct);

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => _session.GetTypeSummaryAsync(handle, Ct));

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.SnapshotNotLoaded);
        handle.IsClosed.ShouldBeTrue();
        (await _host.InvokeAsync((service, ct) => service.ProbeSnapshotAsync(_dump, ct), Ct)).IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_reports_the_cache_state_and_the_session_warns_only_for_a_first_load()
    {
        var tiny = new SnapshotSession(_host, _jobs, () => new PhysicalMemory(16L << 30, 1));

        var before = await _host.InvokeAsync((service, ct) => service.ProbeSnapshotAsync(_dump, ct), Ct);
        (await tiny.GetOpenWarningAsync(_dump, rebuild: false, Ct)).ShouldNotBeNull("no cache yet: a first load");
        await tiny.StartOpen(_dump).Result;
        var after = await _host.InvokeAsync((service, ct) => service.ProbeSnapshotAsync(_dump, ct), Ct);

        before.CacheState.ShouldBe(SnapshotCacheState.Missing);
        before.FileBytes.ShouldBe(new FileInfo(_dump).Length);
        after.CacheState.ShouldBe(SnapshotCacheState.Valid);
        after.IsOpen.ShouldBeTrue();
        (await tiny.GetOpenWarningAsync(_dump, rebuild: false, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Probe_of_a_missing_file_is_an_invalid_argument()
    {
        var ex = await Should.ThrowAsync<WorkerOperationException>(
            () => _host.InvokeAsync((service, ct) => service.ProbeSnapshotAsync(_folder.File("missing.dmp"), ct), Ct));

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task A_closed_snapshot_is_not_loaded_any_more()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var id = handle.Descriptor.SnapshotId;

        await _session.CloseAsync(handle, Ct);

        _session.Snapshots.ShouldBeEmpty();
        var ex = await Should.ThrowAsync<WorkerOperationException>(
            () => _host.InvokeAsync((service, ct) => service.GetTypeSummaryAsync(id, ct), Ct));
        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.SnapshotNotLoaded);
    }

    [Fact]
    public async Task Forced_mapped_mode_opens_the_cache_as_views()
    {
        var handle = await _session.StartOpen(_dump, mode: SnapshotMemoryMode.Mapped).Result;

        handle.Descriptor.Mode.ShouldBe(SnapshotMemoryMode.Mapped);
        (await _session.GetTypeSummaryAsync(handle, Ct)).TotalCount.ShouldBe(handle.Descriptor.TypeCount);
    }

    [Fact]
    public async Task Canceling_a_load_stops_it_and_leaves_no_cache_file()
    {
        // A dump of its own, large enough that a fast machine (CI's, sprint 3.6) is still loading when the cancel arrives:
        // the shared 3,000-item dump loaded completely between the progress report and the cancel.
        var large = _folder.File("large.dmp");
        using (var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", "300000", "--payload", "16"))
        {
            fixture.CaptureDump(large);
        }

        File.Delete(_dump);
        File.Move(large, _dump);

        // A cancel that reaches the worker after the load's last cancellation point lets the load finish; on a fast
        // machine that race is won now and then (CI, sprint 3.6). Each attempt loads afresh, and one must be canceled.
        TimeSpan? stoppedIn = null;
        for (var attempt = 0; attempt < 5 && stoppedIn is null; attempt++)
        {
            var job = _session.StartOpen(_dump, rebuild: true);
            await WaitUntilAsync(() => job.Progress?.Phase is "Opening dump" or "Reading objects" or "Reading references" || job.Result.IsCompleted);
            var canceledAt = System.Diagnostics.Stopwatch.StartNew();
            job.Cancel();
            try
            {
                await _session.CloseAsync(await job.Result, Ct);

                // Finished: its cache is not what this test is about, and the next attempt must leave none.
                File.Delete(CachePath);
            }
            catch (OperationCanceledException)
            {
                stoppedIn = canceledAt.Elapsed;
            }
        }

        stoppedIn.ShouldNotBeNull("five loads in a row finished before their cancel arrived");
        stoppedIn.Value.ShouldBeLessThan(TimeSpan.FromSeconds(2), "acceptance criterion: a load stops within 2 s");
        File.Exists(CachePath).ShouldBeFalse();
        Directory.EnumerateFiles(_folder.Path, "*.tmp").ShouldBeEmpty();
        (await _session.StartOpen(_dump).Result).Descriptor.FromCache.ShouldBeFalse("the worker stays usable");
    }

    [Fact]
    public async Task A_file_that_is_not_a_dump_fails_with_a_format_error()
    {
        var path = _folder.File("broken.dmp");
        await File.WriteAllBytesAsync(path, new byte[64 * 1024], Ct);

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => _session.StartOpen(path).Result);

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.SnapshotFormat);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    private static async Task<List<IReadOnlyList<string?>>> AllRowsAsync(ResultSetHandle rows)
    {
        var all = new List<IReadOnlyList<string?>>();
        while (all.Count < rows.Info.TotalCount)
        {
            all.AddRange((await rows.GetRowsAsync(all.Count, WorkerProtocol.MaxPageSize, Ct)).Rows);
        }

        return all;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(5, Ct);
        }
    }
}

/// <summary>The CLI open command against a real dump (sprint 1.2).</summary>
[Trait("Category", "Integration")]
public sealed class CliOpenTests(StaticCacheDump shared) : IClassFixture<StaticCacheDump>, IDisposable
{
    private readonly TempFolder _folder = new();

    [Fact]
    public async Task Cli_open_prints_the_snapshot_and_top_types_then_reuses_the_cache()
    {
        var dump = _folder.File("app.dmp");
        File.Copy(shared.Path, dump);

        using var first = ChildProcess.Start(TestPaths.CliExe, "open", dump, "--top", "5");
        (await first.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(first.Output);
        first.Process.ExitCode.ShouldBe(0, first.Output);
        first.Output.ShouldContain("from the dump");
        first.Output.ShouldContain("Top 5 of");

        using var second = ChildProcess.Start(TestPaths.CliExe, "open", dump, "--top", "1");
        (await second.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(second.Output);
        second.Process.ExitCode.ShouldBe(0, second.Output);
        second.Output.ShouldContain("from the index cache");
    }

    [Fact]
    public async Task Cli_summary_prints_a_table_csv_and_json()
    {
        var dump = _folder.File("summary.dmp");
        File.Copy(shared.Path, dump);

        using var table = ChildProcess.Start(TestPaths.CliExe, "summary", dump, "--top", "3");
        (await table.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(table.Output);
        table.Process.ExitCode.ShouldBe(0, table.Output);
        table.Output.ShouldContain("Heap kinds:");

        using var csv = ChildProcess.Start(TestPaths.CliExe, "summary", dump, "--format", "csv", "--filter", "StaticCacheEntry", "--sort", "name");
        (await csv.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(csv.Output);
        csv.Process.ExitCode.ShouldBe(0, csv.Output);
        csv.Output.ShouldContain("Name,Objects,Bytes,Percent,Gen0");
        csv.Output.ShouldContain("Fixtures.Leaks.StaticCacheEntry," + StaticCacheDump.Items.ToString(CultureInfo.InvariantCulture) + ",");

        using var json = ChildProcess.Start(TestPaths.CliExe, "summary", dump, "--format", "json", "--group", "namespace", "--top", "2");
        (await json.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(json.Output);
        json.Process.ExitCode.ShouldBe(0, json.Output);
        var start = json.Output.IndexOf('{', StringComparison.Ordinal);
        using var document = System.Text.Json.JsonDocument.Parse(json.Output[start..(json.Output.LastIndexOf('}') + 1)]);
        document.RootElement.GetProperty("Rows").GetArrayLength().ShouldBe(2);
        document.RootElement.GetProperty("TotalObjects").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Cli_open_of_a_missing_file_fails()
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, "open", _folder.File("missing.dmp"));

        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(1, cli.Output);
        cli.Output.ShouldContain("does not exist");
    }

    public void Dispose() => _folder.Dispose();
}
