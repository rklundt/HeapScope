using HeapScope.Cli;
using Microsoft.Extensions.Hosting;

IHost? host = null;
var root = CliCommands.Build(() => host!.Services);
var parseResult = root.Parse(args);
if (parseResult.Errors.Count > 0)
{
    // Prints the errors and usage; usage errors exit 64 (EX_USAGE) so 1 and 2 keep their meanings for CI.
    await parseResult.InvokeAsync(CliCommands.CreateInvocationConfiguration()).ConfigureAwait(false);
    return CliCommands.Usage;
}

// The worker attach pipe must be known before the host is built.
host = CliHost.Build(
    parseResult.GetValue(CliCommands.PipeOption),
    parseResult.GetValue(CliCommands.DacFolderOption),
    parseResult.GetValue(CliCommands.AllowSymbolServerOption));
using (host)
{
    await host.StartAsync().ConfigureAwait(false);
    try
    {
        return await parseResult.InvokeAsync(CliCommands.CreateInvocationConfiguration()).ConfigureAwait(false);
    }
    finally
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StopAsync(stopTimeout.Token).ConfigureAwait(false);
    }
}
