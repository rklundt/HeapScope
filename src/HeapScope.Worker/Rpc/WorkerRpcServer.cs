// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

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
