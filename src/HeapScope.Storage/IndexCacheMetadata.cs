using HeapScope.Core;

namespace HeapScope.Storage;

/// <summary>Descriptive data stored in the cache's metadata section (UTF-8 JSON).</summary>
/// <param name="Info">The snapshot's metadata.</param>
/// <param name="Capabilities">What the snapshot can answer (service-backed capabilities need the original file).</param>
/// <param name="WriterVersion">Version of the HeapScope build that wrote the cache.</param>
public sealed record IndexCacheMetadata(SnapshotInfo Info, SnapshotCapabilities Capabilities, string WriterVersion)
{
    /// <summary>Informational version of this Storage assembly, without source revision.</summary>
    public static string CurrentWriterVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(IndexCacheMetadata).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

/// <summary>The JSON document of the metadata section.</summary>
internal sealed record MetadataDocument(
    IndexCacheMetadata Metadata,
    long DroppedReferenceCount,
    long DroppedRootCount);
