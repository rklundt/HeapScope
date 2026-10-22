namespace HeapScope.Core;

/// <summary>Descriptive metadata of a snapshot. Never contains snapshot contents.</summary>
/// <param name="Kind">The kind of file the snapshot came from.</param>
/// <param name="SourcePath">Full path of the snapshot file, or a descriptive name for synthetic snapshots.</param>
/// <param name="ProcessName">Name of the captured process, when known.</param>
/// <param name="ProcessId">Id of the captured process, when known.</param>
/// <param name="RuntimeVersion">Version of the .NET runtime in the captured process, when known.</param>
/// <param name="PointerSize">Pointer size of the captured process in bytes.</param>
/// <param name="CapturedAtUtc">When the snapshot was captured, when known.</param>
public sealed record SnapshotInfo(
    SnapshotKind Kind,
    string SourcePath,
    string? ProcessName = null,
    int? ProcessId = null,
    string? RuntimeVersion = null,
    int PointerSize = 8,
    DateTimeOffset? CapturedAtUtc = null);
