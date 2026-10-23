namespace HeapScope.Capture;

/// <summary>The target process exited or stopped answering on its diagnostics port.</summary>
public sealed class TargetUnavailableException : Exception
{
    public TargetUnavailableException()
    {
    }

    public TargetUnavailableException(string message)
        : base(message)
    {
    }

    public TargetUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The dump file already exists; HeapScope never overwrites a dump.</summary>
public sealed class DumpFileExistsException : Exception
{
    public DumpFileExistsException()
    {
    }

    public DumpFileExistsException(string message)
        : base(message)
    {
    }

    public DumpFileExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The runtime could not write the dump (disk full, the target's account cannot write the folder, and so on).</summary>
public sealed class DumpWriteFailedException : Exception
{
    public DumpWriteFailedException()
    {
    }

    public DumpWriteFailedException(string message)
        : base(message)
    {
    }

    public DumpWriteFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
