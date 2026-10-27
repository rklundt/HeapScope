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

using System.Collections.Concurrent;
using System.Diagnostics;
using HeapScope.Contracts;
using HeapScope.Core.Allocations;
using HeapScope.Loaders.Trace;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Traces;

/// <summary>
/// The allocation traces open in the worker (sprint 2.5, ADR-028), next to the snapshots and under the same memory
/// budget. A trace is read once and kept as compact arrays; the TraceEvent data used to read it is dropped.
/// </summary>
internal sealed class TraceStore(SnapshotStore snapshots, ILogger<TraceStore> logger)
{
    /// <summary>
    /// The memory reserved while reading, per byte of the trace file: TraceEvent's conversion and its stack index, plus
    /// the samples kept. The 2.5 benchmark measured a peak growth of about 4.6 bytes per byte (a 5.6 MB, 54 s LeakyApi
    /// trace); 6 leaves headroom.
    /// </summary>
    public const int ReadBytesPerFileByte = 6;

    private readonly ConcurrentDictionary<Guid, OpenTrace> _traces = new();

    public int Count => _traces.Count;

    public async Task<TraceDescriptor> OpenAsync(OpenTraceRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Path);
        var path = Path.GetFullPath(request.Path);
        var length = File.Exists(path) ? new FileInfo(path).Length : 0;
        var started = Stopwatch.GetTimestamp();
        AllocationTrace trace;
        using (snapshots.ReserveWorking(length * ReadBytesPerFileByte, "Reading the allocation trace"))
        {
            try
            {
                var phases = new Progress<string>(phase => progress?.Report(new JobProgress(phase, 0, null)));
                trace = await Task.Run(() => AllocationTraceReader.Read(path, phases, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                MemoryReservations.Collect();
            }
        }

        var id = Guid.NewGuid();
        var open = new OpenTrace(path, trace);
        _traces[id] = open;
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        logger.LogInformation(
            "Opened allocation trace {Id} ({Path}, {FileBytes} bytes) in {Seconds:N2} s: process {ProcessId}, {Duration:N1} s recorded, {Samples} samples of {Bytes} bytes in {Types} types, {Frames} frames, {Nodes} stack nodes, {Lost} lost events, {NoStack} samples without a stack",
            id,
            path,
            length,
            seconds,
            trace.Info.ProcessId,
            trace.Info.DurationMs / 1000,
            trace.SampleCount,
            open.TotalBytes,
            trace.Types.Count,
            trace.Frames.Count,
            trace.StackFrames.Length,
            trace.Info.LostEvents,
            trace.Info.SamplesWithoutStack);
        return new TraceDescriptor(
            id,
            path,
            trace.Info.ProcessId,
            trace.Info.ProcessName,
            new DateTimeOffset(DateTime.SpecifyKind(trace.Info.StartUtc, DateTimeKind.Utc)),
            trace.Info.DurationMs / 1000,
            trace.SampleCount,
            open.TotalBytes,
            trace.Types.Count,
            trace.Info.LostEvents,
            trace.Info.SamplesWithoutStack,
            seconds);
    }

    public bool Close(Guid traceId)
    {
        var closed = _traces.TryRemove(traceId, out _);
        if (closed)
        {
            logger.LogInformation("Closed allocation trace {Id}", traceId);
        }

        return closed;
    }

    public OpenTrace? TryGet(Guid traceId) => _traces.TryGetValue(traceId, out var open) ? open : null;
}

/// <summary>An open trace with its type ids by name.</summary>
internal sealed class OpenTrace
{
    public OpenTrace(string path, AllocationTrace trace)
    {
        Path = path;
        Trace = trace;
        TypeIds = new Dictionary<string, int>(trace.Types.Count, StringComparer.Ordinal);
        for (var t = 0; t < trace.Types.Count; t++)
        {
            TypeIds.TryAdd(trace.Types[t], t);
        }

        TotalBytes = trace.SampleBytes.Sum();
    }

    public string Path { get; }

    public AllocationTrace Trace { get; }

    public Dictionary<string, int> TypeIds { get; }

    public long TotalBytes { get; }
}
