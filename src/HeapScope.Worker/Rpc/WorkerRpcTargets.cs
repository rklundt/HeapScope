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

using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The worker's RPC targets: one handler per contract of <see cref="WorkerContracts.All"/>, and the shared services they
/// use, all singletons. Dependencies point one way: handlers use the shared services; the set resolver uses the matcher,
/// the inspection runner and the derived queries; those use the snapshot store and the set cache only.
/// </summary>
internal static class WorkerRpcTargets
{
    /// <summary>Every worker contract with its handler, in the order of <see cref="WorkerContracts.All"/>.</summary>
    public static IReadOnlyList<RpcTarget> All { get; } =
    [
        RpcTarget.For<IWorkerControl, ControlHandler>(),
        RpcTarget.For<IWorkerTestHooks, TestHooksHandler>(),
        RpcTarget.For<ISnapshotService, SnapshotHandler>(),
        RpcTarget.For<IResultSetService, ResultSetHandler>(),
        RpcTarget.For<IHeapQueryService, HeapQueryHandler>(),
        RpcTarget.For<IRetentionService, RetentionHandler>(),
        RpcTarget.For<IInspectionService, InspectionHandler>(),
        RpcTarget.For<IAllocationTraceService, AllocationTraceHandler>(),
    ];

    /// <summary>
    /// Registers the shared services and every handler. The caller registers what they rest on: the options, the result
    /// set store, the snapshot and trace stores, the application lifetime and logging.
    /// </summary>
    public static IServiceCollection AddWorkerRpcTargets(this IServiceCollection services)
    {
        // One set cache for every handler: its budgets hold over all snapshots, and closing a snapshot forgets its sets.
        services.AddSingleton(sp => CreateSetCache(sp.GetRequiredService<ILogger<ObjectSetCache>>()));
        services.AddSingleton<SnapshotAccess>();
        services.AddSingleton<DerivedQueries>();
        services.AddSingleton<IdentityMatcher>();
        services.AddSingleton<InspectionRunner>();
        services.AddSingleton<ObjectSetResolver>();
        foreach (var target in All)
        {
            services.AddSingleton(target.Handler);
        }

        return services;
    }

    /// <summary>
    /// Resolves every handler now, with the stores they rest on, and returns what adds each to a connection as the target of
    /// its contract (before the connection starts listening).
    /// </summary>
    public static Action<JsonRpc> Resolve(IServiceProvider services)
    {
        var adds = All.Select(t => t.Resolve(services)).ToArray();
        return rpc =>
        {
            ArgumentNullException.ThrowIfNull(rpc);
            foreach (var add in adds)
            {
                add(rpc);
            }
        };
    }

    private static ObjectSetCache CreateSetCache(ILogger logger)
    {
        var cache = new ObjectSetCache();
        cache.Evicted += what => logger.LogDebug("Object set cache: dropped {What} to stay within its budget", what);
        return cache;
    }
}
