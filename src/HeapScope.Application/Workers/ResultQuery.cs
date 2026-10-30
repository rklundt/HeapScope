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

using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Workers;

/// <summary>
/// A worker query whose rows come back as a result set (peer review P5): the answer holds the first result, and its
/// <see cref="ResultSetHandle"/> runs the query again (reopening what it needs) when the worker no longer has the set.
/// </summary>
internal static class ResultQuery
{
    public static async Task<TAnswer> RunAsync<TResult, TAnswer>(
        IWorkerClient worker,
        ILogger logger,
        Func<CancellationToken, Task<TResult>> query,
        Func<TResult, ResultSetInfo> rowsOf,
        Func<ResultSetHandle, TResult, TAnswer> answer,
        CancellationToken cancellationToken)
        where TResult : class
    {
        TResult? latest = null;
        async Task<ResultSetInfo> RunAsync(CancellationToken ct)
        {
            latest = await query(ct).ConfigureAwait(false);
            return rowsOf(latest);
        }

        var info = await RunAsync(cancellationToken).ConfigureAwait(false);

        // RunAsync just completed, so it set latest; the compiler cannot see through the local function.
        return answer(new ResultSetHandle(worker, info, RunAsync, logger), latest!);
    }
}
