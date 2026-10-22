using System.Globalization;

namespace HeapScope.Benchmarks;

/// <summary>Minimal "--name value" argument reader for benchmarks.</summary>
internal sealed class BenchmarkArgs(string[] args)
{
    public string? Get(string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--" + name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public int GetInt(string name, int fallback) =>
        Get(name) is { } value ? int.Parse(value.Replace("_", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture) : fallback;

    public double GetDouble(string name, double fallback) =>
        Get(name) is { } value ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;
}
