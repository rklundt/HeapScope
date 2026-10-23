using System.IO;
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

    public Task<WorkerInfo> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        State = WorkerState.Running;
        return Task.FromResult(Info!);
    }

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

    public List<OpenSnapshotRequest> Opened { get; } = [];

    public List<Guid> Closed { get; } = [];

    /// <summary>Rows returned by every page: type, count, bytes.</summary>
    public List<IReadOnlyList<string?>> Rows { get; } = [];

    public Task<SnapshotProbe> ProbeSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.InvalidArgument, $"The file {path} does not exist.", null);
        }

        var cache = path + ".hsidx";
        var state = File.Exists(cache) ? SnapshotCacheState.Valid : SnapshotCacheState.Missing;
        return Task.FromResult(new SnapshotProbe(path, new FileInfo(path).Length, state, cache, null, false));
    }

    /// <summary>When set, the next opens fail with it.</summary>
    public Exception? OpenFailure { get; set; }

    public Task<SnapshotDescriptor> OpenSnapshotAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (OpenFailure is { } failure)
        {
            return Task.FromException<SnapshotDescriptor>(failure);
        }

        Opened.Add(request);
        return Task.FromResult(new SnapshotDescriptor(
            Guid.NewGuid(), request.Path, SnapshotKindDto.FullDump, SnapshotFeatures.ObjectGraph, "App", 42, "10.0.13", null,
            1000, 3000, 10, 5, 48_000, SnapshotMemoryMode.InMemory, false, request.Path + ".hsidx", 1.5, ["A note."]));
    }

    public Task CloseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        Closed.Add(snapshotId);
        return Task.CompletedTask;
    }

    public Task<ResultSetInfo> GetTypeSummaryAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        Task.FromResult(new ResultSetInfo(Guid.NewGuid(), Rows.Count, []));

    public Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(new ResultPage(resultSetId, offset, Rows.Count, [.. Rows.Skip(offset).Take(count)]));

    public Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken)
    {
        Released.Add(resultSetId);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CrashForTestingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Scripted dialogs.</summary>
internal sealed class FakeUserInteraction : HeapScope.UI.Wpf.Services.IUserInteraction
{
    public string? NextFile { get; set; }

    public bool Answer { get; set; } = true;

    public List<string> Questions { get; } = [];

    public string? PickDumpFile() => NextFile;

    public bool Confirm(string title, string message)
    {
        Questions.Add(message);
        return Answer;
    }
}
