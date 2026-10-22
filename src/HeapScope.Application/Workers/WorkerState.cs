namespace HeapScope.Application.Workers;

/// <summary>Lifecycle state of the analysis worker (ARCHITECTURE.md 4.2, 4.3).</summary>
public enum WorkerState
{
    NotStarted,
    Starting,
    Running,
    Crashed,
    Stopped,
}

/// <summary>Raised when the worker changes state; <see cref="Message"/> explains crashes.</summary>
public sealed class WorkerStateChangedEventArgs(WorkerState state, string? message) : EventArgs
{
    public WorkerState State { get; } = state;

    public string? Message { get; } = message;
}
