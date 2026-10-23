namespace HeapScope.Loaders.ClrMd;

/// <summary>Options of the full dump loader (ARCHITECTURE.md 6.5, ADR-017).</summary>
public sealed record ClrMdLoaderOptions
{
    /// <summary>Default cap of ClrMD's dump data cache, measured in sprint 0.4 (same speed, half the memory).</summary>
    public const long DefaultMaxDumpCacheBytes = 512L * 1024 * 1024;

    /// <summary>Upper bound of ClrMD's dump data cache in bytes.</summary>
    public long MaxDumpCacheBytes { get; init; } = DefaultMaxDumpCacheBytes;

    /// <summary>A user folder searched for the data access component (DAC), configuration <c>Symbols:DacFolder</c>.</summary>
    public string? DacFolder { get; init; }

    /// <summary>
    /// Opt-in (configuration <c>Symbols:AllowMicrosoftSymbolServer</c>, default off): when no local DAC matches, let
    /// ClrMD download it from Microsoft's symbol server. Sends module identifiers, never dump contents.
    /// </summary>
    public bool AllowMicrosoftSymbolServer { get; init; }

    /// <summary>
    /// Folders holding installed shared runtimes (<c>...\shared\Microsoft.NETCore.App</c>). Null means the machine
    /// defaults (<c>DOTNET_ROOT</c> and Program Files); tests pass their own.
    /// </summary>
    public IReadOnlyList<string>? InstalledRuntimeRoots { get; init; }

    /// <summary>
    /// Whether the dumped runtime's own folder (a path recorded in the dump) is searched first. Always true in the
    /// product; tests turn it off to simulate a dump from another machine.
    /// </summary>
    public bool SearchDumpedRuntimeDirectory { get; init; } = true;
}
