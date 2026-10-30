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
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using StreamJsonRpc;

namespace HeapScope.Application.Tests;

/// <summary>
/// The DAC details of a <see cref="HeapScopeErrorCode.DacNotTrusted"/> refusal cross the same JSON-RPC formatter the
/// worker and the client use (sprint 3.6 review): if they did not, the app would stop asking and only show the message.
/// </summary>
public sealed class DacTrustRpcTests
{
    [Fact]
    public async Task A_refusal_reaches_the_client_with_its_DAC_details()
    {
        // Short: off Windows the pipe is a socket under the temp folder, and macOS limits its path to 104 bytes.
        var pipe = $"hs-{Guid.NewGuid():N}"[..15];
        await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(TestContext.Current.CancellationToken), client.ConnectAsync(TestContext.Current.CancellationToken));
        var details = new DacTrustDetails("C:/dumps/mscordaccore.dll", new string('c', 64), "10.0.13");
        using var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(server, server, new JsonMessageFormatter()), new Refusing(details));
        using var clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(client, client, new JsonMessageFormatter()));
        serverRpc.StartListening();
        clientRpc.StartListening();

        var refused = await Should.ThrowAsync<RemoteInvocationException>(() => clientRpc.InvokeAsync("Open"));

        refused.ErrorCode.ShouldBe((int)HeapScopeErrorCode.DacNotTrusted);
        WorkerHost.DacTrustOf(refused.ErrorData).ShouldBe(details);
    }

    private sealed class Refusing(DacTrustDetails details)
    {
        public void Open() => throw new LocalRpcException("Not trusted.") { ErrorCode = (int)HeapScopeErrorCode.DacNotTrusted, ErrorData = details };
    }
}
