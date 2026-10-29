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
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Capture;
using HeapScope.Contracts;
using HeapScope.Core.Graph;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.GcDump;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>
/// Two LeakyConsole processes with the static cache leak, 1,000 and 4,000 items: a gcdump of each, and a full dump of the
/// smaller one for the mixed diff (sprint 1.8).
/// </summary>
public sealed class StaticCacheGcDumps : IAsyncLifetime
{
    public const int BaselineItems = 1_000;
    public const int CurrentItems = 4_000;
    public const int Growth = CurrentItems - BaselineItems;

    private readonly TempFolder _folder = new();

    public string BaselineGcDump { get; private set; } = null!;

    public string CurrentGcDump { get; private set; } = null!;

    public string BaselineFullDump { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        BaselineGcDump = _folder.File("baseline.gcdump");
        CurrentGcDump = _folder.File("current.gcdump");
        BaselineFullDump = _folder.File("baseline.dmp");
        using (var baseline = await StartAsync(BaselineItems))
        {
            await new GcDumpCapturer().CaptureAsync(AllLeaksGcDump.Process(baseline.ProcessId), BaselineGcDump, null, null, CancellationToken.None);
            baseline.CaptureDump(BaselineFullDump);
        }

        using var current = await StartAsync(CurrentItems);
        await new GcDumpCapturer().CaptureAsync(AllLeaksGcDump.Process(current.ProcessId), CurrentGcDump, null, null, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A copy of <paramref name="source"/> in <paramref name="folder"/>, so each test has its own index cache.</summary>
    public static string CopyTo(string source, TempFolder folder, string? name = null)
    {
        var path = folder.File(name ?? Path.GetFileName(source));
        File.Copy(source, path);
        return path;
    }

    private static Task<FixtureProcess> StartAsync(int items) =>
        FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", items.ToString(CultureInfo.InvariantCulture), "--payload", "16");
}

/// <summary>Sprint 1.8: gcdumps through the worker, the session and the CLI.</summary>
[Trait("Category", "Integration")]
public sealed class GcDumpSnapshotTests(StaticCacheGcDumps shared) : IClassFixture<StaticCacheGcDumps>, IAsyncDisposable
{
    private const string EntryType = "Fixtures.Leaks.StaticCacheEntry";

    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private SnapshotSession? _session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SnapshotSession Session => _session ??= new SnapshotSession(_host, new JobTracker(TimeProvider.System));

    [Fact]
    public async Task A_gcdump_opens_through_the_worker_with_its_features_and_notes_then_from_its_cache()
    {
        var path = StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder);

        var first = await Session.StartOpen(path).Result;

        var d = first.Descriptor;
        d.Kind.ShouldBe(SnapshotKindDto.GcDump);
        d.Features.ShouldBe(SnapshotFeatures.ObjectGraph | SnapshotFeatures.CompleteGraph | SnapshotFeatures.ExactCounts | SnapshotFeatures.ObjectAddresses);
        d.FromCache.ShouldBeFalse();
        d.Notes.ShouldContain(n => n.StartsWith("A gcdump holds", StringComparison.Ordinal));
        await Session.CloseAsync(first, Ct);

        var second = await Session.StartOpen(path).Result;

        second.Descriptor.FromCache.ShouldBeTrue();
        second.Descriptor.Kind.ShouldBe(SnapshotKindDto.GcDump);
        second.Descriptor.ObjectCount.ShouldBe(d.ObjectCount);
    }

    [Fact]
    public async Task The_loader_is_chosen_by_content_not_by_extension()
    {
        var path = StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder, "named-like-a-dump.dmp");

        var handle = await Session.StartOpen(path).Result;

        handle.Descriptor.Kind.ShouldBe(SnapshotKindDto.GcDump);
    }

    [Fact]
    public async Task A_file_that_is_no_snapshot_is_refused_with_a_message()
    {
        var path = _folder.File("notes.dmp");
        await File.WriteAllTextAsync(path, "not a snapshot", Ct);

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => Session.StartOpen(path).Result);

        ex.Message.ShouldContain("neither a full memory dump nor a gcdump");
    }

    [Fact]
    public async Task An_object_of_a_gcdump_has_no_fields_and_says_why()
    {
        var handle = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder)).Result;
        var address = FirstAddress(shared.CurrentGcDump, EntryType);

        var details = await Session.GetObjectAsync(handle, address, null, Ct);

        details.TypeName.ShouldBe(EntryType);
        details.Fields.ShouldBeNull();
        details.FieldsUnavailable.ShouldBe(CapabilityDescriptions.ExplainMissing(SnapshotFeatures.FieldValues));
        details.Text.ShouldBeNull();
        details.HeapKind.ShouldBe(HeapKindDto.Unknown);
    }

    [Fact]
    public async Task Grouping_a_gcdump_by_interface_is_refused_with_the_reason()
    {
        // Sprint 3.2: a gcdump records no interfaces.
        var handle = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder)).Result;

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => Session.QueryTypeSummaryAsync(handle, TypeGrouping.Interface, null, null, true, null, Ct));

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.CapabilityNotSupported);
        ex.Message.ShouldContain("gcdump records no interfaces");
    }

    [Fact]
    public async Task A_value_filter_on_a_gcdump_is_refused_but_an_address_filter_works()
    {
        var handle = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder)).Result;

        var ex = await Should.ThrowAsync<WorkerOperationException>(
            () => Session.QueryInstancesAsync(handle, "System.String", "System.Private.CoreLib.dll", "abc", null, true, null, Ct));
        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.CapabilityNotSupported);
        ex.Message.ShouldContain("gcdump");

        var all = await Session.QueryInstancesAsync(handle, EntryType, "Leaks.dll", "0x", null, true, null, Ct);
        all.Result.Rows.TotalCount.ShouldBe(StaticCacheGcDumps.CurrentItems);
    }

    [Fact]
    public async Task Inspections_that_need_string_text_are_unavailable_and_refused_end_to_end()
    {
        var handle = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder)).Result;

        var inspections = await Session.ListInspectionsAsync(handle, Ct);
        var duplicates = inspections.Single(i => i.Name == "duplicate-strings");
        duplicates.Unavailable.ShouldBe(CapabilityDescriptions.ExplainMissing(SnapshotFeatures.StringContents));
        inspections.Single(i => i.Name == "loh").Unavailable.ShouldNotBeNull("the large object heap needs generations");
        inspections.Single(i => i.Name == "finalizable").Unavailable.ShouldNotBeNull().ShouldContain("a gcdump does not record", customMessage: "review H5");
        var scope = await Should.ThrowAsync<WorkerOperationException>(
            () => Session.QueryInstancesAsync(handle, new InstancesQuery("Fixtures.Leaks.StaticCacheEntry", "Leaks.dll", Scope: InstanceScope.Finalizable), null, Ct));
        scope.ErrorCode.ShouldBe(HeapScopeErrorCode.CapabilityNotSupported);

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => Session.RunInspectionAsync(handle, "duplicate-strings", null, Ct));
        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.CapabilityNotSupported);

        var finalizable = await Session.RunInspectionAsync(handle, "empty-collections", null, Ct).ShouldThrowAsync<WorkerOperationException>();
        finalizable.ErrorCode.ShouldBe(HeapScopeErrorCode.CapabilityNotSupported);
    }

    [Fact]
    public async Task A_diff_of_two_gcdumps_ranks_the_leaking_type_first()
    {
        var baseline = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.BaselineGcDump, _folder)).Result;
        var current = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder)).Result;

        var diff = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, "Fixtures", TypeDiffColumns.ObjectsDelta, true, null, Ct);
        var rows = (await diff.Rows.GetRowsAsync(0, 10, Ct)).Rows;

        diff.Result.ExactCounts.ShouldBeTrue();
        rows[0][0].ShouldBeOneOf(EntryType, "Fixtures.Leaks.EntryMetadata");
        Delta(rows, EntryType).ShouldBe(StaticCacheGcDumps.Growth);
    }

    [Fact]
    public async Task A_diff_of_a_full_dump_and_a_gcdump_matches_types_including_nested_ones()
    {
        var baseline = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.BaselineFullDump, _folder)).Result;
        var current = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder)).Result;

        var diff = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, TypeDiffColumns.ObjectsDelta, true, null, Ct);
        var rows = (await diff.Rows.GetRowsAsync(0, diff.Rows.Info.TotalCount, Ct)).Rows;

        Delta(rows, EntryType).ShouldBe(StaticCacheGcDumps.Growth);

        // The dictionary's entry array is nested: the full dump's name is matched in the gcdump's form, so it is one row
        // present on both sides rather than a gone row and a new row.
        var entries = rows.Single(r => r[0] == TypeNameForms.AsGcDumpName("System.Collections.Generic.Dictionary<System.Int32, Fixtures.Leaks.StaticCacheEntry>+Entry[]"));
        long.Parse(entries[2]!, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
        long.Parse(entries[3]!, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_mixed_diff_row_opens_the_full_dump_side_by_its_own_name_or_says_why_it_cannot()
    {
        // The current side is the full dump, so rows open there (review H2): a nested type's key is its full name, and a
        // row that merges several full dump types under one gcdump name has no key.
        var baseline = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.BaselineGcDump, _folder)).Result;
        var current = await Session.StartOpen(StaticCacheGcDumps.CopyTo(shared.BaselineFullDump, _folder)).Result;

        var diff = await Session.QueryTypeDiffAsync(baseline, current, TypeGrouping.Type, null, null, true, null, Ct);
        var rows = (await diff.Rows.GetRowsAsync(0, diff.Rows.Info.TotalCount, Ct)).Rows;

        const string FullName = "System.Collections.Generic.Dictionary<System.Int32, Fixtures.Leaks.StaticCacheEntry>+Entry[]";
        var entries = rows.Single(r => r[0] == TypeNameForms.AsGcDumpName(FullName));
        entries[10].ShouldBe(FullName);
        entries[1].ShouldNotBe("copies", "types matched across kinds are not compared for copies");
        var instances = await Session.QueryInstancesAsync(current, entries[10]!, entries[11], null, null, true, null, Ct);
        instances.Result.Rows.TotalCount.ShouldBeGreaterThan(0);

        var merged = rows.Where(r => r[10] is null).ToList();
        merged.ShouldNotBeEmpty("CoreLib has several <>c classes, one gcdump name");
        merged.ShouldAllBe(r => r[0]!.StartsWith("<>c", StringComparison.Ordinal) || r[0]!.Contains('<', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cli_object_on_a_gcdump_prints_the_note_and_succeeds()
    {
        var path = StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder);
        var address = "0x" + FirstAddress(path, EntryType).ToString("x", CultureInfo.InvariantCulture);

        using var cli = ChildProcess.Start(TestPaths.CliExe, "object", path, address);

        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(0, cli.Output);
        cli.Output.ShouldContain(EntryType);
        cli.Output.ShouldContain("not stored in a gcdump");
    }

    [Fact]
    public async Task Cli_json_says_which_kind_each_snapshot_is_and_whether_it_counts_unreachable_objects()
    {
        var gcdump = StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder);
        var full = StaticCacheGcDumps.CopyTo(shared.BaselineFullDump, _folder);

        using var summary = ChildProcess.Start(TestPaths.CliExe, "summary", gcdump, "--format", "json", "--top", "1");
        (await summary.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(summary.Output);
        summary.Process.ExitCode.ShouldBe(0, summary.Output);
        using (var document = Json(summary.Output))
        {
            document.RootElement.GetProperty("Kind").GetString().ShouldBe("GcDump");
            document.RootElement.GetProperty("IncludesUnreachable").GetBoolean().ShouldBeFalse();
        }

        using var diff = ChildProcess.Start(TestPaths.CliExe, "diff", full, gcdump, "--format", "json", "--top", "1");
        (await diff.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(diff.Output);
        diff.Process.ExitCode.ShouldBe(0, diff.Output);
        using (var document = Json(diff.Output))
        {
            var root = document.RootElement;
            (root.GetProperty("BaselineKind").GetString(), root.GetProperty("CurrentKind").GetString()).ShouldBe(("FullDump", "GcDump"));
            root.GetProperty("BaselineIncludesUnreachable").GetBoolean().ShouldBeTrue();
            root.GetProperty("CurrentIncludesUnreachable").GetBoolean().ShouldBeFalse();
            root.GetProperty("IncludesUnreachable").GetBoolean().ShouldBeFalse("only when both sides do");
        }

        static System.Text.Json.JsonDocument Json(string output) =>
            System.Text.Json.JsonDocument.Parse(output[output.IndexOf('{', StringComparison.Ordinal)..(output.LastIndexOf('}') + 1)]);
    }

    [Fact]
    public async Task Cli_instances_with_a_value_filter_on_a_gcdump_fails()
    {
        var path = StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder);

        using var cli = ChildProcess.Start(TestPaths.CliExe, "instances", path, "--type", "System.String", "--filter", "abc");

        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(1, cli.Output);
        cli.Output.ShouldContain("gcdump");
    }

    [Fact]
    public async Task Cli_inspect_of_an_unavailable_inspection_on_a_gcdump_fails_with_the_reason()
    {
        var path = StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder);

        using var cli = ChildProcess.Start(TestPaths.CliExe, "inspect", path, "duplicate-strings");

        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(1, cli.Output);
        cli.Output.ShouldContain("String contents are not stored in a gcdump");
    }

    [Fact]
    public async Task Canceling_a_gcdump_capture_while_the_heap_is_reported_ends_promptly_without_a_file()
    {
        // A heap of a few million objects takes seconds to report, so the cancellation lands during the session (review
        // H1: the session was never ended and the capture hung until the target exited).
        using var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", "500000", "--payload", "16");
        var path = _folder.File("canceled.gcdump");
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == "Collecting heap")
            {
                cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
            }
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var capture = new GcDumpCapturer().CaptureAsync(AllLeaksGcDump.Process(fixture.ProcessId), path, progress, null, cancel.Token);
        var ended = await Task.WhenAny(capture, Task.Delay(TimeSpan.FromSeconds(30), Ct));

        ended.ShouldBe(capture, "a canceled capture must end");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(25));
        if (capture.IsCanceled || capture.Exception?.InnerException is OperationCanceledException)
        {
            File.Exists(path).ShouldBeFalse();
            File.Exists(path + DumpCapturer.PartialSuffix).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Cli_capture_of_a_gcdump_writes_opens_and_explains_it()
    {
        using var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", "500");
        var output = _folder.File("cli.gcdump");

        using var cli = ChildProcess.Start(TestPaths.CliExe, "capture", "--pid", fixture.ProcessId.ToString(CultureInfo.InvariantCulture), "--kind", "gcdump", "--out", output, "--open");

        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue(cli.Output);
        cli.Process.ExitCode.ShouldBe(0, cli.Output);
        Core.Loading.SnapshotFileSignature.Detect(output).ShouldBe(Core.SnapshotKind.GcDump);
        cli.Output.ShouldContain("Captured LeakyConsole");
        cli.Output.ShouldContain("no field values or string text");
        cli.Output.ShouldContain("Opened:");
    }

    [Fact]
    public async Task A_gcdump_capture_is_recorded_in_its_session_as_a_gcdump()
    {
        using var fixture = await FixtureProcess.StartConsoleAsync("--leak", "StaticCache", "--count", "500");
        var service = new Application.Sessions.CaptureService(
            new ProcessDiscovery(), new DumpCapturer(), new Application.Sessions.SessionLibrary(_folder.Path), new JobTracker(TimeProvider.System));

        var result = await service.StartCapture(fixture.ProcessId, kind: SnapshotKindDto.GcDump).Result;

        result.Kind.ShouldBe(SnapshotKindDto.GcDump);
        result.Path.ShouldEndWith(".gcdump");
        var session = result.Session.ShouldNotBeNull();
        Directory.Exists(Path.Combine(session.Folder, "dac")).ShouldBeFalse("a gcdump needs no DAC");
        Application.Sessions.SessionLibrary.TryRead(session.Folder)!.Document.Snapshots.ShouldHaveSingleItem().Kind.ShouldBe(SnapshotKindDto.GcDump);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    private static ulong FirstAddress(string gcdump, string typeName)
    {
        var result = new GcDumpLoader().Load(gcdump);
        using (result.Graph)
        {
            return result.Graph.Addresses[GraphQueries.Instances(result.Graph, typeName).First()];
        }
    }

    private sealed class SyncProgress(Action<CaptureProgress> report) : IProgress<CaptureProgress>
    {
        public void Report(CaptureProgress value) => report(value);
    }

    private static long Delta(IReadOnlyList<IReadOnlyList<string?>> rows, string name) =>
        long.Parse(rows.Single(r => r[0] == name)[4]!, CultureInfo.InvariantCulture);
}
