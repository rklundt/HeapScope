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

using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;

namespace HeapScope.Worker.Rpc;

/// <summary>A worker contract and the handler that serves it on the JSON-RPC connection.</summary>
internal sealed class RpcTarget
{
    private readonly Action<JsonRpc, object> _add;

    private RpcTarget(Type contract, Type handler, Action<JsonRpc, object> add)
    {
        Contract = contract;
        Handler = handler;
        _add = add;
    }

    /// <summary>The contract interface, one of <see cref="HeapScope.Contracts.WorkerContracts.All"/>.</summary>
    public Type Contract { get; }

    /// <summary>The handler type, a singleton in the worker's services.</summary>
    public Type Handler { get; }

    /// <summary>Pairs <typeparamref name="TContract"/> with <typeparamref name="THandler"/>, which the compiler checks implements it.</summary>
    public static RpcTarget For<TContract, THandler>()
        where TContract : class
        where THandler : class, TContract =>
        new(typeof(TContract), typeof(THandler), (rpc, handler) => rpc.AddLocalRpcTarget<TContract>((TContract)handler, null));

    /// <summary>
    /// Resolves the handler from <paramref name="services"/> now, and returns what exposes the contract's methods, and only
    /// those, on a connection served by it.
    /// </summary>
    public Action<JsonRpc> Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var handler = services.GetRequiredService(Handler);
        return rpc => _add(rpc, handler);
    }
}
