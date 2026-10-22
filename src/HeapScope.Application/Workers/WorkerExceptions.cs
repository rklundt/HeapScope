using HeapScope.Contracts;

namespace HeapScope.Application.Workers;

/// <summary>The worker process exited or the pipe broke while a call was in flight (ARCHITECTURE.md 4.3).</summary>
public sealed class WorkerCrashedException : Exception
{
    public WorkerCrashedException()
        : this("The analysis worker stopped unexpectedly.")
    {
    }

    public WorkerCrashedException(string message)
        : base(message)
    {
    }

    public WorkerCrashedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkerCrashedException(int? exitCode, Exception? innerException)
        : base(Describe(exitCode), innerException)
    {
        ExitCode = exitCode;
    }

    /// <summary>The worker's exit code, when it exited.</summary>
    public int? ExitCode { get; }

    private static string Describe(int? exitCode) => exitCode is int code
        ? $"The analysis worker stopped unexpectedly (exit code {code}). It restarts automatically on the next operation; see the log for its last messages."
        : "The connection to the analysis worker was lost. It restarts automatically on the next operation; see the log for details.";
}

/// <summary>The worker rejected an operation with a <see cref="HeapScopeErrorCode"/>.</summary>
public sealed class WorkerOperationException : Exception
{
    public WorkerOperationException()
    {
    }

    public WorkerOperationException(string message)
        : base(message)
    {
    }

    public WorkerOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkerOperationException(HeapScopeErrorCode errorCode, string message, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public HeapScopeErrorCode ErrorCode { get; } = HeapScopeErrorCode.Unknown;
}

/// <summary>The worker could not be started or connected.</summary>
public sealed class WorkerStartException : Exception
{
    public WorkerStartException()
    {
    }

    public WorkerStartException(string message)
        : base(message)
    {
    }

    public WorkerStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
