using System.CommandLine;
using System.CommandLine.Invocation;
using HeapScope.Contracts;

namespace HeapScope.Cli;

/// <summary>Builds the command tree. Command handlers resolve their services from the host.</summary>
internal static class CliCommands
{
    public static RootCommand Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var root = new RootCommand($"{ProductInfo.Name} {ProductInfo.Version}: .NET memory profiler command line");

        // The built-in --version prints the raw informational version (with the source revision);
        // the product version is ProductInfo.Version everywhere (ARCHITECTURE.md 15).
        foreach (var option in root.Options)
        {
            if (option is VersionOption)
            {
                option.Action = new ProductVersionAction();
            }
        }

        return root;
    }

    private sealed class ProductVersionAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(ProductInfo.Version);
            return 0;
        }
    }
}
