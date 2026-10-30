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

/// <summary>The fake worker's HeapQueries (<see cref="IHeapQueryService"/>).</summary>
internal sealed partial class FakeWorkerService : IHeapQueryService
{
    public async Task<TypeSummaryResult> QueryTypeSummaryAsync(TypeSummaryRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        SummaryQueries.Add(request);
        if (request.IncludeRetained && RetainedFailure is { } failure)
        {
            throw failure;
        }

        if (SummaryGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        // The worker's order: the sort column, then the name ascending for ties.
        var matching = Rows.Where(r => NameFilter.IsMatch(r[0]!, request.Filter));
        Func<IReadOnlyList<string?>, long> key = request.SortColumn switch
        {
            TypeSummaryColumns.Objects => r => long.Parse(r[1]!, System.Globalization.CultureInfo.InvariantCulture),
            _ => r => long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture),
        };
        List<IReadOnlyList<string?>> rows = request.SortColumn == TypeSummaryColumns.Name
            ? (request.Descending
                ? [.. matching.OrderByDescending(r => r[0], StringComparer.OrdinalIgnoreCase).ThenBy(r => r[0], StringComparer.Ordinal)]
                : [.. matching.OrderBy(r => r[0], StringComparer.OrdinalIgnoreCase).ThenBy(r => r[0], StringComparer.Ordinal)])
            : (request.Descending
                ? [.. matching.OrderByDescending(key).ThenBy(r => r[0], StringComparer.Ordinal)]
                : [.. matching.OrderBy(key).ThenBy(r => r[0], StringComparer.Ordinal)]);

        // Type rows carry the hidden key columns, as the worker's do; retained bytes (sprint 2.3) are three times the bytes.
        var byType = request.GroupBy == TypeGrouping.Type;
        var retained = request.IncludeRetained;
        if (retained)
        {
            progress?.Report(new JobProgress("Finding immediate dominators", 50, 100));
        }

        rows = [.. rows.Select(r => (IReadOnlyList<string?>)[
            .. r.Take(3), r.Count >= 4 ? r[3] : "0",
            .. retained ? [(3 * long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture)] : Array.Empty<string?>(),
            .. byType ? [r[0], "App.dll"] : Array.Empty<string?>()])];
        var id = Guid.NewGuid();
        _sets[id] = rows;
        ResultColumn[] columns = [new("Name", ResultColumnKind.Text, true), new("Objects", ResultColumnKind.Integer), new("Bytes", ResultColumnKind.Bytes), new("Percent", ResultColumnKind.Percent),
            .. retained ? new ResultColumn[] { new(TypeSummaryColumns.Retained, ResultColumnKind.Bytes, Bound: ValueBound.LowerBound) } : [],
            .. byType ? new ResultColumn[] { new(TypeSummaryColumns.TypeName, ResultColumnKind.Text), new(TypeSummaryColumns.Module, ResultColumnKind.Text) } : []];
        var totalBytes = Rows.Sum(r => long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture));
        HeapKindTotal[] kinds = HasGenerations ? [new(HeapKindDto.Gen0, 10, totalBytes / 2), new(HeapKindDto.Gen2, 5, totalBytes / 2)] : [];
        return new TypeSummaryResult(
            new ResultSetInfo(id, rows.Count, columns),
            Rows.Sum(r => long.Parse(r[1]!, System.Globalization.CultureInfo.InvariantCulture)),
            totalBytes,
            Rows.Count,
            HasGenerations,
            kinds,
            0.1,
            Rows.Count,
            retained ? Dominators : null);
    }

    public Task<InstancesResult> QueryInstancesAsync(InstancesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        InstanceQueries.Add(request);
        if (request.IncludeRetained && RetainedFailure is { } failure)
        {
            return Task.FromException<InstancesResult>(failure);
        }

        List<IReadOnlyList<string?>> rows = [.. InstanceRows.Where(r => string.IsNullOrEmpty(request.Filter) || (r[4] ?? string.Empty).Contains(request.Filter, StringComparison.OrdinalIgnoreCase))];

        // Sprint 2.3: retained bytes are twice the bytes, and empty for a Gen0 object (as if no root reached it).
        if (request.IncludeRetained)
        {
            rows = [.. rows.Select(r => (IReadOnlyList<string?>)[.. r.Take(5), r[3] == "Gen0" ? null : (2 * long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture)])];
        }

        var id = Guid.NewGuid();
        _sets[id] = rows;
        ResultColumn[] columns =
        [
            new(InstanceColumns.Address, ResultColumnKind.Address, true), new(InstanceColumns.Type, ResultColumnKind.Text), new(InstanceColumns.Bytes, ResultColumnKind.Bytes),
            new(InstanceColumns.Generation, ResultColumnKind.Text), new(InstanceColumns.Value, ResultColumnKind.Text),
            .. request.IncludeRetained ? new ResultColumn[] { new(InstanceColumns.Retained, ResultColumnKind.Bytes) } : [],
        ];
        return Task.FromResult(new InstancesResult(
            new ResultSetInfo(id, rows.Count, columns),
            InstanceRows.Count,
            InstanceRows.Sum(r => long.Parse(r[2]!, System.Globalization.CultureInfo.InvariantCulture)),
            1,
            ValuesAvailable,
            0.1,
            request.IncludeRetained ? Dominators : null));
    }

    public Task<ObjectDetails> GetObjectAsync(ObjectRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(request.Address, out var details)
            ? details
            : new ObjectDetails(request.Address, "App.Order", 40, HeapKindDto.Gen2, ReferenceRows.Count, false,
                [new ObjectFieldDto("Id", "System.Int32", ObjectFieldKindDto.Primitive, "7", 0, [])], null, null));

    public Task<ResultSetInfo> QueryReferencesAsync(ReferencesRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        _sets[id] = [.. ReferenceRows];
        return Task.FromResult(new ResultSetInfo(id, ReferenceRows.Count,
        [
            new(ReferenceColumns.Field, ResultColumnKind.Text), new(ReferenceColumns.Address, ResultColumnKind.Address, true),
            new(ReferenceColumns.Type, ResultColumnKind.Text), new(ReferenceColumns.Bytes, ResultColumnKind.Bytes),
        ]));
    }

    public Task<ReferrersResult> QueryReferrersAsync(ReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ReferrerQueries.Add(request);
        if (ReferrerProgress is { } report)
        {
            progress?.Report(report);
        }

        var id = Guid.NewGuid();
        _sets[id] = [.. ReferrerRows];
        var info = new ResultSetInfo(id, ReferrerRows.Count,
        [
            new(ReferrerColumns.Field, ResultColumnKind.Text), new(ReferrerColumns.Address, ResultColumnKind.Address, true),
            new(ReferrerColumns.Type, ResultColumnKind.Text), new(ReferrerColumns.Bytes, ResultColumnKind.Bytes),
            new(ReferrerColumns.References, ResultColumnKind.Integer),
        ]);
        var references = ReferrerRows.Sum(r => long.Parse(r[4]!, System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(new ReferrersResult(info, references, null, new ReverseIndexInfo(false, SnapshotMemoryMode.InMemory, 0.2, 1024, true), CompleteGraph));
    }

    public Task<TypeReferrersResult> QueryTypeReferrersAsync(TypeReferrersRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        TypeReferrerQueries.Add(request);
        var id = Guid.NewGuid();
        _sets[id] = [.. TypeReferrerRows];
        var info = new ResultSetInfo(id, TypeReferrerRows.Count,
        [
            new(TypeReferrerColumns.Type, ResultColumnKind.Text), new(TypeReferrerColumns.Module, ResultColumnKind.Text),
            new(TypeReferrerColumns.Objects, ResultColumnKind.Integer), new(TypeReferrerColumns.References, ResultColumnKind.Integer),
            new(TypeReferrerColumns.Targets, ResultColumnKind.Integer), new(TypeReferrerColumns.Bytes, ResultColumnKind.Bytes),
        ]);
        var references = TypeReferrerRows.Sum(r => long.Parse(r[3]!, System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(new TypeReferrersResult(info, 1, 5, references, new ReverseIndexInfo(true, SnapshotMemoryMode.InMemory, 0.1, 1024, true), CompleteGraph));
    }

    public async Task<TypeDiffResult> QueryTypeDiffAsync(TypeDiffRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        DiffQueries.Add(request);
        if (SummaryGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        string[] names = [TypeDiffColumns.Name, TypeDiffColumns.Change, TypeDiffColumns.BaselineObjects, TypeDiffColumns.CurrentObjects, TypeDiffColumns.ObjectsDelta, TypeDiffColumns.BaselineBytes, TypeDiffColumns.CurrentBytes, TypeDiffColumns.BytesDelta, TypeDiffColumns.BaselineTypes, TypeDiffColumns.CurrentTypes, TypeDiffColumns.TypeName, TypeDiffColumns.Module];
        var column = Array.IndexOf(names, request.SortColumn ?? TypeDiffColumns.BytesDelta);
        if (column < 0)
        {
            throw new HeapScope.Application.Workers.WorkerOperationException(HeapScopeErrorCode.InvalidArgument, $"Unknown sort column {request.SortColumn}.", null);
        }

        var matching = DiffRows.Where(r => NameFilter.IsMatch(r[0]!, request.Filter));
        IOrderedEnumerable<IReadOnlyList<string?>> ordered = column <= 1
            ? (request.Descending ? matching.OrderByDescending(r => r[column], StringComparer.OrdinalIgnoreCase) : matching.OrderBy(r => r[column], StringComparer.OrdinalIgnoreCase))
            : (request.Descending ? matching.OrderByDescending(r => long.Parse(r[column]!, System.Globalization.CultureInfo.InvariantCulture)) : matching.OrderBy(r => long.Parse(r[column]!, System.Globalization.CultureInfo.InvariantCulture)));
        List<IReadOnlyList<string?>> rows = [.. ordered.ThenBy(r => r[0], StringComparer.Ordinal)];

        // Sprint 3.3: New and Dead objects (1 and 0 per row) when asked and not refused by IdentityNote.
        var matched = request.MatchObjects && IdentityNote is null;
        if (matched)
        {
            names = [.. names, TypeDiffColumns.NewObjects, TypeDiffColumns.DeadObjects];
            rows = [.. rows.Select(r => (IReadOnlyList<string?>)[.. r, "1", "0"])];
        }

        var id = Guid.NewGuid();
        _sets[id] = rows;
        DiffSets.Add(id);
        static long Sum(IEnumerable<IReadOnlyList<string?>> rows, int index) => rows.Sum(r => long.Parse(r[index]!, System.Globalization.CultureInfo.InvariantCulture));
        return new TypeDiffResult(
            new ResultSetInfo(id, rows.Count, [.. names.Select((n, i) => new ResultColumn(n, i <= 1 ? ResultColumnKind.Text : i is 4 or 7 ? ResultColumnKind.SignedInteger : ResultColumnKind.Integer, i == 0))]),
            Sum(DiffRows, 2),
            Sum(DiffRows, 5),
            Sum(DiffRows, 3),
            Sum(DiffRows, 6),
            DiffRows.Count(r => r[1] == TypeDiffColumns.New),
            DiffRows.Count(r => r[1] == TypeDiffColumns.Gone),
            DiffRows.Count,
            Sum(rows, 5),
            Sum(rows, 7),
            0.1,
            DiffExact,
            matched,
            request.MatchObjects && !matched ? IdentityNote : null,
            matched ? rows.Count : 0,
            0);
    }

    public Task<TypeTrendResult> QueryTypeTrendAsync(TypeTrendRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        TrendQueries.Add(request);
        var n = request.SnapshotIds.Count;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        ResultColumn[] columns =
        [
            new(TypeTrendColumns.Name, ResultColumnKind.Text, IsKey: true),
            .. Enumerable.Range(1, n).Select(i => new ResultColumn(TypeTrendColumns.Objects(i), ResultColumnKind.Integer)),
            .. Enumerable.Range(1, n).Select(i => new ResultColumn(TypeTrendColumns.Bytes(i), ResultColumnKind.Bytes)),
            new(TypeTrendColumns.ObjectsDelta, ResultColumnKind.SignedInteger),
            new(TypeTrendColumns.BytesDelta, ResultColumnKind.SignedInteger),
            new(TypeTrendColumns.GrowthSteps, ResultColumnKind.Integer),
            new(TypeTrendColumns.TypeName, ResultColumnKind.Text, IsHidden: true),
            new(TypeTrendColumns.Module, ResultColumnKind.Text, IsHidden: true),
        ];
        List<IReadOnlyList<string?>> rows = [.. TrendRows.Select(r => (IReadOnlyList<string?>)
        [
            r.Name,
            .. r.Objects.Take(n).Select(o => o.ToString(invariant)),
            .. r.Objects.Take(n).Select(o => (o * 10).ToString(invariant)),
            (r.Objects[n - 1] - r.Objects[0]).ToString(invariant),
            ((r.Objects[n - 1] - r.Objects[0]) * 10).ToString(invariant),
            Enumerable.Range(1, n - 1).Count(i => r.Objects[i] > r.Objects[i - 1]).ToString(invariant),
            r.Name,
            "App.dll",
        ])];
        var id = Guid.NewGuid();
        _sets[id] = rows;
        return Task.FromResult(new TypeTrendResult(new ResultSetInfo(id, rows.Count, columns), [.. Enumerable.Repeat(100L, n)], rows.Count, 0.1, true));
    }
}
