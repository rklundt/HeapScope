namespace HeapScope.Contracts;

/// <summary>
/// Wire copy of Core's snapshot capabilities (ADR-016). Numeric values must equal Core's
/// <c>SnapshotCapabilities</c>; a test enforces it.
/// </summary>
[Flags]
public enum SnapshotFeatures : uint
{
    None = 0,
    ObjectGraph = 1 << 0,
    CompleteGraph = 1 << 1,
    ExactCounts = 1 << 2,
    ObjectAddresses = 1 << 3,
    Generations = 1 << 4,
    FieldValues = 1 << 5,
    StringContents = 1 << 6,
    RootDetails = 1 << 7,
    ThreadStacks = 1 << 8,
}

/// <summary>Wire copy of Core's snapshot kind (ADR-016).</summary>
public enum SnapshotKindDto
{
    FullDump = 0,
    GcDump = 1,
}

/// <summary>User-facing explanations of missing features, shown by UI and CLI (ARCHITECTURE.md 5.2).</summary>
public static class CapabilityDescriptions
{
    /// <summary>
    /// Why a feature is unavailable in a snapshot of <paramref name="kind"/>. A full dump holds everything; features it
    /// lacks are not built yet (field values, string contents and thread stacks read the dump again, sprint 1.5) or
    /// could not be read from a damaged dump.
    /// </summary>
    public static string ExplainMissing(SnapshotFeatures missing, SnapshotKindDto kind)
    {
        if (kind != SnapshotKindDto.FullDump)
        {
            return ExplainMissing(missing);
        }

        return missing switch
        {
            SnapshotFeatures.FieldValues => "Reading field values from a full dump is not available in this version of HeapScope yet.",
            SnapshotFeatures.StringContents => "Reading string contents from a full dump is not available in this version of HeapScope yet.",
            SnapshotFeatures.ThreadStacks => "Thread stacks of a full dump are not available in this version of HeapScope yet.",
            SnapshotFeatures.Generations => "Generations could not be read from this dump.",
            SnapshotFeatures.CompleteGraph or SnapshotFeatures.ExactCounts =>
                "Parts of the managed heap could not be read from this dump, so some objects may be missing and counts are not exact.",
            _ => ExplainMissing(missing),
        };
    }

    /// <summary>Why a feature is unavailable when <paramref name="missing"/> is not supported by a gcdump.</summary>
    public static string ExplainMissing(SnapshotFeatures missing) => missing switch
    {
        SnapshotFeatures.FieldValues => "Field values are not stored in a gcdump. Capture a full dump to inspect fields.",
        SnapshotFeatures.StringContents => "String contents are not stored in a gcdump. Capture a full dump to see string text.",
        SnapshotFeatures.Generations => "This snapshot does not record generations. Capture a full dump to see Gen0, Gen1, Gen2, LOH and POH.",
        SnapshotFeatures.ThreadStacks => "Thread stacks are only available in full dumps.",
        SnapshotFeatures.CompleteGraph => "This gcdump was sampled, so not every object is present. Counts may be estimates.",
        SnapshotFeatures.ExactCounts => "Counts and sizes in this snapshot are scaled estimates, not exact values.",
        SnapshotFeatures.ObjectAddresses => "This snapshot does not record real object addresses.",
        SnapshotFeatures.RootDetails => "This snapshot records root kinds but not static field names or thread ids.",
        SnapshotFeatures.ObjectGraph => "This snapshot has no object graph.",
        _ => $"This snapshot does not support: {missing}.",
    };
}
