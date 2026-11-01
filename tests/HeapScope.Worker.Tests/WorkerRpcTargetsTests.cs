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
using HeapScope.Storage;
using HeapScope.Tests.Shared;
using HeapScope.Worker.Rpc;
using HeapScope.Worker.Snapshots;
using HeapScope.Worker.Traces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamJsonRpc;

namespace HeapScope.Worker.Tests;

/// <summary>
/// The worker serves every contract of <see cref="WorkerContracts.All"/> through one handler each: a contract left out of
/// the registration would answer every call with "method not found", which only a real connection shows.
/// </summary>
public sealed class WorkerRpcTargetsTests
{
    [Fact]
    public void Every_contract_has_exactly_one_handler_that_implements_it_and_no_other_contract()
    {
        WorkerRpcTargets.All.Count.ShouldBe(WorkerContracts.All.Count);
        foreach (var contract in WorkerContracts.All)
        {
            var targets = WorkerRpcTargets.All.Where(t => t.Contract == contract).ToList();
            targets.Count.ShouldBe(1, contract.Name);
            contract.IsAssignableFrom(targets[0].Handler).ShouldBeTrue(targets[0].Handler.Name);
            WorkerContracts.All.Count(c => c.IsAssignableFrom(targets[0].Handler)).ShouldBe(1, targets[0].Handler.Name);
        }

        WorkerRpcTargets.All.Select(t => t.Handler).Distinct().Count().ShouldBe(WorkerRpcTargets.All.Count);
    }

    [Fact]
    public async Task Every_contract_method_reaches_its_handler_over_a_real_connection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var services = BuildServices();

        // Short: off Windows the pipe is a socket under the temp folder, and macOS limits its path to 104 bytes.
        var pipe = $"hs-{Guid.NewGuid():N}"[..15];
        await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(cancellationToken), client.ConnectAsync(cancellationToken));
        using var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(server, server, new JsonMessageFormatter()))
        {
            ActivityTracingStrategy = new ActivityTracingStrategy(),
        };
        WorkerRpcTargets.Resolve(services)(serverRpc);
        serverRpc.StartListening();
        using var clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(client, client, new JsonMessageFormatter()));
        clientRpc.StartListening();

        // Empty arguments: a handler refuses them (no such snapshot, a null request), which proves the call reached it.
        var notFound = new List<string>();
        foreach (var method in WorkerContracts.All.SelectMany(c => c.GetMethods()))
        {
            var arguments = method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                .ToArray();
            try
            {
                await clientRpc.InvokeWithCancellationAsync(method.Name, arguments, cancellationToken);
            }
            catch (RemoteMethodNotFoundException)
            {
                notFound.Add($"{method.DeclaringType!.Name}.{method.Name}");
            }
            catch (RemoteInvocationException)
            {
                // Answered by the handler with a refusal.
            }
        }

        notFound.ShouldBeEmpty();

        // The test hooks still refuse when they are off, and the handshake still names this worker.
        var crash = await Should.ThrowAsync<RemoteInvocationException>(
            () => clientRpc.InvokeWithCancellationAsync(nameof(IWorkerTestHooks.CrashForTestingAsync), Array.Empty<object?>(), cancellationToken));
        crash.ErrorCode.ShouldBe((int)HeapScopeErrorCode.TestHooksDisabled);
        var hang = await Should.ThrowAsync<RemoteInvocationException>(
            () => clientRpc.InvokeWithCancellationAsync(nameof(IWorkerTestHooks.HangForTestingAsync), new object?[] { null }, cancellationToken));
        hang.ErrorCode.ShouldBe((int)HeapScopeErrorCode.TestHooksDisabled);

        var info = await clientRpc.InvokeWithCancellationAsync<WorkerInfo>(nameof(IWorkerControl.GetInfoAsync), [WorkerProtocol.Version], cancellationToken);
        info.InstanceId.ShouldBe(services.GetRequiredService<ControlHandler>().InstanceId);
        info.TestHooksEnabled.ShouldBeFalse();
    }

    /// <summary>The worker's services as the worker registers them, on a data folder of the tests, with test hooks off.</summary>
    private static ServiceProvider BuildServices()
    {
        var options = new WorkerOptions("unused", null) { DataFolder = TestDataFolder.Path };
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(options)
            .AddSingleton(new ResultSetStore())
            .AddSingleton(new IndexCacheLocator(options.Folders.Cache))
            .AddSingleton(IndexCacheSigner.None)
            .AddSingleton<SnapshotStore>()
            .AddSingleton<TraceStore>()
            .AddSingleton<IHostApplicationLifetime, StoppedNowhere>()
            .AddWorkerRpcTargets()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>A lifetime whose stop does nothing: the shutdown call must not stop the test host.</summary>
    private sealed class StoppedNowhere : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
