namespace HeapScope.Core;

/// <summary>Thrown when an operation needs a capability the snapshot does not have (ADR-003).</summary>
public sealed class CapabilityNotSupportedException : Exception
{
    public CapabilityNotSupportedException()
        : this(SnapshotCapabilities.None)
    {
    }

    public CapabilityNotSupportedException(string message)
        : base(message)
    {
    }

    public CapabilityNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CapabilityNotSupportedException(SnapshotCapabilities missing)
        : base($"This snapshot does not support: {missing}.")
    {
        Missing = missing;
    }

    /// <summary>The capabilities that were required but missing.</summary>
    public SnapshotCapabilities Missing { get; }
}
