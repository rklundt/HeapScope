namespace HeapScope.Contracts;

/// <summary>
/// Error codes carried in the JSON-RPC error code of worker errors (ARCHITECTURE.md 11). They are outside the
/// range JSON-RPC and StreamJsonRpc reserve (-32768 to -32000), and never change.
/// </summary>
public enum HeapScopeErrorCode
{
    Unknown = 1000,
    ProtocolMismatch = 1001,
    ResultSetNotFound = 1002,
    InvalidArgument = 1003,
    CapabilityNotSupported = 1004,
    TestHooksDisabled = 1005,

    /// <summary>The snapshot id is not loaded in this worker (for example after a worker restart).</summary>
    SnapshotNotLoaded = 1006,

    /// <summary>The operation would not fit in the worker's memory budget, or ran out of memory.</summary>
    InsufficientMemory = 1007,

    /// <summary>The snapshot file or index cache is not in a readable format.</summary>
    SnapshotFormat = 1008,

    /// <summary>The data access component matching the dumped runtime is not on this machine (ADR-017).</summary>
    RuntimeComponentMissing = 1009,

    /// <summary>The snapshot file could not be read: in use (still being written), access denied or gone.</summary>
    SnapshotUnavailable = 1010,

    /// <summary>A reopen that requires the index cache found it missing or invalid: the file changed since it was opened.</summary>
    SnapshotChanged = 1011,
}
