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

using System.Globalization;
using System.IO;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The fake worker's ResultSets (<see cref="IResultSetService"/>).</summary>
internal sealed partial class FakeWorkerService : IResultSetService
{
    public async Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken)
    {
        PageRequests.Add((offset, count));
        if (PageGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (PageFailure is { } failure)
        {
            throw failure;
        }

        if (!_sets.TryGetValue(resultSetId, out var rows))
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.ResultSetNotFound, "The result set is gone. Run the query again.", null);
        }

        return new ResultPage(resultSetId, offset, rows.Count, [.. rows.Skip(offset).Take(count).Select(WithPercent)]);
    }

    public Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken)
    {
        Released.Add(resultSetId);
        _sets.Remove(resultSetId);
        return Task.CompletedTask;
    }
}
