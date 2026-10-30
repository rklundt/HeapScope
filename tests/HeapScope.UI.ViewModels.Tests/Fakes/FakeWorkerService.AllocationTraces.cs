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

/// <summary>The fake worker's AllocationTraces (<see cref="IAllocationTraceService"/>).</summary>
internal sealed partial class FakeWorkerService : IAllocationTraceService
{
    public Task<TraceDescriptor> OpenTraceAsync(OpenTraceRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        if (OpenFailure is { } failure)
        {
            return Task.FromException<TraceDescriptor>(failure);
        }

        TracesOpened.Add(request);
        var descriptor = new TraceDescriptor(Guid.NewGuid(), request.Path, 42, "App", DateTimeOffset.UnixEpoch, 10, 4, 400, 2, 0, 0, 0.1, TraceFileBytes, DateTimeOffset.UnixEpoch);
        return OpenTraceGate is { } gate ? gate.Task.ContinueWith(_ => descriptor, TaskScheduler.Default) : Task.FromResult(descriptor);
    }

    public Task CloseTraceAsync(Guid traceId, CancellationToken cancellationToken)
    {
        TracesClosed.Add(traceId);
        return Task.CompletedTask;
    }

    public Task<AllocationTimelineResult> QueryAllocationTimelineAsync(AllocationTimelineRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AllocationTimelineResult([new(0, 300, 3), new(5, 100, 1)], 5, 10));

    public Task<AllocationTypesResult> QueryAllocationTypesAsync(AllocationTypesRequest request, CancellationToken cancellationToken)
    {
        TypeQueries.Add(request);
        List<IReadOnlyList<string?>> rows = [["System.Byte[]", "300", "3", "75"], ["System.String", "100", "1", "25"]];
        var id = Guid.NewGuid();
        _sets[id] = rows;
        return Task.FromResult(new AllocationTypesResult(new ResultSetInfo(id, rows.Count, AllocationTypeColumnsList), 400, 4));
    }

    public Task<AllocationStacksResult> QueryAllocationStacksAsync(AllocationStacksRequest request, CancellationToken cancellationToken)
    {
        StackQueries.Add(request);
        AllocationStackDto[] stacks = [new(300, 3, ["App.Grow", "App.Handle", "App.Main"]), new(100, 1, ["App.Log", "App.Main"])];
        var shown = request.TypeName switch
        {
            "System.Byte[]" => stacks[..1],
            "System.String" => stacks[1..],
            _ => stacks,
        };
        return Task.FromResult(new AllocationStacksResult(shown, shown.Sum(s => s.Bytes), shown.Sum(s => s.Samples)));
    }

    public Task<AllocationTreeResult> QueryAllocationTreeAsync(AllocationTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        AllocationTreeQueries.Add(request);
        var nodes = (request.Kind == AllocationTreeKindDto.CallTree ? CallTreeNodes : BackTraceNodes)
            .Where(n => request.TypeName is null || n.Type is null || n.Type == request.TypeName)
            .ToArray();
        var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
        var rows = new List<IReadOnlyList<string?>>();
        var keys = new List<string>();
        var total = nodes.Where(n => n.Parent == AllocationTreeKeys.Root).Sum(n => n.Total);
        void Emit(string parent, int depth)
        {
            foreach (var node in nodes.Where(n => n.Parent == parent))
            {
                var children = nodes.Count(n => n.Parent == node.Key);
                var open = children > 0 && expanded.Contains(node.Key);
                rows.Add(
                [
                    node.Key, node.Parent, depth.ToString(CultureInfo.InvariantCulture), AllocationRowKinds.Frame, node.Name,
                    node.Total.ToString(CultureInfo.InvariantCulture), node.Own < 0 ? null : node.Own.ToString(CultureInfo.InvariantCulture), "1",
                    (100.0 * node.Total / total).ToString("0.##", CultureInfo.InvariantCulture), children.ToString(CultureInfo.InvariantCulture), open ? "1" : "0",
                ]);
                keys.Add(node.Key);
                if (open)
                {
                    Emit(node.Key, depth + 1);
                }
            }
        }

        Emit(AllocationTreeKeys.Root, 0);
        var id = Guid.NewGuid();
        _sets[id] = rows;
        return Task.FromResult(new AllocationTreeResult(new ResultSetInfo(id, rows.Count, AllocationTreeColumnsList), total, 4, false, request.RevealKey is { } reveal ? keys.IndexOf(reveal) : -1));
    }
}
