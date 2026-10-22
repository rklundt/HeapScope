namespace HeapScope.Core;

/// <summary>
/// What a snapshot can answer (ARCHITECTURE.md 5.2). Values are persisted in the index cache and mirrored on the
/// wire by Contracts (ADR-016), so they never change.
/// </summary>
[Flags]
public enum SnapshotCapabilities : uint
{
    None = 0,

    /// <summary>Objects, types, references and roots.</summary>
    ObjectGraph = 1 << 0,

    /// <summary>Every object is present (the snapshot is not sampled).</summary>
    CompleteGraph = 1 << 1,

    /// <summary>Counts and sizes are exact, not scaled estimates.</summary>
    ExactCounts = 1 << 2,

    /// <summary>Real object addresses.</summary>
    ObjectAddresses = 1 << 3,

    /// <summary>Gen0/1/2, LOH, POH and frozen segment per object.</summary>
    Generations = 1 << 4,

    /// <summary>Primitive and nested struct field values can be read.</summary>
    FieldValues = 1 << 5,

    /// <summary>String text can be read.</summary>
    StringContents = 1 << 6,

    /// <summary>Static field names and thread ids for roots.</summary>
    RootDetails = 1 << 7,

    /// <summary>Managed thread stacks.</summary>
    ThreadStacks = 1 << 8,

    /// <summary>Everything a full process dump provides.</summary>
    FullDump = ObjectGraph | CompleteGraph | ExactCounts | ObjectAddresses | Generations | FieldValues
        | StringContents | RootDetails | ThreadStacks,
}

/// <summary>Capability checks used by analysis entry points.</summary>
public static class SnapshotCapabilitiesExtensions
{
    /// <summary>Returns true when every flag in <paramref name="required"/> is present.</summary>
    public static bool HasAll(this SnapshotCapabilities capabilities, SnapshotCapabilities required) =>
        (capabilities & required) == required;

    /// <summary>The flags of <paramref name="required"/> that are missing.</summary>
    public static SnapshotCapabilities Missing(this SnapshotCapabilities capabilities, SnapshotCapabilities required) =>
        required & ~capabilities;

    /// <summary>Throws <see cref="CapabilityNotSupportedException"/> when any required flag is missing.</summary>
    public static void Require(this SnapshotCapabilities capabilities, SnapshotCapabilities required)
    {
        var missing = capabilities.Missing(required);
        if (missing != SnapshotCapabilities.None)
        {
            throw new CapabilityNotSupportedException(missing);
        }
    }
}
