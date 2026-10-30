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
using HeapScope.Core;
using HeapScope.Core.Allocations;
using HeapScope.Loaders.Trace;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Traces;

/// <summary>
/// The allocation traces open in the worker (sprint 2.5, ADR-028), next to the snapshots and under the same memory
/// budget: a read reserves its working memory. An open trace's arrays are part of the worker's committed memory, which
/// every later reservation counts, so they hold no reservation of their own (that would count them twice, the 2.5
/// re-review); the open line logs what they keep.
/// A trace is read once and kept as compact arrays; the TraceEvent data used to read it is dropped.
/// </summary>
internal sealed class TraceStore
{
    /// <summary>
    /// The working memory reserved while reading: a fixed part for TraceEvent and the runtime's names, plus a part per
    /// byte of the trace file (TraceEvent's conversion and stack index, and the samples kept). The 2.5 review measured
    /// growth with the worker's GC settings (12 heaps): 40 MB for a 5.6 MB trace, 131 MB for 26 MB, 227 MB for 72 MB,
    /// about 76 MB plus 2.1 bytes per file byte; this leaves a margin on each (ARCHITECTURE.md 10).
    /// </summary>
    public const long ReadFixedBytes = 96L * 1024 * 1024;

    /// <inheritdoc cref="ReadFixedBytes"/>
    public const int ReadBytesPerFileByte = 3;

    /// <summary>How old a leftover <c>.etlx</c> must be before the start sweep deletes it.</summary>
    private static readonly TimeSpan s_sweepAge = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, OpenTrace> _traces = new();
    private readonly SnapshotStore _snapshots;
    private readonly string _etlx;
    private readonly ILogger<TraceStore> _logger;

    public TraceStore(SnapshotStore snapshots, WorkerOptions options, ILogger<TraceStore> logger)
    {
        _snapshots = snapshots;
        _logger = logger;
        _etlx = options.Folders.Etlx;

        // Files a killed worker left behind (the 2.5 review); off the starting thread.
        _ = Task.Run(() =>
        {
            try
            {
                var (files, bytes) = AllocationTraceReader.SweepTemporaryFiles(s_sweepAge, _etlx);
                if (files > 0)
                {
                    _logger.LogInformation("Deleted {Files} temporary trace files ({Bytes} bytes) left by an earlier read", files, bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The temporary trace files could not be swept");
            }
        });
    }

    public int Count => _traces.Count;

    /// <summary>The memory a read of a file of <paramref name="fileBytes"/> reserves.</summary>
    public static long ReadReservation(long fileBytes) => ReadFixedBytes + (fileBytes * ReadBytesPerFileByte);

    public async Task<TraceDescriptor> OpenAsync(OpenTraceRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Path);
        var path = Path.GetFullPath(request.Path);
        var file = new FileInfo(path);
        var (length, written) = file.Exists ? (file.Length, new DateTimeOffset(file.LastWriteTimeUtc)) : (0L, default);
        var started = Stopwatch.GetTimestamp();
        AllocationTrace trace;
        var next = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Close other traces or snapshots, or record a shorter trace (this one is {length / (1024 * 1024):N0} MB), and try again.");
        using (_snapshots.ReserveWorking(ReadReservation(length), "Reading the allocation trace", next))
        {
            try
            {
                var phases = new Progress<string>(phase => progress?.Report(new JobProgress(phase, 0, null)));
                trace = await Task.Run(() => AllocationTraceReader.Read(path, phases, _etlx, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                MemoryReservations.Collect();
            }
        }

        var info = trace.Info;
        if (trace.SampleCount == 0)
        {
            _logger.LogInformation("Refused allocation trace {Path} ({FileBytes} bytes, written {Written:O}): no allocation samples", path, length, written);
            // Either cause leaves no sample: another profile, or a process that allocated too little (the 2.5 re-review).
            throw new SnapshotFormatException($"{path} has no allocation samples: it was recorded with another profile, or the process allocated less than about 100 KB per heap while recording. Record with Record allocations on the Processes page (or 'heapscope-cli trace --profile allocations') while the application is busy, or for longer.");
        }

        var kept = KeptBytes(trace);
        var id = Guid.NewGuid();
        var open = new OpenTrace(path, trace);
        _traces[id] = open;
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        // The trace's identity and what reading it found (the 2.5 review): a disputed figure is matched to this line.
        _logger.LogInformation(
            "Opened allocation trace {Id} ({Path}, {FileBytes} bytes, written {Written:O}) with reader {Reader} in {Seconds:N2} s: process {ProcessId} {ProcessName} (from its first allocation tick), recording started {Start:O}, {Duration:N1} s recorded, {Samples} samples of {Bytes} bytes in {Types} types, {Frames} frames, {Nodes} stack nodes, {Lost} lost events, {NoStack} samples without a stack, {Unresolved} samples with unresolved frames, rundown {Rundown}, {Issues} conversion problems {FirstIssues}; keeps {Kept} bytes",
            id,
            path,
            length,
            written,
            AllocationTraceReader.Version,
            seconds,
            info.ProcessId,
            info.ProcessName ?? "unknown",
            info.StartUtc,
            info.DurationMs / 1000,
            trace.SampleCount,
            open.TotalBytes,
            trace.Types.Count,
            trace.Frames.Count,
            trace.StackFrames.Length,
            info.LostEvents,
            info.SamplesWithoutStack,
            info.SamplesWithUnresolvedFrames,
            info.HasRundown ? "seen" : "missing",
            info.ConversionIssues,
            string.Join(" | ", info.FirstIssues ?? []),
            kept);
        return new TraceDescriptor(
            id,
            path,
            info.ProcessId,
            info.ProcessName,
            new DateTimeOffset(DateTime.SpecifyKind(info.StartUtc, DateTimeKind.Utc)),
            info.DurationMs / 1000,
            trace.SampleCount,
            open.TotalBytes,
            trace.Types.Count,
            info.LostEvents,
            info.SamplesWithoutStack,
            seconds,
            length,
            written,
            info.SamplesWithUnresolvedFrames,
            info.HasRundown,
            info.ConversionIssues);
    }

    public bool Close(Guid traceId)
    {
        if (!_traces.TryRemove(traceId, out var open))
        {
            return false;
        }

        MemoryReservations.Collect();
        _logger.LogInformation("Closed allocation trace {Id} ({Path})", traceId, open.Path);
        return true;
    }

    public OpenTrace? TryGet(Guid traceId) => _traces.TryGetValue(traceId, out var open) ? open : null;

    /// <summary>What an open trace keeps: its samples, stack tree and names.</summary>
    private static long KeptBytes(AllocationTrace trace) =>
        ((long)trace.SampleCount * AllocationTrace.BytesPerSample)
        + (trace.StackFrames.Length * 8L)
        + trace.Frames.Sum(f => 40L + (2L * f.Length))
        + trace.Types.Sum(t => 40L + (2L * t.Length));
}

/// <summary>An open trace with its type ids by name.</summary>
internal sealed class OpenTrace(string path, AllocationTrace trace)
{
    public string Path { get; } = path;

    public AllocationTrace Trace { get; } = trace;

    public Dictionary<string, int> TypeIds { get; } = Index(trace);

    public long TotalBytes { get; } = trace.SampleBytes.Sum();

    private static Dictionary<string, int> Index(AllocationTrace trace)
    {
        var ids = new Dictionary<string, int>(trace.Types.Count, StringComparer.Ordinal);
        for (var t = 0; t < trace.Types.Count; t++)
        {
            ids.TryAdd(trace.Types[t], t);
        }

        return ids;
    }
}
