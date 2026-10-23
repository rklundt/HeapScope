namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// The data access component (DAC) matching the dumped runtime was not found locally and downloads are off
/// (ADR-017). The message names the runtime version and how to provide the file.
/// </summary>
public sealed class RuntimeComponentMissingException : Exception
{
    public RuntimeComponentMissingException()
    {
    }

    public RuntimeComponentMissingException(string message)
        : base(message)
    {
    }

    public RuntimeComponentMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RuntimeComponentMissingException(string runtimeVersion, string fileName, IReadOnlyList<string> searched)
        : base(BuildMessage(runtimeVersion, fileName, searched))
    {
        RuntimeVersion = runtimeVersion;
        FileName = fileName;
        Searched = searched;
    }

    public string? RuntimeVersion { get; }

    public string? FileName { get; }

    public IReadOnlyList<string> Searched { get; } = [];

    private static string BuildMessage(string runtimeVersion, string fileName, IReadOnlyList<string> searched) =>
        $"This dump was taken from .NET {runtimeVersion}, and the matching {fileName} was not found on this machine. " +
        $"Install the .NET {runtimeVersion} runtime, or copy {fileName} from that runtime's folder next to the dump or into a folder given with --dac-folder (CLI) or the Symbols:DacFolder setting. " +
        "Downloading it from Microsoft's symbol server is off by default; it can be turned on with --allow-symbol-server (CLI) or the Symbols:AllowMicrosoftSymbolServer setting. " +
        $"Searched: {(searched.Count == 0 ? "nothing" : string.Join("; ", searched))}.";
}
