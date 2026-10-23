namespace HeapScope.Core;

/// <summary>A snapshot file cannot be read: not a dump, damaged, or taken in a state that cannot be analyzed.</summary>
public sealed class SnapshotFormatException : Exception
{
    public SnapshotFormatException()
    {
    }

    public SnapshotFormatException(string message)
        : base(message)
    {
    }

    public SnapshotFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
