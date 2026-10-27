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

/// <summary>
/// Sampled allocations read from an EventPipe trace (sprint 2.5, ADR-028): one sample about every 100 KB allocated,
/// each with its type, the bytes it stands for and its call stack. Holds type and method names, times and sizes only,
/// never heap contents (ARCHITECTURE.md 12).
/// </summary>
/// <remarks>
/// Stacks are a tree of nodes shared by the samples: node <c>n</c> is frame <see cref="StackFrames"/>[n] called from
/// node <see cref="StackParents"/>[n] (-1 at the outermost frame). A sample points at its innermost node, the frame that
/// allocated, or -1 when the trace recorded no stack for it. Samples are in time order.
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
        ArgumentNullException.ThrowIfNull(info);
        if (stackFrames.Length != stackParents.Length)
        {
            throw new ArgumentException("Every stack node needs a frame and a parent.", nameof(stackParents));
        }

        if (sampleStacks.Length != sampleTimes.Length || sampleTypes.Length != sampleTimes.Length || sampleBytes.Length != sampleTimes.Length)
        {
            throw new ArgumentException("Every sample needs a time, a stack, a type and its bytes.", nameof(sampleTimes));
        }

        Frames = frames;
        Types = types;
        StackFrames = stackFrames;
        StackParents = stackParents;
        SampleTimes = sampleTimes;
        SampleStacks = sampleStacks;
        SampleTypes = sampleTypes;
        SampleBytes = sampleBytes;
        Info = info;
    }

    /// <summary>Frame names by frame id: a method's full name, or <see cref="NativeFrame"/>.</summary>
    public IReadOnlyList<string> Frames { get; }

    /// <summary>Type names by type id.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>The frame of each stack node.</summary>
    public int[] StackFrames { get; }

    /// <summary>The caller node of each stack node, or -1 for the outermost frame.</summary>
    public int[] StackParents { get; }

    /// <summary>Each sample's time in milliseconds since the trace started, ascending.</summary>
    public double[] SampleTimes { get; }

    /// <summary>Each sample's innermost stack node, or -1 without a stack.</summary>
    public int[] SampleStacks { get; }

    /// <summary>Each sample's type id.</summary>
    public int[] SampleTypes { get; }

    /// <summary>The bytes each sample stands for: those allocated since the previous sample on its heap.</summary>
    public long[] SampleBytes { get; }

    public AllocationTraceInfo Info { get; }

    public int SampleCount => SampleTimes.Length;

    /// <summary>The name given to a run of frames without a managed method (native code; no symbols, ADR-017).</summary>
    public const string NativeFrame = "[native code]";

    /// <summary>The frame of samples the trace recorded no stack for (counted in <see cref="AllocationTraceInfo.SamplesWithoutStack"/>).</summary>
    public const string NoStackFrame = "[no stack recorded]";

    /// <summary>Bytes kept per sample (time, stack, type, bytes) for memory estimates.</summary>
    public const int BytesPerSample = 24;
}

/// <summary>What a trace says about itself.</summary>
/// <param name="ProcessId">The traced process, or 0 when the trace does not say.</param>
/// <param name="ProcessName">The traced process's name, when known.</param>
/// <param name="StartUtc">When recording started.</param>
/// <param name="DurationMs">How long it recorded.</param>
/// <param name="LostEvents">Events the runtime dropped (the buffer was full): the samples undercount by about that many.</param>
/// <param name="SamplesWithoutStack">Samples whose stack the trace did not record.</param>
public sealed record AllocationTraceInfo(int ProcessId, string? ProcessName, DateTime StartUtc, double DurationMs, long LostEvents, long SamplesWithoutStack);
