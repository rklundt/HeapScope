using System.IO.Pipes;
using HeapScope.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Serves <see cref="IWorkerService"/> on the named pipe (ARCHITECTURE.md 4.2). The pipe accepts one client from the
/// current user only. The worker stops when the client does not connect in time or disconnects.
/// </summary>
internal sealed class WorkerRpcServer(
    WorkerOptions options,
    WorkerService service,
    IHostApplicationLifetime lifetime,
    ILogger<WorkerRpcServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipe = new NamedPipeServerStream(
            options.Pipe,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (pipe.ConfigureAwait(false))
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                connectTimeout.CancelAfter(options.ConnectTimeout);
                try
                {
                    await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("No client connected to pipe {Pipe} within {Timeout}; stopping", options.Pipe, options.ConnectTimeout);
                    lifetime.StopApplication();
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            logger.LogInformation("Client connected on pipe {Pipe}", options.Pipe);
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new JsonMessageFormatter()));
            rpc.AddLocalRpcTarget<IWorkerService>(service, null);
            rpc.StartListening();
            try
            {
                await rpc.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
                logger.LogInformation("Client disconnected; stopping");
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Worker stopping; closing the pipe");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "JSON-RPC connection failed; stopping");
            }

            lifetime.StopApplication();
        }
    }
}
