namespace HeapScope.Worker.Snapshots;

/// <summary>A reopen that required the index cache found it missing or invalid (error code SnapshotChanged).</summary>
internal sealed class SnapshotChangedException : Exception
{
    public SnapshotChangedException()
    {
    }

    public SnapshotChangedException(string message)
        : base(message)
    {
    }

    public SnapshotChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
