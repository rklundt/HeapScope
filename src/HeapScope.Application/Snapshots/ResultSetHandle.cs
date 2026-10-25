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

using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.Application.Snapshots;

/// <summary>
/// A worker result set together with the query that produced it (from the 0.3 review): when paging fails with
/// <see cref="HeapScopeErrorCode.ResultSetNotFound"/> (evicted, or the worker restarted) the query runs again once and
/// paging continues on the new set. Front ends page through this instead of raw result set ids. After
/// <see cref="ReleaseAsync"/> the handle is finished: paging throws <see cref="ObjectDisposedException"/> and never
/// re-runs the query, so a page request racing a release cannot leave an orphaned set in the worker.
/// </summary>
public sealed class ResultSetHandle
{
    private readonly IWorkerClient _worker;
    private readonly Func<CancellationToken, Task<ResultSetInfo>> _query;

    // Serializes re-queries and the release. Never disposed: it is not used through AvailableWaitHandle, so it holds
    // no kernel object.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _released;

    internal ResultSetHandle(IWorkerClient worker, ResultSetInfo info, Func<CancellationToken, Task<ResultSetInfo>> query)
    {
        _worker = worker;
        Info = info;
        _query = query;
    }

    /// <summary>The current result set (replaced when the query had to run again).</summary>
    public ResultSetInfo Info { get; private set; }

    /// <summary>How often the query ran again because the set was gone (tests and diagnostics).</summary>
    public int Requeries { get; private set; }

    /// <summary>Whether <see cref="ReleaseAsync"/> was called.</summary>
    public bool IsReleased => _released;

    public async Task<ResultPage> GetRowsAsync(int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_released, this);
        var id = Info.Id;
        try
        {
            return await _worker.InvokeAsync((service, ct) => service.GetRowsAsync(id, offset, count, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerOperationException ex) when (ex.ErrorCode == HeapScopeErrorCode.ResultSetNotFound)
        {
            await RequeryAsync(id, cancellationToken).ConfigureAwait(false);
            var current = Info.Id;
            return await _worker.InvokeAsync((service, ct) => service.GetRowsAsync(current, offset, count, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Frees the result set in the worker and finishes the handle; releasing a set that is already gone is not an
    /// error. A re-query in progress completes first, and its new set is the one released.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Guid id;
        try
        {
            if (_released)
            {
                return;
            }

            _released = true;
            id = Info.Id;
        }
        finally
        {
            _gate.Release();
        }

        if (_worker.State == WorkerState.Running)
        {
            await _worker.InvokeAsync((service, ct) => service.ReleaseResultSetAsync(id, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequeryAsync(Guid failedId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_released, this);

            // Another page request may have re-run the query already.
            if (Info.Id == failedId)
            {
                Info = await _query(cancellationToken).ConfigureAwait(false);
                Requeries++;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// A type summary: its pageable rows and the totals of the first run. A re-query (eviction or restart) recomputes the
/// same snapshot, so the totals do not change.
/// </summary>
public sealed record TypeSummary(ResultSetHandle Rows, TypeSummaryResult Result);

/// <summary>The objects of a type: their pageable rows and the totals of the first run (sprint 1.5).</summary>
public sealed record InstanceList(ResultSetHandle Rows, InstancesResult Result);

/// <summary>
/// An instances query: a type key, a filter and sort, the scope within the type (sprint 1.6), and whether to add retained
/// bytes (sprint 2.3).
/// </summary>
public sealed record InstancesQuery(
    string TypeName,
    string? Module,
    string? Filter = null,
    string? SortColumn = null,
    bool Descending = true,
    InstanceScope Scope = InstanceScope.All,
    ulong SameValueAs = 0,
    bool IncludeRetained = false);

/// <summary>An inspection that ran: its rows and its summary (sprint 1.6).</summary>
public sealed record InspectionRun(ResultSetHandle Rows, InspectionResultDto Result);

/// <summary>A snapshot comparison: its pageable rows and the totals of the first run (sprint 1.4).</summary>
public sealed record SnapshotDiff(ResultSetHandle Rows, TypeDiffResult Result);

/// <summary>The objects referencing one object: their pageable rows and the totals of the first run (sprint 2.1).</summary>
public sealed record ReferrerList(ResultSetHandle Rows, ReferrersResult Result);

/// <summary>The objects referencing a type's objects, by their type: pageable rows and the first run's totals (sprint 2.1).</summary>
public sealed record TypeReferrerList(ResultSetHandle Rows, TypeReferrersResult Result);
