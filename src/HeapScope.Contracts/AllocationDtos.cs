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

namespace HeapScope.Contracts;

/// <summary>Opens an EventPipe trace's sampled allocations in the worker (sprint 2.5, ADR-028).</summary>
public sealed record OpenTraceRequest(string Path);

/// <summary>An opened allocation trace.</summary>
/// <param name="ProcessId">The traced process (from its first allocation tick), or 0 when the trace has none.</param>
/// <param name="SampledBytes">What all samples stand for: an estimate of the bytes allocated while recording.</param>
/// <param name="LostEvents">Events the runtime dropped, of any kind: an upper bound on lost samples, whose bytes are lost too.</param>
/// <param name="SamplesWithoutStack">Samples the trace recorded no stack for; trees show them under one frame.</param>
/// <param name="FileBytes">The trace file's size when read; with <paramref name="FileWriteUtc"/>, its identity (the 2.5 review).</param>
/// <param name="FileWriteUtc">The trace file's last write time when read.</param>
/// <param name="SamplesWithUnresolvedFrames">Samples whose stack has a frame the trace does not name.</param>
/// <param name="HasRundown">Whether the trace ends with the runtime's rundown, which names every method still loaded.</param>
/// <param name="ConversionIssues">Problems TraceEvent reported while reading it, such as a stream cut short.</param>
public sealed record TraceDescriptor(
    Guid TraceId,
    string Path,
    int ProcessId,
    string? ProcessName,
    DateTimeOffset StartUtc,
    double DurationSeconds,
    int Samples,
    long SampledBytes,
    int Types,
    long LostEvents,
    long SamplesWithoutStack,
    double LoadSeconds,
    long FileBytes = 0,
    DateTimeOffset FileWriteUtc = default,
    long SamplesWithUnresolvedFrames = 0,
    bool HasRundown = true,
    int ConversionIssues = 0)
{
    /// <summary>Whether this and <paramref name="other"/> were read from the same file content (size and write time).</summary>
    public bool SameFileAs(TraceDescriptor other) =>
        other is not null && FileBytes == other.FileBytes && FileWriteUtc == other.FileWriteUtc;
}

/// <summary>Sampled bytes per stretch of time from the start of the trace to its end.</summary>
public sealed record AllocationTimelineRequest(Guid TraceId, double BucketSeconds = 1);

public sealed record AllocationBucketDto(double StartSeconds, long Bytes, int Samples);

public sealed record AllocationTimelineResult(IReadOnlyList<AllocationBucketDto> Buckets, double BucketSeconds, double DurationSeconds);

/// <summary>The types allocated in an interval (seconds since the trace started; no end means to the end of the trace).</summary>
public sealed record AllocationTypesRequest(Guid TraceId, double FromSeconds = 0, double? ToSeconds = null);

/// <summary>The types (<see cref="AllocationTypeColumns"/>) and what the interval's samples stand for.</summary>
public sealed record AllocationTypesResult(ResultSetInfo Types, long SampledBytes, int Samples);

/// <summary>The call stacks with the most sampled bytes in an interval, of every type or of one.</summary>
public sealed record AllocationStacksRequest(Guid TraceId, double FromSeconds = 0, double? ToSeconds = null, string? TypeName = null, int Count = 20);

/// <summary>One call stack: its frames, the allocating method first, then its callers.</summary>
public sealed record AllocationStackDto(long Bytes, int Samples, IReadOnlyList<string> Frames);

public sealed record AllocationStacksResult(IReadOnlyList<AllocationStackDto> Stacks, long SampledBytes, int Samples);

/// <summary>How an allocation tree is read.</summary>
public enum AllocationTreeKindDto
{
    /// <summary>Top-down: outermost frames first; Own is what a frame allocated itself.</summary>
    CallTree = 0,

    /// <summary>Bottom-up: the allocating frames first, then their callers.</summary>
    BackTraces = 1,
}

/// <summary>
/// The call tree or back traces of an interval, flattened from the open keys like the dominator tree (ADR-027).
/// <paramref name="RevealKey"/> is a row whose index the answer gives back, so a client keeps the user's place.
/// </summary>
public sealed record AllocationTreeRequest(
    Guid TraceId,
    AllocationTreeKindDto Kind,
    IReadOnlyList<string> Expanded,
    double FromSeconds = 0,
    double? ToSeconds = null,
    string? TypeName = null,
    int MaxChildren = 100,
    string? RevealKey = null);

/// <summary>The rows (<see cref="AllocationTreeColumns"/>) and the interval's totals.</summary>
/// <param name="RevealIndex">The row index of the request's reveal key, or -1.</param>
public sealed record AllocationTreeResult(ResultSetInfo Rows, long SampledBytes, int Samples, bool Truncated, int RevealIndex = -1);

/// <summary>Column names of an allocation types result set.</summary>
public static class AllocationTypeColumns
{
    public const string Type = "Type";

    /// <summary>Sampled bytes: an estimate (<see cref="ValueBound.Estimate"/>).</summary>
    public const string Bytes = "Bytes";

    /// <summary>The part of <see cref="Bytes"/> counted on the large object heap.</summary>
    public const string LargeObjectHeap = "LOH bytes";

    /// <summary>The part of <see cref="Bytes"/> counted on the pinned object heap.</summary>
    public const string PinnedObjectHeap = "POH bytes";

    public const string Samples = "Samples";

    /// <summary>The share of the interval's sampled bytes.</summary>
    public const string Percent = "Percent";
}

/// <summary>Column names of an allocation tree result set.</summary>
public static class AllocationTreeColumns
{
    /// <summary>The row's key (<see cref="AllocationTreeKeys"/>).</summary>
    public const string Key = "Key";

    /// <summary>The parent row's key; <see cref="AllocationTreeKeys.Root"/> at the top level.</summary>
    public const string ParentKey = "ParentKey";

    public const string Depth = "Depth";

    /// <summary><c>Frame</c> or <c>More</c>.</summary>
    public const string Kind = "Kind";

    /// <summary>The method, or what a "more" row stands for.</summary>
    public const string Name = "Name";

    /// <summary>Sampled bytes of the stacks through the row.</summary>
    public const string Total = "Total";

    /// <summary>Sampled bytes allocated in the frame itself; empty where it has no meaning (deeper back-trace rows).</summary>
    public const string Own = "Own";

    public const string Samples = "Samples";

    /// <summary>Total as a share of the interval's sampled bytes.</summary>
    public const string Percent = "Percent";

    public const string Children = "Children";

    /// <summary>1 when the row is open.</summary>
    public const string Expanded = "Expanded";
}

/// <summary>The kinds of an allocation tree row (<see cref="AllocationTreeColumns.Kind"/>).</summary>
public static class AllocationRowKinds
{
    public const string Frame = "Frame";

    public const string More = "More";
}

/// <summary>
/// The allocation trees' row keys (sprint 2.5): the top level's list key is <c>root</c>; a call tree row's key is
/// <c>n:</c> and its stack node; a back-trace row's is <c>b:</c> and its frame for the first level, then its parent's key,
/// <c>/</c> and its frame; a "more" row's is its list's key, <c>|m:</c> and how many rows precede it. Contracts owns the
/// format; the analysis mirrors it (ADR-016, a test checks every form).
/// </summary>
public static class AllocationTreeKeys
{
    public const string Root = "root";

    /// <summary>A call tree row: its stack node.</summary>
    public static string Node(int node) => "n:" + node.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A back-trace row: its parent's key and its frame.</summary>
    public static string BackTrace(string parentKey, int frame) =>
        (parentKey == Root ? "b:" : parentKey + "/") + frame.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string More(string listKey, int shown) => listKey + "|m:" + shown.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The allocation queries' limits.</summary>
public static class AllocationTreeLimits
{
    public const int MaxRows = 50_000;

    public const int MaxExpanded = 10_000;

    public const int MinChildren = 1;

    public const int MaxChildren = 1_000;

    public const int MaxDepth = 1_000;

    /// <summary>The most stacks one stacks query returns.</summary>
    public const int MaxStacks = 1_000;

    /// <summary>The shortest and longest timeline stretch, in seconds.</summary>
    public const double MinBucketSeconds = 0.01;

    public const double MaxBucketSeconds = 3_600;

    /// <summary>The most stretches one timeline answer holds (the 2.5 review: the answer is not paged).</summary>
    public const int MaxBuckets = 10_000;
}

/// <summary>What sampled sizes mean, said the same way by every front end (sprint 2.5).</summary>
public static class AllocationTexts
{
    /// <summary>The note for sampled bytes.</summary>
    public const string Sampled = "Estimated from samples: the runtime records an allocation about every 100 KB on each heap, and each sample stands for the bytes allocated there since the previous one, counted at its time. Small, rare allocations may not appear, and an interval's edges are as precise as the samples.";

    /// <summary>What a share of bytes is a share of, said the same way by the UI and the CLI.</summary>
    public const string ShareOf = "% is of the bytes shown: the interval's, of the chosen type when there is one.";

    /// <summary>The note for a trace without its rundown, whose methods may be unnamed.</summary>
    public static string NoRundown(long unresolvedSamples) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"The recording ended before the runtime named its methods (no rundown: the process exited, or the recording was cut short), so {unresolvedSamples:N0} samples have frames shown as [unresolved frame]. Record again and let the recording finish.");

    /// <summary>The note for a trace with frames the trace does not name, although its rundown arrived.</summary>
    public static string Unresolved(long unresolvedSamples) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{unresolvedSamples:N0} samples have frames the trace does not name (shown as [unresolved frame]), such as methods unloaded before the recording ended.");

    /// <summary>The note for a trace TraceEvent reported problems with while reading.</summary>
    public static string Damaged(int issues) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Reading the trace reported {issues:N0} problems (it may have been cut short), so these figures may undercount. Record again if the numbers look low.");

    /// <summary>The note for samples without a stack.</summary>
    public static string NoStack(long samples) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{samples:N0} samples have no stack; the trees show them under [no stack recorded].");

    /// <summary>Every note that applies to <paramref name="trace"/>, in one line.</summary>
    public static string NotesFor(TraceDescriptor trace)
    {
        System.ArgumentNullException.ThrowIfNull(trace);
        var notes = new List<string>();
        if (trace.LostEvents > 0)
        {
            notes.Add(Lost(trace.LostEvents));
        }

        if (!trace.HasRundown)
        {
            notes.Add(NoRundown(trace.SamplesWithUnresolvedFrames));
        }
        else if (trace.SamplesWithUnresolvedFrames > 0)
        {
            notes.Add(Unresolved(trace.SamplesWithUnresolvedFrames));
        }

        if (trace.ConversionIssues > 0)
        {
            notes.Add(Damaged(trace.ConversionIssues));
        }

        if (trace.SamplesWithoutStack > 0)
        {
            notes.Add(NoStack(trace.SamplesWithoutStack));
        }

        return string.Join(" ", notes);
    }

    /// <summary>The note for a trace with lost events.</summary>
    public static string Lost(long lostEvents) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"The runtime dropped {lostEvents:N0} events while recording (its buffer was full), so these figures undercount.");
}
