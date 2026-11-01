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
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Result pages and their release (<see cref="IResultSetService"/>).</summary>
internal sealed class ResultSetHandler(ResultSetStore resultSets, ILogger<ResultSetHandler> logger) : RpcHandler(logger), IResultSetService
{
    public Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken)
    {
        if (offset < 0 || count < 0)
        {
            throw WorkerErrors.Create(HeapScopeErrorCode.InvalidArgument, "Offset and count must not be negative.");
        }

        // Pages of instances read string values from the dump (sprint 1.5), so paging gets the error contract (codes,
        // the worker log). It stays on the request's thread: a further thread pool hop cost latency under load.
        return GuardAsync(() => Task.FromResult(
            resultSets.TryGetPage(resultSetId, offset, count)
                ?? throw WorkerErrors.Create(
                    HeapScopeErrorCode.ResultSetNotFound,
                    $"Result set {resultSetId} is not available: it was released, evicted or never existed. Run the query again.")),
            LogLevel.Debug);
    }

    public Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken)
    {
        resultSets.Release(resultSetId);
        return Task.CompletedTask;
    }
}
