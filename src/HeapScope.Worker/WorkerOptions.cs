using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace HeapScope.Worker;

/// <summary>Validated command line options of the worker process.</summary>
public sealed record WorkerOptions(string Pipe, int? ParentPid)
{
    public const string SectionName = "Worker";

    /// <summary>Creates the map from command line switches to configuration keys.</summary>
    public static Dictionary<string, string> CreateSwitchMappings() => new()
    {
        ["--pipe"] = $"{SectionName}:Pipe",
        ["--parent-pid"] = $"{SectionName}:ParentPid",
    };

    /// <summary>
    /// Reads and validates the options. Returns false with a user-facing error for missing or malformed values,
    /// so the worker can exit with a usage error instead of an unhandled exception.
    /// </summary>
    public static bool TryRead(IConfiguration configuration, out WorkerOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options = null;
        var section = configuration.GetSection(SectionName);

        var pipe = section["Pipe"];
        if (string.IsNullOrWhiteSpace(pipe))
        {
            error = "Missing required option --pipe <name>.";
            return false;
        }

        int? parentPid = null;
        var rawParentPid = section["ParentPid"];
        if (rawParentPid is not null)
        {
            if (!int.TryParse(rawParentPid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                error = $"Invalid --parent-pid '{rawParentPid}': expected a process id.";
                return false;
            }

            parentPid = pid;
        }

        options = new WorkerOptions(pipe, parentPid);
        error = null;
        return true;
    }
}
