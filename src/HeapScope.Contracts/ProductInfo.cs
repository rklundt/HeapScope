using System.Reflection;

namespace HeapScope.Contracts;

/// <summary>Product name and version shared by every HeapScope executable.</summary>
public static class ProductInfo
{
    public const string Name = "HeapScope";

    /// <summary>The informational version of the HeapScope build, without source revision metadata.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(ProductInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
