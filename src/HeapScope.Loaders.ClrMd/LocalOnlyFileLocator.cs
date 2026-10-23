using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// Replaces ClrMD's default file locator, which can reach symbol servers, with one that only accepts files that
/// already exist on this machine, on a local fixed drive, at the recorded path and match the requested image (ADR-017).
/// Paths recorded in the dump never lead to a network share.
/// </summary>
internal sealed class LocalOnlyFileLocator : IFileLocator
{
    public static LocalOnlyFileLocator Instance { get; } = new();

    public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties) =>
        DacLocator.IsLocalFixedPath(fileName) && File.Exists(fileName) && DacLocator.Matches(fileName, buildTimeStamp, imageSize) ? fileName : null;

    public string? FindPEImage(string fileName, SymbolProperties archivedUnder, ImmutableArray<byte> buildIdOrUUID, OSPlatform originalPlatform, bool checkProperties) => null;
}
