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
using HeapScope.Analysis.Allocations;
using HeapScope.Application.Allocations;
using HeapScope.Application.Jobs;
using HeapScope.Application.Sessions;
using HeapScope.Application.Workers;
using HeapScope.Capture;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Allocations;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Loaders.Trace;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>One allocations trace of LeakyApi under LoadGen (sprint 2.5), shared by the tests that read it.</summary>
public sealed class LeakyApiAllocations : IAsyncLifetime
{
    private readonly TempFolder _root = new();

    public FixtureProcess Api { get; private set; } = null!;

    public string TracePath => _root.File("allocations.nettrace");

    public TraceRecording Recording { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Api = await FixtureProcess.StartApiAsync();

        // The leaks' methods are compiled before recording starts, so only the rundown names them (the 2.5 review).
        await Api.PostWorkAsync(1, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var recording = TraceRecorder.RecordAsync(Api.ProcessId, TracePath, TraceProfile.Allocations, TimeSpan.FromMinutes(5), stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(1));
        using (var load = ChildProcess.Start(TestPaths.LoadGenExe, "--url", Api.Url!.ToString(), "--requests", "2000", "--concurrency", "8"))
        {
            if (!await load.WaitForExitAsync(TimeSpan.FromMinutes(3)) || load.Process.ExitCode != 0)
            {
                throw new InvalidOperationException("LoadGen did not finish: " + load.Output);
            }
        }

        await stop.CancelAsync();
        Recording = await recording;
    }

    public ValueTask DisposeAsync()
    {
        Api?.Dispose();
        _root.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Sprint 2.5: reading allocation traces, the queries through the real worker, and the acceptance criterion on LeakyApi.</summary>
[Trait("Category", "Integration")]
public sealed class AllocationTraceTests : IClassFixture<LeakyApiAllocations>, IAsyncDisposable
{
    private readonly LeakyApiAllocations _shared;
    private readonly TempFolder _scratch = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly AllocationSession _session;
    private readonly SessionLibrary _library;

    public AllocationTraceTests(LeakyApiAllocations shared)
    {
        _shared = shared;

        // Never the user's session folders (tests must not write there).
        _library = new SessionLibrary(_scratch.File("sessions"));
        _session = new AllocationSession(_host, new JobTracker(TimeProvider.System), new TraceService(), _library);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_trace_holds_samples_of_LeakyApi_with_named_stacks_and_leaves_no_temporary_file()
    {
        var trace = AllocationTraceReader.Read(_shared.TracePath, null, _scratch.Path, Ct);

        trace.SampleCount.ShouldBeGreaterThan(100, "2,000 requests allocate megabytes, a sample about every 100 KB");
        trace.Info.ProcessId.ShouldBe(_shared.Api.ProcessId);
        trace.SampleBytes.Sum().ShouldBeGreaterThan(10_000_000);
        trace.Types.ShouldContain("System.Byte[]");
        trace.Frames.ShouldContain(f => f.Contains("Leak.Grow", StringComparison.Ordinal), "methods compiled before recording are named from the rundown");
        trace.Info.HasRundown.ShouldBeTrue();
        trace.Info.ConversionIssues.ShouldBe(0, string.Join(" | ", trace.Info.FirstIssues ?? []));
        trace.Info.SamplesWithUnresolvedFrames.ShouldBeLessThan(trace.SampleCount / 20, "nearly every frame is named");
        trace.SampleKinds.ShouldContain(AllocationKind.Small);
        Directory.GetFiles(_scratch.Path).ShouldBeEmpty("the .etlx is deleted after reading");
    }

    [Fact]
    public void Acceptance_the_leaking_allocation_site_is_in_the_top_call_stacks()
    {
        var trace = AllocationTraceReader.Read(_shared.TracePath, null, _scratch.Path, Ct);

        var top = AllocationProfile.TopStacks(trace, AllocationFilter.All, 10, Ct);
        var stacks = top.Select(s => AllocationProfile.FramesOf(trace, s.Node).Select(f => trace.Frames[f]).ToArray()).ToArray();
        var report = string.Join(Environment.NewLine, top.Zip(stacks, (s, frames) => string.Create(CultureInfo.InvariantCulture, $"{s.Bytes:N0} B: {string.Join(" < ", frames.Take(4))}")));
        stacks.ShouldContain(frames => frames.Any(f => f.StartsWith("Fixtures.Leaks.", StringComparison.Ordinal) && f.Contains("Leak.Grow", StringComparison.Ordinal)), report);
    }

    [Fact]
    public async Task A_recording_whose_process_exits_has_no_rundown_and_says_so()
    {
        using var api = await FixtureProcess.StartApiAsync();
        await api.PostWorkAsync(1, Ct);
        var path = _scratch.File("exited.nettrace");
        var recording = TraceRecorder.RecordAsync(api.ProcessId, path, TraceProfile.Allocations, TimeSpan.FromMinutes(2), Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await api.PostWorkAsync(200, Ct);
        api.Dispose();
        (await recording).StoppedEarly.ShouldBeTrue();

        var handle = await _session.StartOpen(path).Result;

        handle.Descriptor.HasRundown.ShouldBeFalse();
        AllocationTexts.NotesFor(handle.Descriptor).ShouldContain("before the runtime named its methods");
    }

    [Fact]
    public async Task A_trace_with_no_allocation_samples_is_refused_with_the_profile_to_use()
    {
        var path = _scratch.File("cpu.nettrace");
        var recording = TraceRecorder.RecordAsync(_shared.Api.ProcessId, path, TraceProfile.Cpu, TimeSpan.FromSeconds(2), Ct);
        await recording;

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => _session.StartOpen(path).Result);

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.SnapshotFormat);
        ex.Message.ShouldContain("has no allocation samples");
    }

    [Fact]
    public void A_file_that_is_not_a_trace_is_refused_with_a_plain_message()
    {
        var path = _scratch.File("notes.nettrace");
        File.WriteAllText(path, "These are notes, not a trace; long enough to read a header from.");

        var ex = Should.Throw<SnapshotFormatException>(() => AllocationTraceReader.Read(path, null, _scratch.File("etlx"), Ct));

        ex.Message.ShouldContain("is not an EventPipe trace");
    }

    [Fact]
    public void A_canceled_read_stops_and_leaves_no_temporary_file()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var folder = _scratch.File("canceled");

        Should.Throw<OperationCanceledException>(() => AllocationTraceReader.Read(_shared.TracePath, null, folder, cancel.Token));

        (Directory.Exists(folder) ? Directory.GetFiles(folder) : []).ShouldBeEmpty();
    }

    [Fact]
    public async Task Acceptance_through_the_worker_the_top_call_stacks_hold_the_leaking_allocation_site()
    {
        var handle = await _session.StartOpen(_shared.TracePath).Result;

        handle.Descriptor.Samples.ShouldBeGreaterThan(100);
        handle.Descriptor.ProcessId.ShouldBe(_shared.Api.ProcessId);
        var stacks = await _session.QueryStacksAsync(handle, AllocationScope.All, 10, Ct);
        stacks.SampledBytes.ShouldBe(handle.Descriptor.SampledBytes);
        stacks.Stacks.ShouldContain(s => s.Frames.Any(f => f.StartsWith("Fixtures.Leaks.", StringComparison.Ordinal) && f.Contains("Leak.Grow", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Types_trees_and_the_timeline_add_up_to_the_same_sampled_bytes()
    {
        var handle = await _session.StartOpen(_shared.TracePath).Result;
        var total = handle.Descriptor.SampledBytes;

        var types = await _session.QueryTypesAsync(handle, AllocationScope.All, Ct);
        var typeRows = await RetainedSizesTests.AllRowsAsync(types.Rows);
        typeRows.Sum(r => long.Parse(r[AllocationTypeColumns.Bytes]!, CultureInfo.InvariantCulture)).ShouldBe(total);
        types.Rows.Info.Columns.Single(c => c.Name == AllocationTypeColumns.Bytes).Bound.ShouldBe(ValueBound.Estimate);

        var timeline = await _session.QueryTimelineAsync(handle, 1, Ct);
        timeline.Buckets.Sum(b => b.Bytes).ShouldBe(total);

        var callTree = await _session.QueryTreeAsync(handle, AllocationScope.All, AllocationTreeKindDto.CallTree, [], null, null, Ct);
        var roots = await RetainedSizesTests.AllRowsAsync(callTree.Rows);
        roots.Where(r => r[AllocationTreeColumns.Kind] == AllocationRowKinds.Frame).Sum(r => long.Parse(r[AllocationTreeColumns.Total]!, CultureInfo.InvariantCulture))
            .ShouldBe(total, "the top level of the call tree covers every sample (no more rows on a small trace)");

        // Back traces: the first allocating frame's callers, revealed in place.
        var top = await _session.QueryTreeAsync(handle, AllocationScope.All, AllocationTreeKindDto.BackTraces, [], null, null, Ct);
        var first = (await RetainedSizesTests.AllRowsAsync(top.Rows))[0];
        var key = first[AllocationTreeColumns.Key]!;
        var open = await _session.QueryTreeAsync(handle, AllocationScope.All, AllocationTreeKindDto.BackTraces, [key], key, null, Ct);
        open.Result.RevealIndex.ShouldBe(0);
        var rows = await RetainedSizesTests.AllRowsAsync(open.Rows);
        rows.Where(r => r[AllocationTreeColumns.ParentKey] == key).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_type_or_an_interval_narrows_the_answer_and_bad_ones_are_refused()
    {
        var handle = await _session.StartOpen(_shared.TracePath).Result;
        var half = handle.Descriptor.DurationSeconds / 2;

        var bytes = await _session.QueryStacksAsync(handle, new AllocationScope(TypeName: "System.Byte[]"), 5, Ct);
        bytes.SampledBytes.ShouldBeLessThan(handle.Descriptor.SampledBytes);
        var early = await _session.QueryStacksAsync(handle, new AllocationScope(0, half), 5, Ct);
        var late = await _session.QueryStacksAsync(handle, new AllocationScope(half), 5, Ct);
        (early.SampledBytes + late.SampledBytes).ShouldBe(handle.Descriptor.SampledBytes);

        var unknown = await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryStacksAsync(handle, new AllocationScope(TypeName: "No.Such.Type"), 5, Ct));
        unknown.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
        var empty = await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryStacksAsync(handle, new AllocationScope(2, 1), 5, Ct));
        empty.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task A_trace_the_worker_no_longer_has_is_read_again()
    {
        var handle = await _session.StartOpen(_shared.TracePath).Result;
        var id = handle.Descriptor.TraceId;
        await _host.InvokeAsync((service, ct) => service.CloseTraceAsync(id, ct), Ct);

        var timeline = await _session.QueryTimelineAsync(handle, 1, Ct);

        timeline.Buckets.ShouldNotBeEmpty();
        handle.Descriptor.TraceId.ShouldNotBe(id);
    }

    [Fact]
    public async Task Record_allocations_writes_into_the_session_folder_and_opens_the_trace()
    {
        using var stop = new CancellationTokenSource();
        var job = _session.StartRecord(_shared.Api.ProcessId, "LeakyApi", TimeSpan.FromSeconds(30), stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await _shared.Api.PostWorkAsync(50, Ct);
        await stop.CancelAsync();

        var handle = await job.Result;

        Path.GetDirectoryName(handle.Path).ShouldBe(_library.Current.Folder);
        handle.Path.ShouldEndWith("-allocations.nettrace");
        handle.Descriptor.Samples.ShouldBeGreaterThan(0);
        _session.Traces.ShouldContain(handle);
    }

    [Fact]
    public async Task Cli_allocations_prints_the_top_types_and_stacks_in_every_format()
    {
        var (tableCode, table) = await RunCliAsync("allocations", _shared.TracePath, "--stacks", "5");
        tableCode.ShouldBe(0, table);
        table.ShouldContain("System.Byte[]");
        table.ShouldContain("Leak.Grow(");
        table.ShouldContain("Note: Estimated from samples");

        var (jsonCode, json) = await RunCliAsync("allocations", _shared.TracePath, "--format", "json", "--stacks", "3", "--types", "2");
        jsonCode.ShouldBe(0, json);

        // The notes go to stderr, which the test reads merged with stdout in arrival order.
        json = string.Join('\n', json.Split('\n').Where(line => !line.StartsWith("Note:", StringComparison.Ordinal)));
        using (var document = System.Text.Json.JsonDocument.Parse(json[json.IndexOf('{', StringComparison.Ordinal)..(json.LastIndexOf('}') + 1)]))
        {
            var root = document.RootElement;
            root.GetProperty("BytesAre").GetString().ShouldBe("Estimate");
            root.GetProperty("Types").GetArrayLength().ShouldBe(2);
            root.GetProperty("Stacks").GetArrayLength().ShouldBe(3);
        }

        var (csvCode, csv) = await RunCliAsync("allocations", _shared.TracePath, "--format", "csv", "--view", "stacks", "--stacks", "2");
        csvCode.ShouldBe(0, csv);
        csv.ShouldContain("Rank,Bytes,Samples,Percent,Frames");

        var (typesCode, typesOnly) = await RunCliAsync("allocations", _shared.TracePath, "--view", "types");
        typesCode.ShouldBe(0, typesOnly);
        typesOnly.ShouldContain("All types from");

        var (usageCode, usage) = await RunCliAsync("allocations", _shared.TracePath, "--format", "csv");
        usageCode.ShouldBe(64, usage);

        var notTrace = _scratch.File("notes.txt");
        await File.WriteAllTextAsync(notTrace, "These are notes, not a trace; long enough to read a header from.", Ct);
        var (badCode, bad) = await RunCliAsync("allocations", notTrace);
        badCode.ShouldBe(1, bad);
        bad.ShouldContain("is not an EventPipe trace");
    }

    private static async Task<(int Code, string Output)> RunCliAsync(params string[] arguments)
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, arguments);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(180))).ShouldBeTrue(cli.Output);
        return (cli.Process.ExitCode, cli.Output);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _scratch.Dispose();
    }
}
