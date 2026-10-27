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

using HeapScope.Core.Allocations;

namespace HeapScope.Analysis.Allocations;

/// <summary>Which samples a question is about: an interval of the trace and, optionally, one type.</summary>
/// <param name="FromMs">Start of the interval in milliseconds since the trace started (inclusive).</param>
/// <param name="ToMs">End of the interval (exclusive); <see cref="double.PositiveInfinity"/> for the end of the trace.</param>
/// <param name="Type">A type id, or -1 for every type.</param>
public readonly record struct AllocationFilter(double FromMs, double ToMs, int Type = -1)
{
    /// <summary>Every sample of the trace.</summary>
    public static AllocationFilter All { get; } = new(0, double.PositiveInfinity);
}

/// <summary>Sampled bytes in one stretch of the timeline.</summary>
public readonly record struct AllocationBucket(double StartMs, long Bytes, int Samples);

/// <summary>Sampled bytes of one type.</summary>
public readonly record struct AllocationTypeTotal(int Type, long Bytes, int Samples);

/// <summary>Sampled bytes of one call stack (its innermost node).</summary>
public readonly record struct AllocationStackTotal(int Node, long Bytes, int Samples);

/// <summary>
/// Questions over a trace's sampled allocations (sprint 2.5): the timeline, the types and the call stacks of an
/// interval. Sizes are what the samples stand for (about 100 KB each), so they are estimates.
/// </summary>
public static class AllocationProfile
{
    /// <summary>The samples of <paramref name="filter"/>'s interval, as a half-open range of sample indices.</summary>
    public static (int Start, int End) SampleRange(AllocationTrace trace, AllocationFilter filter)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var times = trace.SampleTimes;
        return (LowerBound(times, filter.FromMs), double.IsPositiveInfinity(filter.ToMs) ? times.Length : LowerBound(times, filter.ToMs));
    }

    /// <summary>Sampled bytes per <paramref name="bucketMs"/> from the start of the trace to its end.</summary>
    public static AllocationBucket[] Timeline(AllocationTrace trace, double bucketMs)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucketMs, 0);
        var end = Math.Max(trace.Info.DurationMs, trace.SampleCount > 0 ? trace.SampleTimes[^1] : 0);
        var count = Math.Max(1, (int)Math.Ceiling(end / bucketMs));
        var bytes = new long[count];
        var samples = new int[count];
        for (var i = 0; i < trace.SampleCount; i++)
        {
            var bucket = Math.Clamp((int)(trace.SampleTimes[i] / bucketMs), 0, count - 1);
            bytes[bucket] += trace.SampleBytes[i];
            samples[bucket]++;
        }

        var buckets = new AllocationBucket[count];
        for (var b = 0; b < count; b++)
        {
            buckets[b] = new AllocationBucket(b * bucketMs, bytes[b], samples[b]);
        }

        return buckets;
    }

    /// <summary>The sampled bytes and samples of <paramref name="filter"/>.</summary>
    public static (long Bytes, int Samples) Totals(AllocationTrace trace, AllocationFilter filter)
    {
        var (start, end) = SampleRange(trace, filter);
        long bytes = 0;
        var samples = 0;
        for (var i = start; i < end; i++)
        {
            if (filter.Type < 0 || trace.SampleTypes[i] == filter.Type)
            {
                bytes += trace.SampleBytes[i];
                samples++;
            }
        }

        return (bytes, samples);
    }

    /// <summary>The types allocated in the interval (the filter's type is ignored), most bytes first, then by name.</summary>
    public static AllocationTypeTotal[] Types(AllocationTrace trace, AllocationFilter filter)
    {
        var (start, end) = SampleRange(trace, filter);
        var bytes = new long[trace.Types.Count];
        var samples = new int[trace.Types.Count];
        for (var i = start; i < end; i++)
        {
            bytes[trace.SampleTypes[i]] += trace.SampleBytes[i];
            samples[trace.SampleTypes[i]]++;
        }

        return [.. Enumerable.Range(0, trace.Types.Count)
            .Where(t => samples[t] > 0)
            .Select(t => new AllocationTypeTotal(t, bytes[t], samples[t]))
            .OrderByDescending(t => t.Bytes)
            .ThenBy(t => trace.Types[t.Type], StringComparer.Ordinal)];
    }

    /// <summary>The <paramref name="count"/> call stacks with the most sampled bytes, then the most samples, then by node.</summary>
    public static AllocationStackTotal[] TopStacks(AllocationTrace trace, AllocationFilter filter, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var (bytes, samples) = Own(trace, filter);
        return [.. Enumerable.Range(0, bytes.Length)
            .Where(n => samples[n] > 0)
            .Select(n => new AllocationStackTotal(n, bytes[n], samples[n]))
            .OrderByDescending(s => s.Bytes)
            .ThenByDescending(s => s.Samples)
            .ThenBy(s => s.Node)
            .Take(count)];
    }

    /// <summary>The frames of a stack node, innermost (the allocating method) first.</summary>
    public static int[] FramesOf(AllocationTrace trace, int node)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var frames = new List<int>();
        for (var at = node; at >= 0; at = trace.StackParents[at])
        {
            frames.Add(trace.StackFrames[at]);
        }

        return [.. frames];
    }

    /// <summary>Sampled bytes and samples per stack node from samples whose innermost frame it is.</summary>
    internal static (long[] Bytes, int[] Samples) Own(AllocationTrace trace, AllocationFilter filter)
    {
        var (start, end) = SampleRange(trace, filter);
        var bytes = new long[trace.StackFrames.Length];
        var samples = new int[trace.StackFrames.Length];
        for (var i = start; i < end; i++)
        {
            var node = trace.SampleStacks[i];
            if (node >= 0 && (filter.Type < 0 || trace.SampleTypes[i] == filter.Type))
            {
                bytes[node] += trace.SampleBytes[i];
                samples[node]++;
            }
        }

        return (bytes, samples);
    }

    private static int LowerBound(double[] times, double value)
    {
        int low = 0, high = times.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (times[mid] < value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
