using HeapScope.Cli;

using var host = CliHost.Build(args);
await host.StartAsync().ConfigureAwait(false);
try
{
    var root = CliCommands.Build(host.Services);
    return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
}
finally
{
    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await host.StopAsync(stopTimeout.Token).ConfigureAwait(false);
}
