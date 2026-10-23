namespace HeapScope.Application.Sessions;

// Front ends see only Application and Contracts (ARCHITECTURE.md 3.1), so capture results and failures are expressed
// in these types rather than HeapScope.Capture's.

/// <summary>Whether a process can be dumped by this HeapScope process (ADR-014).</summary>
public enum ProcessElevation
{
    NotElevated = 0,
    Elevated = 1,
    Unknown = 2,
}

/// <summary>A running .NET process as front ends show it. Details that could not be read are null.</summary>
/// <param name="NeedsAdministrator">True when HeapScope must run as administrator to capture it.</param>
public sealed record ProcessEntry(
    int ProcessId,
    string Name,
    string? CommandLine,
    string? RuntimeVersion,
    ProcessElevation Elevation,
    bool NeedsAdministrator,
    long? WorkingSetBytes,
    long? PrivateBytes);

/// <summary>Why a capture failed.</summary>
public enum CaptureFailure
{
    /// <summary>Not classified (see the log).</summary>
    Unknown = 0,

    /// <summary>The target is elevated or another user's: run HeapScope as administrator.</summary>
    NeedsAdministrator = 1,

    /// <summary>The dump would not fit on the disk.</summary>
    InsufficientDiskSpace = 2,

    /// <summary>No running .NET process with that id.</summary>
    ProcessNotFound = 3,

    /// <summary>The process exited or stopped answering during the capture.</summary>
    TargetExited = 4,

    /// <summary>The dump file exists already; HeapScope never overwrites one.</summary>
    FileExists = 5,

    /// <summary>The runtime could not write the dump (disk full, the target's account cannot write the folder).</summary>
    WriteFailed = 6,
}

/// <summary>The capture use cases front ends depend on (faked in their tests).</summary>
public interface ICaptureService
{
    /// <summary>The session library captures are recorded in.</summary>
    SessionLibrary Library { get; }

    /// <summary>True when this HeapScope process runs as administrator and can capture elevated processes.</summary>
    bool IsHeapScopeElevated { get; }

    /// <summary>The .NET processes HeapScope can see, read off the calling thread.</summary>
    Task<IReadOnlyList<ProcessEntry>> ListProcessesAsync(CancellationToken cancellationToken);

    /// <summary>Captures a full dump as a tracked job; fails with <see cref="CaptureFailedException"/>.</summary>
    Jobs.TrackedJob<CaptureResult> StartCapture(int processId, string? outputPath = null, string? label = null);
}

/// <summary>A capture failed; the message is plain language with a next step.</summary>
public sealed class CaptureFailedException : Exception
{
    public CaptureFailedException()
    {
    }

    public CaptureFailedException(string message)
        : base(message)
    {
    }

    public CaptureFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CaptureFailedException(CaptureFailure reason, string message, Exception? innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public CaptureFailure Reason { get; } = CaptureFailure.Unknown;
}
