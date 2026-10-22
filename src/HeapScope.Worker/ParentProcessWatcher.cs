using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker;

/// <summary>Stops the worker when its parent process exits, so a crashed UI never leaves an orphaned worker.</summary>
internal sealed class ParentProcessWatcher(
    WorkerOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<ParentProcessWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.ParentPid is not int parentPid)
        {
            logger.LogInformation("No parent process given; the worker will not watch a parent");
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("Parent process {ParentPid} is not running; stopping", parentPid);
            lifetime.StopApplication();
            return;
        }

        using (parent)
        {
            try
            {
                await parent.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        logger.LogWarning("Parent process {ParentPid} exited; stopping", parentPid);
        lifetime.StopApplication();
    }
}
