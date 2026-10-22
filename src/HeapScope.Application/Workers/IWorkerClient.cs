using HeapScope.Contracts;

namespace HeapScope.Application.Workers;

/// <summary>
/// Access to the analysis worker. Starts it on first use and restarts it transparently after a crash
/// (ARCHITECTURE.md 4.3). Calls translate transport failures into <see cref="WorkerCrashedException"/> and worker
/// errors into <see cref="WorkerOperationException"/>.
/// </summary>
public interface IWorkerClient
{
    WorkerState State { get; }

    /// <summary>Information from the last successful handshake.</summary>
    WorkerInfo? Info { get; }

    /// <summary>
    /// Incremented each time a new worker process is connected. Worker-owned ids (snapshots, result sets) obtained
    /// under an older generation are gone (ARCHITECTURE.md 4.3).
    /// </summary>
    int Generation { get; }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    event EventHandler<WorkerStateChangedEventArgs>? StateChanged;

    /// <summary>Starts (or restarts) the worker when it is not running and returns the handshake information.</summary>
    Task<WorkerInfo> EnsureStartedAsync(CancellationToken cancellationToken);

    /// <summary>Calls the worker, starting it first when needed.</summary>
    Task<T> InvokeAsync<T>(Func<IWorkerService, CancellationToken, Task<T>> call, CancellationToken cancellationToken);

    /// <summary>Calls the worker, starting it first when needed.</summary>
    Task InvokeAsync(Func<IWorkerService, CancellationToken, Task> call, CancellationToken cancellationToken);

    /// <summary>Stops the worker (orderly when possible) and starts a new one.</summary>
    Task RestartAsync(CancellationToken cancellationToken);
}
