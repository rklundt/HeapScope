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

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The base of the worker's contract handlers, an RPC boundary (ARCHITECTURE.md 11): a method body runs inside
/// <see cref="WorkerErrors.GuardAsync"/>, with the RPC method's own name as the operation in the log.
/// </summary>
internal abstract class RpcHandler(ILogger logger)
{
    /// <summary>The handler's log, under the handler's own category.</summary>
    protected ILogger Logger { get; } = logger;

    // Every answer is logged with its request (sprint 2.3.7 review H3); page reads and probes, which a grid makes many of,
    // at Debug.
    protected Task<T> GuardAsync<T>(Func<Task<T>> body, LogLevel resultLevel = LogLevel.Information, [CallerMemberName] string operation = "") =>
        WorkerErrors.GuardAsync(body, Logger, operation, null, resultLevel);

    protected Task<T> GuardAsync<T>(object? request, Func<Task<T>> body, LogLevel resultLevel = LogLevel.Information, [CallerMemberName] string operation = "") =>
        WorkerErrors.GuardAsync(body, Logger, operation, request, resultLevel);
}
