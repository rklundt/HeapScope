using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.Wpf.Tests;

/// <summary>A worker client that calls an in-process fake service.</summary>
internal sealed class FakeWorkerClient : IWorkerClient
{
    public FakeWorkerService Service { get; } = new();

    public WorkerState State { get; private set; } = WorkerState.NotStarted;

    public WorkerInfo? Info { get; } = new(Guid.NewGuid(), WorkerProtocol.Version, "0.0.0", 1234, true, 0, 0, 0, false);

    public int RestartCount { get; private set; }

    public int Generation { get; private set; } = 1;

    public event EventHandler<WorkerStateChangedEventArgs>? StateChanged;

    public Task<WorkerInfo> EnsureStartedAsync(CancellationToken cancellationToken) => Task.FromResult(Info!);

    public Task<T> InvokeAsync<T>(Func<IWorkerService, CancellationToken, Task<T>> call, CancellationToken cancellationToken) =>
        call(Service, cancellationToken);

    public Task InvokeAsync(Func<IWorkerService, CancellationToken, Task> call, CancellationToken cancellationToken) =>
        call(Service, cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken)
    {
        RestartCount++;
        Raise(WorkerState.Running, null);
        return Task.CompletedTask;
    }

    public void Raise(WorkerState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(this, new WorkerStateChangedEventArgs(state, message));
    }
}

/// <summary>A worker service whose synthetic job finishes only when the test says so.</summary>
internal sealed class FakeWorkerService : IWorkerService
{
    private TaskCompletionSource<SyntheticJobResult> _synthetic = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IProgress<JobProgress>? LastProgress { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public List<Guid> Released { get; } = [];

    public void CompleteSynthetic(int objects) => _synthetic.TrySetResult(new SyntheticJobResult(
        objects, objects * 3, (ulong)objects * 48, objects * 34L, 0.5, new ResultSetInfo(Guid.NewGuid(), 1, [])));

    public Task<WorkerInfo> GetInfoAsync(int clientProtocolVersion, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkerInfo(Guid.NewGuid(), WorkerProtocol.Version, "0.0.0", 1234, true, 1, 2, 3, false));

    public Task<SyntheticJobResult> RunSyntheticJobAsync(SyntheticJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        _synthetic = new TaskCompletionSource<SyntheticJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        LastProgress = progress;
        LastToken = cancellationToken;
        cancellationToken.Register(() => _synthetic.TrySetCanceled(cancellationToken));
        return _synthetic.Task;
    }

    public Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(new ResultPage(resultSetId, offset, 0, []));

    public Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken)
    {
        Released.Add(resultSetId);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CrashForTestingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
}
