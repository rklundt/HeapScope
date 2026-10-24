namespace HeapScope.Contracts;

/// <summary>Protocol constants shared by the worker and its clients.</summary>
public static class WorkerProtocol
{
    /// <summary>Bumped on any incompatible change to <see cref="IWorkerService"/> or its DTOs.</summary>
    /// Version 2 (sprint 1.2): snapshots (probe, open, close, type summary) and the snapshot error codes.
    /// Version 3 (sprint 1.3): type summary queries (grouping, filter, sort, heap kinds), new column kinds and the key flag.
    /// Version 4 (sprint 1.4): type diffs.
    /// Version 5 (sprint 1.5): instances, object details and references; hidden key columns; sort by column name.
    /// Version 6 (sprint 1.6): inspections (list and run), instance scopes and SameValueAs; progress on object and
    /// references calls. ARCHITECTURE.md 4.4 has the details.
    public const int Version = 6;

    /// <summary>Maximum rows returned by one <see cref="IWorkerService.GetRowsAsync"/> call.</summary>
    public const int MaxPageSize = 10_000;

    /// <summary>Prefix of worker pipe names; the rest is a GUID.</summary>
    public const string PipeNamePrefix = "HeapScope.Worker.";
}
