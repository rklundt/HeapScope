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

namespace HeapScope.Core.Allocations;

/// <summary>The heap an allocation tick counted bytes for (the runtime counts ticks per heap and per kind).</summary>
public enum AllocationKind : byte
{
    /// <summary>The small object heap.</summary>
    Small = 0,

    /// <summary>The large object heap (objects of 85,000 bytes or more).</summary>
    Large = 1,

    /// <summary>The pinned object heap (.NET 5 and later).</summary>
    Pinned = 2,
}

/// <summary>
/// Sampled allocations read from an EventPipe trace (sprint 2.5, ADR-028): one sample about every 100 KB allocated per
/// heap and kind, each with its type, kind, the bytes it stands for and its call stack. Holds type and method names,
/// times and sizes only, never heap contents (ARCHITECTURE.md 12).
/// </summary>
/// <remarks>
/// Stacks are a tree of nodes shared by the samples: node <c>n</c> is frame <see cref="StackFrames"/>[n] called from
/// node <see cref="StackParents"/>[n] (-1 at the outermost frame), and a parent always comes before its children. A
/// sample points at its innermost node, the frame that allocated; a sample the trace recorded no stack for points at a
/// node of its own (<see cref="NoStackFrame"/>), so every view adds up to the same bytes. Samples are in time order. The
/// constructor checks all three rules: the queries rely on them (the 2.5 review).
/// </remarks>
public sealed class AllocationTrace
{
    public AllocationTrace(
        IReadOnlyList<string> frames,
        IReadOnlyList<string> types,
        int[] stackFrames,
        int[] stackParents,
        double[] sampleTimes,
        int[] sampleStacks,
        int[] sampleTypes,
        long[] sampleBytes,
        AllocationKind[] sampleKinds,
        AllocationTraceInfo info)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(stackFrames);
        ArgumentNullException.ThrowIfNull(stackParents);
        ArgumentNullException.ThrowIfNull(sampleTimes);
        ArgumentNullException.ThrowIfNull(sampleStacks);
        ArgumentNullException.ThrowIfNull(sampleTypes);
        ArgumentNullException.ThrowIfNull(sampleBytes);
        ArgumentNullException.ThrowIfNull(sampleKinds);
        ArgumentNullException.ThrowIfNull(info);
        if (stackFrames.Length != stackParents.Length)
        {
            throw new ArgumentException("Every stack node needs a frame and a parent.", nameof(stackParents));
        }

        var count = sampleTimes.Length;
        if (sampleStacks.Length != count || sampleTypes.Length != count || sampleBytes.Length != count || sampleKinds.Length != count)
        {
            throw new ArgumentException("Every sample needs a time, a stack, a type, a kind and its bytes.", nameof(sampleTimes));
        }

        for (var n = 0; n < stackParents.Length; n++)
        {
            if (stackParents[n] < -1 || stackParents[n] >= n || (uint)stackFrames[n] >= (uint)frames.Count)
            {
                throw new ArgumentException("A stack node's parent must come before it, and its frame must exist.", nameof(stackParents));
            }
        }

        for (var i = 0; i < count; i++)
        {
            if ((i > 0 && sampleTimes[i] < sampleTimes[i - 1]) || (uint)sampleStacks[i] >= (uint)stackFrames.Length || (uint)sampleTypes[i] >= (uint)types.Count)
            {
                throw new ArgumentException("Samples must be in time order, each with an existing stack node and type.", nameof(sampleTimes));
            }
        }

        Frames = frames;
        Types = types;
        StackFrames = stackFrames;
        StackParents = stackParents;
        SampleTimes = sampleTimes;
        SampleStacks = sampleStacks;
        SampleTypes = sampleTypes;
        SampleBytes = sampleBytes;
        SampleKinds = sampleKinds;
        Info = info;
    }

    /// <summary>Frame names by frame id: a method's full name, <see cref="NoStackFrame"/>, or an unresolved frame's label.</summary>
    public IReadOnlyList<string> Frames { get; }

    /// <summary>Type names by type id (types are told apart by name, ADR-028).</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>The frame of each stack node.</summary>
    public int[] StackFrames { get; }

    /// <summary>The caller node of each stack node, or -1 for the outermost frame; always lower than the node.</summary>
    public int[] StackParents { get; }

    /// <summary>Each sample's time in milliseconds since the trace started, ascending.</summary>
    public double[] SampleTimes { get; }

    /// <summary>Each sample's innermost stack node.</summary>
    public int[] SampleStacks { get; }

    /// <summary>Each sample's type id.</summary>
    public int[] SampleTypes { get; }

    /// <summary>
    /// The bytes each sample stands for: those allocated on its heap and kind since the previous sample there, placed at
    /// this sample's time.
    /// </summary>
    public long[] SampleBytes { get; }

    /// <summary>The heap each sample counted bytes for.</summary>
    public AllocationKind[] SampleKinds { get; }

    public AllocationTraceInfo Info { get; }

    public int SampleCount => SampleTimes.Length;

    /// <summary>
    /// The label of a frame the trace does not name. EventPipe walks managed frames only, so this is a managed method
    /// whose name is missing, usually because recording ended before the rundown (<see cref="AllocationTraceInfo.HasRundown"/>).
    /// </summary>
    public static string UnresolvedFrame(string? module) =>
        string.IsNullOrEmpty(module) ? "[unresolved frame]" : "[unresolved frame in " + module + "]";

    /// <summary>The frame of samples the trace recorded no stack for (counted in <see cref="AllocationTraceInfo.SamplesWithoutStack"/>).</summary>
    public const string NoStackFrame = "[no stack recorded]";

    /// <summary>Bytes kept per sample (time, stack, type, bytes, kind) for memory estimates.</summary>
    public const int BytesPerSample = 25;
}

/// <summary>What a trace says about itself, and what reading it found.</summary>
/// <param name="ProcessId">The traced process (from its first allocation tick), or 0 when the trace has none.</param>
/// <param name="ProcessName">The traced process's name, when known.</param>
/// <param name="StartUtc">When recording started.</param>
/// <param name="DurationMs">How long it recorded.</param>
/// <param name="LostEvents">
/// Events the runtime dropped (its buffer was full), of any kind: an upper bound on lost samples. A lost sample's bytes
/// are lost with it, so the figures undercount.
/// </param>
/// <param name="SamplesWithoutStack">Samples whose stack the trace did not record.</param>
/// <param name="SamplesWithUnresolvedFrames">Samples whose stack has at least one frame the trace does not name.</param>
/// <param name="HasRundown">Whether the trace ends with the runtime's rundown, which names every method still loaded.</param>
/// <param name="ConversionIssues">Problems TraceEvent reported while reading, such as a stream cut short.</param>
/// <param name="FirstIssues">The first of those problems, for the log (TraceEvent's own words; no event payloads).</param>
public sealed record AllocationTraceInfo(
    int ProcessId,
    string? ProcessName,
    DateTime StartUtc,
    double DurationMs,
    long LostEvents,
    long SamplesWithoutStack,
    long SamplesWithUnresolvedFrames = 0,
    bool HasRundown = true,
    int ConversionIssues = 0,
    IReadOnlyList<string>? FirstIssues = null)
{
    /// <summary>True when the trace may be cut short or misses names: the figures should be read with care.</summary>
    public bool IsIncomplete => !HasRundown || ConversionIssues > 0;
}
