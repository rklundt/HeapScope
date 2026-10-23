namespace HeapScope.Storage;

/// <summary>
/// The index cache could not be replaced because it is open (a mapped graph keeps its file open until disposed).
/// The previous cache is intact and no temporary file is left behind.
/// </summary>
public sealed class IndexCacheInUseException : IOException
{
    public IndexCacheInUseException()
    {
    }

    public IndexCacheInUseException(string message)
        : base(message)
    {
    }

    public IndexCacheInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
