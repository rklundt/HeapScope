using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

/// <summary>Paging through a result set that the worker may evict or lose (sprint 1.3).</summary>
public sealed class ResultSetHandleTests
{
    private readonly PagingWorker _worker = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_pages_that_find_the_set_gone_run_the_query_once()
    {
        var handle = _worker.NewHandle();
        _worker.Evict(handle.Info.Id);
        _worker.PageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = handle.GetRowsAsync(0, 2, Ct);
        var second = handle.GetRowsAsync(2, 2, Ct);
        _worker.PageGate.SetResult();
        await Task.WhenAll(first, second);

        handle.Requeries.ShouldBe(1);
        _worker.Queries.ShouldBe(2, "the original query and one re-run");
        (await first).Rows.Count.ShouldBe(2);
        (await second).Rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_set_lost_again_after_the_rerun_fails_with_the_worker_error()
    {
        var handle = _worker.NewHandle();
        _worker.Evict(handle.Info.Id);
        _worker.EvictEveryNewSet = true;

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => handle.GetRowsAsync(0, 2, Ct));

        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.ResultSetNotFound);
        handle.Requeries.ShouldBe(1);
    }

    [Fact]
    public async Task A_released_handle_never_runs_the_query_again()
    {
        var handle = _worker.NewHandle();

        await handle.ReleaseAsync(Ct);
        await handle.ReleaseAsync(Ct);

        await Should.ThrowAsync<ObjectDisposedException>(() => handle.GetRowsAsync(0, 2, Ct));
        _worker.Queries.ShouldBe(1);
        _worker.Live.ShouldBeEmpty();
        handle.IsReleased.ShouldBeTrue();
    }

    [Fact]
    public async Task A_page_in_flight_when_its_handle_is_released_leaves_no_set_behind()
    {
        var handle = _worker.NewHandle();
        _worker.PageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = handle.GetRowsAsync(0, 2, Ct);

        await handle.ReleaseAsync(Ct);
        _worker.PageGate.SetResult();

        await Should.ThrowAsync<Exception>(() => page);
        _worker.Queries.ShouldBe(1, "a page failing because its set was released does not re-run the query");
        _worker.Live.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rerun_in_progress_when_the_handle_is_released_has_its_new_set_released_too()
    {
        var handle = _worker.NewHandle();
        _worker.Evict(handle.Info.Id);
        _worker.QueryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = handle.GetRowsAsync(0, 2, Ct);
        await WaitUntilAsync(() => _worker.Queries == 2);

        var release = handle.ReleaseAsync(Ct);
        _worker.QueryGate.SetResult();
        await release;
        await page.ContinueWith(_ => { }, Ct, TaskContinuationOptions.None, TaskScheduler.Default);

        _worker.Live.ShouldBeEmpty();
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

    /// <summary>A worker holding ten-row result sets, with gates to hold queries and pages in flight.</summary>
    private sealed class PagingWorker : IWorkerClient, IWorkerService
    {
        private readonly HashSet<Guid> _live = [];
        private readonly Lock _lock = new();

        public TaskCompletionSource? PageGate { get; set; }

        public TaskCompletionSource? QueryGate { get; set; }

        public bool EvictEveryNewSet { get; set; }

        public int Queries { get; private set; }

        public IReadOnlyCollection<Guid> Live
        {
            get
            {
                lock (_lock)
                {
                    return [.. _live];
                }
            }
        }

        public WorkerState State => WorkerState.Running;

        public WorkerInfo? Info => null;

        public int Generation => 1;

        public event EventHandler<WorkerStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public ResultSetHandle NewHandle() => new(this, RunQueryAsync(CancellationToken.None).GetAwaiter().GetResult(), RunQueryAsync);

        public void Evict(Guid id)
        {
            lock (_lock)
            {
                _live.Remove(id);
            }
        }

        public Task<WorkerInfo> EnsureStartedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<T> InvokeAsync<T>(Func<IWorkerService, CancellationToken, Task<T>> call, CancellationToken cancellationToken) => call(this, cancellationToken);

        public Task InvokeAsync(Func<IWorkerService, CancellationToken, Task> call, CancellationToken cancellationToken) => call(this, cancellationToken);

        public Task RestartAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken)
        {
            if (PageGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            lock (_lock)
            {
                if (!_live.Contains(resultSetId))
                {
                    throw new WorkerOperationException(HeapScopeErrorCode.ResultSetNotFound, "Gone. Run the query again.", null);
                }
            }

            return new ResultPage(resultSetId, offset, 10, [.. Enumerable.Range(offset, count).Select(i => (IReadOnlyList<string?>)[$"Row{i}"])]);
        }

        public Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken)
        {
            Evict(resultSetId);
            return Task.CompletedTask;
        }

        public Task<WorkerInfo> GetInfoAsync(int clientProtocolVersion, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SyntheticJobResult> RunSyntheticJobAsync(SyntheticJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TypeSummaryResult> QueryTypeSummaryAsync(TypeSummaryRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TypeDiffResult> QueryTypeDiffAsync(TypeDiffRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CrashForTestingAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        private async Task<ResultSetInfo> RunQueryAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Queries++;
            }

            if (QueryGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            var id = Guid.NewGuid();
            lock (_lock)
            {
                if (!EvictEveryNewSet)
                {
                    _live.Add(id);
                }
            }

            return new ResultSetInfo(id, 10, [new("Name", ResultColumnKind.Text, IsKey: true)]);
        }
    }
}
