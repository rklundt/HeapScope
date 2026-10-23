using System.Collections.Concurrent;
using System.Diagnostics;
using HeapScope.Application;
using HeapScope.Application.Jobs;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>Sprint 0.3 acceptance criteria against a real worker process.</summary>
[Trait("Category", "Integration")]
public sealed class WorkerIpcTests : IAsyncDisposable
{
    private readonly ListLoggerProvider _logs = new();
    private readonly WorkerHost _host;
    private readonly WorkerOperations _operations;
    private readonly ConcurrentQueue<WorkerState> _states = new();

    public WorkerIpcTests()
    {
        _host = CreateHost(enableTestHooks: true);
        _host.StateChanged += (_, e) => _states.Enqueue(e.State);
        _operations = new WorkerOperations(_host, new JobTracker(TimeProvider.System));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Handshake_succeeds_and_worker_runs_with_server_gc()
    {
        var info = await _host.EnsureStartedAsync(Ct);

        info.ProtocolVersion.ShouldBe(WorkerProtocol.Version);
        info.ServerGc.ShouldBeTrue();
        info.TestHooksEnabled.ShouldBeTrue();
        _host.State.ShouldBe(WorkerState.Running);
        _states.ShouldContain(WorkerState.Running);
    }

    [Fact]
    public async Task Protocol_version_mismatch_is_a_clear_error()
    {
        var ex = await Should.ThrowAsync<WorkerOperationException>(
            () => _host.InvokeAsync((service, ct) => service.GetInfoAsync(WorkerProtocol.Version + 98, ct), Ct));

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.ProtocolMismatch);
        ex.Message.ShouldContain("protocol");
    }

    [Fact]
    public async Task Handshake_protocol_mismatch_fails_the_start_and_leaves_no_worker()
    {
        var options = new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe, ClientProtocolVersion = WorkerProtocol.Version + 1 };
        await using var host = new WorkerHost(Options.Create(options), _logs);
        var states = new ConcurrentQueue<WorkerState>();
        host.StateChanged += (_, e) => states.Enqueue(e.State);
        var workersBefore = WorkerProcessIds();

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => host.EnsureStartedAsync(Ct));

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.ProtocolMismatch);
        ex.Message.ShouldContain("protocol");
        host.State.ShouldBe(WorkerState.Crashed);
        await WaitUntilAsync(() => WorkerProcessIds().Except(workersBefore).All(pid => !IsRunning(pid)));
    }

    [Fact]
    public async Task Restart_creates_a_new_generation_and_instance()
    {
        var first = await _host.EnsureStartedAsync(Ct);
        var generation = _host.Generation;

        await _host.RestartAsync(Ct);

        _host.Generation.ShouldBe(generation + 1);
        _host.Info!.InstanceId.ShouldNotBe(first.InstanceId);
        await WaitUntilAsync(() => !IsRunning(first.ProcessId));
    }

    [Fact]
    public async Task A_stopped_host_refuses_to_start_a_worker_until_started_again()
    {
        await _host.EnsureStartedAsync(Ct);
        await _host.StopAsync(Ct);

        var ex = await Should.ThrowAsync<WorkerStartException>(() => _operations.GetInfoAsync(Ct));
        ex.Message.ShouldContain("shutting down");

        await _host.StartAsync(Ct);
        (await _operations.GetInfoAsync(Ct)).ProtocolVersion.ShouldBe(WorkerProtocol.Version);
    }

    [Fact]
    public async Task Synthetic_job_beyond_the_memory_budget_is_refused()
    {
        var job = _operations.StartSyntheticJob(new SyntheticJobRequest(100_000_000, AverageReferences: 16));

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => job.Result);

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.InsufficientMemory);
        ex.Message.ShouldContain("budget");
    }

    [Fact]
    public async Task Worker_stderr_is_forwarded_into_the_client_log()
    {
        await _host.EnsureStartedAsync(Ct);

        await WaitUntilAsync(() => _logs.Records.Any(r => r.Category == "Worker" && r.Message.Contains("worker", StringComparison.Ordinal) && r.Message.Contains("started", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Synthetic_job_of_5M_objects_reports_progress_and_completes()
    {
        var job = _operations.StartSyntheticJob(new SyntheticJobRequest(5_000_000));
        var reports = new ConcurrentQueue<JobProgress>();
        _operations.Jobs.JobChanged += (_, changed) =>
        {
            if (changed.Id == job.Id && changed.Progress is { } progress)
            {
                reports.Enqueue(progress);
            }
        };

        var result = await job.Result;

        result.ObjectCount.ShouldBe(5_000_000);
        result.ReferenceCount.ShouldBe(15_000_000);
        reports.Count.ShouldBeGreaterThanOrEqualTo(3);
        job.Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task Cancellation_stops_a_hanging_call_within_a_second_and_the_worker_stays_usable()
    {
        var info = await _host.EnsureStartedAsync(Ct);
        var started = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var call = _host.InvokeAsync(
            (service, ct) => service.HangForTestingAsync(new InlineProgress(_ => started.TrySetResult()), ct),
            cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var stopwatch = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => call);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));

        var after = await _operations.GetInfoAsync(Ct);
        after.ProcessId.ShouldBe(info.ProcessId);
    }

    [Fact]
    public async Task Cancellation_stops_a_large_synthetic_job_within_a_second()
    {
        var job = _operations.StartSyntheticJob(new SyntheticJobRequest(40_000_000));
        await WaitUntilAsync(() => job.Progress is not null, TimeSpan.FromSeconds(60));

        var stopwatch = Stopwatch.StartNew();
        job.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => job.Result);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        job.Status.ShouldBe(JobStatus.Canceled);

        (await _operations.PingAsync(Ct)).ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Killing_the_worker_faults_the_call_and_the_next_call_restarts_it()
    {
        var info = await _host.EnsureStartedAsync(Ct);
        var started = new TaskCompletionSource();
        var call = _host.InvokeAsync(
            (service, ct) => service.HangForTestingAsync(new InlineProgress(_ => started.TrySetResult()), ct),
            Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using (var worker = Process.GetProcessById(info.ProcessId))
        {
            worker.Kill();
        }

        var crash = await Should.ThrowAsync<WorkerCrashedException>(() => call);
        crash.ExitCode.ShouldNotBeNull();
        await WaitUntilAsync(() => _states.Contains(WorkerState.Crashed));

        var restarted = await _operations.GetInfoAsync(Ct);
        restarted.ProcessId.ShouldNotBe(info.ProcessId);
        _host.State.ShouldBe(WorkerState.Running);
    }

    [Fact]
    public async Task Crash_test_hook_is_reported_as_a_crash()
    {
        await _host.EnsureStartedAsync(Ct);

        await Should.ThrowAsync<WorkerCrashedException>(() => _host.InvokeAsync((service, ct) => service.CrashForTestingAsync(ct), Ct));

        (await _operations.GetInfoAsync(Ct)).ProtocolVersion.ShouldBe(WorkerProtocol.Version);
    }

    [Fact]
    public async Task Test_hooks_are_refused_unless_enabled()
    {
        await using var host = CreateHost(enableTestHooks: false);

        var ex = await Should.ThrowAsync<WorkerOperationException>(
            () => host.InvokeAsync((service, ct) => service.HangForTestingAsync(null, ct), Ct));

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.TestHooksDisabled);
    }

    [Fact]
    public async Task Paging_honors_offset_and_count_and_caps_the_page_size()
    {
        var result = await _operations.StartSyntheticJob(new SyntheticJobRequest(60_000, TypeCount: 30_000)).Result;
        var id = result.TypeSummary.Id;
        var total = result.TypeSummary.TotalCount;
        total.ShouldBeGreaterThan(WorkerProtocol.MaxPageSize);
        result.TypeSummary.Columns.Select(c => c.Name).ShouldBe(["Name", "Objects", "Bytes", "Percent"]);

        var page = await _operations.GetRowsAsync(id, 5, 7, Ct);
        page.Offset.ShouldBe(5);
        page.Rows.Count.ShouldBe(7);
        page.TotalCount.ShouldBe(total);

        (await _operations.GetRowsAsync(id, 0, 50_000, Ct)).Rows.Count.ShouldBe(WorkerProtocol.MaxPageSize);
        (await _operations.GetRowsAsync(id, total - 3, 100, Ct)).Rows.Count.ShouldBe(3);
        (await _operations.GetRowsAsync(id, total + 10, 100, Ct)).Rows.Count.ShouldBe(0);

        var invalid = await Should.ThrowAsync<WorkerOperationException>(() => _operations.GetRowsAsync(id, -1, 10, Ct));
        invalid.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);

        await _operations.ReleaseResultSetAsync(id, Ct);
        var released = await Should.ThrowAsync<WorkerOperationException>(() => _operations.GetRowsAsync(id, 0, 1, Ct));
        released.ErrorCode.ShouldBe(HeapScopeErrorCode.ResultSetNotFound);
        released.Message.ShouldContain("Run the query again");
    }

    [Fact]
    public async Task A_full_page_costs_little_to_receive_in_the_client_process()
    {
        // Sprint 1.3 decision 6: the formatter changes only above 50 MB or 200 ms per 10,000-row page.
        // The grid itself asks for pages of VirtualRowList.PageSize (200) rows; the full page is the CLI's.
        var result = await _operations.StartSyntheticJob(new SyntheticJobRequest(60_000, TypeCount: 30_000)).Result;
        var id = result.TypeSummary.Id;
        var full = await MeasurePagesAsync(id, WorkerProtocol.MaxPageSize);
        var grid = await MeasurePagesAsync(id, 200);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"10,000-row page: median {full.MedianMs:F1} ms, {full.MaxBytes / 1048576.0:F1} MB; 200-row page: median {grid.MedianMs:F1} ms, {grid.MaxBytes / 1024.0:F0} KB");
        full.MaxBytes.ShouldBeLessThan(50L * 1024 * 1024);
        full.MedianMs.ShouldBeLessThan(200);
    }

    private async Task<(double MedianMs, long MaxBytes)> MeasurePagesAsync(Guid id, int count)
    {
        for (var i = 0; i < 3; i++)
        {
            await _operations.GetRowsAsync(id, 0, count, Ct);
        }

        var times = new List<double>();
        long maxBytes = 0;
        for (var i = 0; i < 9; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            var page = await _operations.GetRowsAsync(id, count, count, Ct);
            watch.Stop();
            maxBytes = Math.Max(maxBytes, GC.GetTotalAllocatedBytes(precise: true) - before);
            times.Add(watch.Elapsed.TotalMilliseconds);
            page.Rows.Count.ShouldBe(count);
        }

        times.Sort();
        return (times[times.Count / 2], maxBytes);
    }

    [Fact]
    public async Task Least_recently_used_result_sets_are_evicted()
    {
        var first = (await _operations.StartSyntheticJob(new SyntheticJobRequest(100, TypeCount: 5)).Result).TypeSummary.Id;
        for (var i = 0; i < 32; i++)
        {
            await _operations.StartSyntheticJob(new SyntheticJobRequest(100, TypeCount: 5)).Result;
        }

        var evicted = await Should.ThrowAsync<WorkerOperationException>(() => _operations.GetRowsAsync(first, 0, 1, Ct));
        evicted.ErrorCode.ShouldBe(HeapScopeErrorCode.ResultSetNotFound);
    }

    [Fact]
    public async Task Orderly_stop_ends_the_worker_process()
    {
        var info = await _host.EnsureStartedAsync(Ct);

        await _host.StopAsync(Ct);

        _host.State.ShouldBe(WorkerState.Stopped);
        await WaitUntilAsync(() => !IsRunning(info.ProcessId));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private WorkerHost CreateHost(bool enableTestHooks) => new(
        Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe, EnableTestHooks = enableTestHooks }),
        _logs);

    private static int[] WorkerProcessIds() =>
        [.. Process.GetProcessesByName("HeapScope.Worker").Select(p =>
        {
            using (p)
            {
                return p.Id;
            }
        })];

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not met in time");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class InlineProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }
}
