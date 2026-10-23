namespace HeapScope.Contracts;

/// <summary>Protocol constants shared by the worker and its clients.</summary>
public static class WorkerProtocol
{
    /// <summary>Bumped on any incompatible change to <see cref="IWorkerService"/> or its DTOs.</summary>
    /// Version 2 (sprint 1.2): snapshots (probe, open, close, type summary) and the snapshot error codes.
    /// Version 3 (sprint 1.3): type summary queries (grouping, filter, sort, heap kinds), new column kinds and the key flag.
    public const int Version = 3;

    /// <summary>Maximum rows returned by one <see cref="IWorkerService.GetRowsAsync"/> call.</summary>
    public const int MaxPageSize = 10_000;

    /// <summary>Prefix of worker pipe names; the rest is a GUID.</summary>
    public const string PipeNamePrefix = "HeapScope.Worker.";
}
