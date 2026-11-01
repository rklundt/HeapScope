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
using HeapScope.Analysis.Allocations;
using HeapScope.Contracts;
using HeapScope.Worker.Traces;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Allocation traces (<see cref="IAllocationTraceService"/>, ADR-028): open, close and the queries over an interval.</summary>
internal sealed class AllocationTraceHandler(
    TraceStore traces,
    ResultSetStore resultSets,
    ILogger<AllocationTraceHandler> logger) : RpcHandler(logger), IAllocationTraceService
{
    /// <summary>Rows written to a query's provenance line.</summary>
    private const int LoggedAllocationRows = 10;

    /// <summary>Frames of each stack written to the stacks line; the stack node gives the rest.</summary>
    private const int LoggedFrames = 16;

    /// <summary>Expanded keys written to the provenance line.</summary>
    private const int LoggedKeys = 20;

    public Task<TraceDescriptor> OpenTraceAsync(OpenTraceRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, () => traces.OpenAsync(request, progress is null ? null : new ThrottledProgress<JobProgress>(progress), cancellationToken));

    public Task CloseTraceAsync(Guid traceId, CancellationToken cancellationToken) =>
        GuardAsync<bool>(traceId, () => Task.FromResult(traces.Close(traceId)));

    public Task<AllocationTimelineResult> QueryAllocationTimelineAsync(AllocationTimelineRequest request, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.BucketSeconds is < AllocationTreeLimits.MinBucketSeconds or > AllocationTreeLimits.MaxBucketSeconds || double.IsNaN(request.BucketSeconds))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A timeline stretch must be between {AllocationTreeLimits.MinBucketSeconds} and {AllocationTreeLimits.MaxBucketSeconds:N0} seconds."));
            }

            var open = Trace(request.TraceId);
            if (AllocationProfile.BucketCount(open.Trace, request.BucketSeconds * 1000) > AllocationTreeLimits.MaxBuckets)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A timeline has at most {AllocationTreeLimits.MaxBuckets:N0} stretches; ask for longer ones (this trace is {open.Trace.Info.DurationMs / 1000:N1} s)."));
            }

            var buckets = await Task.Run(() => AllocationProfile.Timeline(open.Trace, request.BucketSeconds * 1000, cancellationToken), cancellationToken).ConfigureAwait(false);
            var result = new AllocationTimelineResult(
                [.. buckets.Select(b => new AllocationBucketDto(b.StartMs / 1000, b.Bytes, b.Samples))],
                request.BucketSeconds,
                open.Trace.Info.DurationMs / 1000);
            Logger.LogInformation(
                "Allocation timeline of trace {Id}: {Buckets} stretches of {Seconds} s, busiest {Busiest}",
                request.TraceId,
                buckets.Length,
                request.BucketSeconds,
                string.Join("; ", buckets.OrderByDescending(b => b.Bytes).Take(3).Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.StartMs / 1000:0.##} s {b.Bytes} bytes"))));
            return result;
        });

    public Task<AllocationTypesResult> QueryAllocationTypesAsync(AllocationTypesRequest request, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var open = Trace(request.TraceId);
            var filter = Filter(open, request.FromSeconds, request.ToSeconds, null);
            var (rows, (bytes, samples)) = await Task.Run(
                () => (AllocationProfile.Types(open.Trace, filter, cancellationToken), AllocationProfile.Totals(open.Trace, filter, cancellationToken)),
                cancellationToken).ConfigureAwait(false);
            Logger.LogInformation(
                "Allocation types of trace {Id} from {From} s to {To}: {Types} types, {Samples} samples of {Bytes} bytes; top {Top}",
                request.TraceId,
                request.FromSeconds,
                To(request.ToSeconds),
                rows.Length,
                samples,
                bytes,
                string.Join("; ", rows.Take(LoggedAllocationRows).Select(r => string.Create(CultureInfo.InvariantCulture, $"{open.Trace.Types[r.Type]} {r.Bytes} bytes ({r.Samples} samples, LOH {r.LargeBytes}, POH {r.PinnedBytes})"))));
            return new AllocationTypesResult(resultSets.Add(new AllocationTypesSource(open.Trace.Types, rows, bytes)), bytes, samples);
        });

    public Task<AllocationStacksResult> QueryAllocationStacksAsync(AllocationStacksRequest request, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Count is < 1 or > AllocationTreeLimits.MaxStacks)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Ask for between 1 and {AllocationTreeLimits.MaxStacks:N0} stacks."));
            }

            var open = Trace(request.TraceId);
            var filter = Filter(open, request.FromSeconds, request.ToSeconds, request.TypeName);
            var (top, (bytes, samples)) = await Task.Run(
                () => (AllocationProfile.TopStacks(open.Trace, filter, request.Count, cancellationToken), AllocationProfile.Totals(open.Trace, filter, cancellationToken)),
                cancellationToken).ConfigureAwait(false);
            var stacks = top.Select(s => new AllocationStackDto(s.Bytes, s.Samples, [.. AllocationProfile.FramesOf(open.Trace, s.Node).Select(f => open.Trace.Frames[f])])).ToArray();

            // Every stack answered, with its rank, its innermost stack node (which rebuilds it from the same trace) and its
            // frames, allocating frame first: framework frames come first, so the disputed one may be deep (the 2.5 review).
            Logger.LogInformation(
                "Allocation stacks of trace {Id} from {From} s to {To}, type {Type}: {Samples} samples of {Bytes} bytes, {Count} stacks: {Stacks}",
                request.TraceId,
                request.FromSeconds,
                To(request.ToSeconds),
                request.TypeName ?? "all",
                samples,
                bytes,
                stacks.Length,
                string.Join(" || ", top.Select((s, i) => string.Create(
                    CultureInfo.InvariantCulture,
                    $"#{i + 1} {s.Bytes} bytes ({s.Samples} samples) node {s.Node}, {stacks[i].Frames.Count} frames: {string.Join(" < ", stacks[i].Frames.Take(LoggedFrames))}{(stacks[i].Frames.Count > LoggedFrames ? " < ..." : string.Empty)}"))));
            return new AllocationStacksResult(stacks, bytes, samples);
        });

    public Task<AllocationTreeResult> QueryAllocationTreeAsync(AllocationTreeRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.MaxChildren is < AllocationTreeLimits.MinChildren or > AllocationTreeLimits.MaxChildren)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Children per row must be between {AllocationTreeLimits.MinChildren} and {AllocationTreeLimits.MaxChildren:N0}."));
            }

            if (request.Expanded is null || request.Expanded.Count > AllocationTreeLimits.MaxExpanded)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"At most {AllocationTreeLimits.MaxExpanded:N0} rows can be open at once. Collapse some rows."));
            }

            var open = Trace(request.TraceId);
            var filter = Filter(open, request.FromSeconds, request.ToSeconds, request.TypeName);
            var kind = request.Kind == AllocationTreeKindDto.BackTraces ? AllocationTreeKind.BackTraces : AllocationTreeKind.CallTree;
            var expanded = request.Expanded.ToHashSet(StringComparer.Ordinal);
            progress?.Report(new JobProgress(kind == AllocationTreeKind.CallTree ? "Building the call tree" : "Building the back traces", 0, null));
            var view = await Task.Run(() => AllocationTreeRows.Build(open.Trace, filter, kind, expanded, request.MaxChildren, cancellationToken), cancellationToken).ConfigureAwait(false);
            var reveal = -1;
            for (var i = 0; request.RevealKey is not null && i < view.Rows.Count; i++)
            {
                if (view.Rows[i].Key == request.RevealKey)
                {
                    reveal = i;
                    break;
                }
            }

            // What the user looked at: the rows of the list just opened (or the top), the open keys and the interval.
            var shown = reveal >= 0 && view.Rows[reveal].Expanded
                ? view.Rows.Where(r => r.ParentKey == request.RevealKey).Take(LoggedAllocationRows)
                : view.Rows.Take(LoggedAllocationRows);
            Logger.LogInformation(
                "Allocation {Kind} of trace {Id} from {From} s to {To}, type {Type}: {Samples} samples of {Bytes} bytes, {Rows} rows{Truncated}; {Open} open rows {Keys}; reveal {Reveal} at row {RevealIndex}; rows shown under it {Shown}",
                kind,
                request.TraceId,
                request.FromSeconds,
                To(request.ToSeconds),
                request.TypeName ?? "all",
                view.TotalSamples,
                view.TotalBytes,
                view.Rows.Count,
                view.Truncated ? " (cut at the row limit)" : string.Empty,
                request.Expanded.Count,
                string.Join(", ", request.Expanded.Take(LoggedKeys)),
                request.RevealKey ?? "none",
                reveal,
                string.Join("; ", shown.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Name} total {r.Total} own {r.Own}"))));
            return new AllocationTreeResult(resultSets.Add(new AllocationTreeSource(view)), view.TotalBytes, view.TotalSamples, view.Truncated, reveal);
        });

    private OpenTrace Trace(Guid traceId) => traces.TryGet(traceId) ?? throw WorkerErrors.Create(
        HeapScopeErrorCode.TraceNotLoaded,
        $"Allocation trace {traceId} is not open in the analysis worker: it was closed or the worker restarted. Open it again.");

    private static AllocationFilter Filter(OpenTrace open, double fromSeconds, double? toSeconds, string? typeName)
    {
        if (double.IsNaN(fromSeconds) || fromSeconds < 0 || toSeconds is { } to && (double.IsNaN(to) || to <= fromSeconds))
        {
            throw new ArgumentException("An interval starts at 0 seconds or later and ends after it starts.");
        }

        var type = -1;
        if (typeName is not null && !open.TypeIds.TryGetValue(typeName, out type))
        {
            // The text may be what someone typed: not repeated (ARCHITECTURE.md 11).
            throw new ArgumentException("No sample in this trace has that type. The types view ('allocations <trace> --view types' on the command line) lists the type names.");
        }

        return new AllocationFilter(fromSeconds * 1000, toSeconds is { } end ? end * 1000 : double.PositiveInfinity, type);
    }

    private static string To(double? toSeconds) => toSeconds is { } to ? to.ToString("0.###", CultureInfo.InvariantCulture) + " s" : "the end";
}
