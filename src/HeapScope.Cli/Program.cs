using HeapScope.Cli;
using Microsoft.Extensions.Hosting;

IHost? host = null;
var root = CliCommands.Build(() => host!.Services);
var parseResult = root.Parse(args);

// The worker attach pipe must be known before the host is built.
host = CliHost.Build(parseResult.GetValue(CliCommands.PipeOption));
using (host)
{
    await host.StartAsync().ConfigureAwait(false);
    try
    {
        return await parseResult.InvokeAsync().ConfigureAwait(false);
    }
    finally
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StopAsync(stopTimeout.Token).ConfigureAwait(false);
    }
}
