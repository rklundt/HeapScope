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

/// <summary>Sampled bytes of one type, with the part counted on the large and pinned object heaps.</summary>
public readonly record struct AllocationTypeTotal(long Bytes, long LargeBytes, long PinnedBytes, int Type, int Samples);

/// <summary>Sampled bytes of one call stack (its innermost node).</summary>
public readonly record struct AllocationStackTotal(long Bytes, int Node, int Samples);

/// <summary>
/// Questions over a trace's sampled allocations (sprint 2.5): the timeline, the types and the call stacks of an
/// interval. Sizes are what the samples stand for (about 100 KB each per heap and kind), so they are estimates. The
/// loops over samples check the cancellation token every 64K samples (ARCHITECTURE.md 4.5).
/// </summary>
public static class AllocationProfile
{
    private const int CheckEvery = 0xFFFF;

    /// <summary>The samples of <paramref name="filter"/>'s interval, as a half-open range of sample indices.</summary>
    public static (int Start, int End) SampleRange(AllocationTrace trace, AllocationFilter filter)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var times = trace.SampleTimes;
        return (LowerBound(times, filter.FromMs), double.IsPositiveInfinity(filter.ToMs) ? times.Length : LowerBound(times, filter.ToMs));
    }

    /// <summary>Sampled bytes per <paramref name="bucketMs"/> from the start of the trace to its end.</summary>
    public static AllocationBucket[] Timeline(AllocationTrace trace, double bucketMs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucketMs, 0);
        var count = BucketCount(trace, bucketMs);
        var bytes = new long[count];
        var samples = new int[count];
        for (var i = 0; i < trace.SampleCount; i++)
        {
            var bucket = Math.Clamp((int)(trace.SampleTimes[i] / bucketMs), 0, count - 1);
            bytes[bucket] += trace.SampleBytes[i];
            samples[bucket]++;
            Check(i, cancellationToken);
        }

        var buckets = new AllocationBucket[count];
        for (var b = 0; b < count; b++)
        {
            buckets[b] = new AllocationBucket(b * bucketMs, bytes[b], samples[b]);
        }

        return buckets;
    }

    /// <summary>How many stretches of <paramref name="bucketMs"/> the timeline has.</summary>
    public static int BucketCount(AllocationTrace trace, double bucketMs)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var end = Math.Max(trace.Info.DurationMs, trace.SampleCount > 0 ? trace.SampleTimes[^1] : 0);
        return (int)Math.Clamp(Math.Ceiling(end / bucketMs), 1, int.MaxValue);
    }

    /// <summary>The sampled bytes and samples of <paramref name="filter"/>.</summary>
    public static (long Bytes, int Samples) Totals(AllocationTrace trace, AllocationFilter filter, CancellationToken cancellationToken = default)
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

            Check(i, cancellationToken);
        }

        return (bytes, samples);
    }

    /// <summary>The types allocated in the interval (the filter's type is ignored), most bytes first, then by name.</summary>
    public static AllocationTypeTotal[] Types(AllocationTrace trace, AllocationFilter filter, CancellationToken cancellationToken = default)
    {
        var (start, end) = SampleRange(trace, filter);
        var count = trace.Types.Count;
        var bytes = new long[count];
        var large = new long[count];
        var pinned = new long[count];
        var samples = new int[count];
        for (var i = start; i < end; i++)
        {
            var type = trace.SampleTypes[i];
            var amount = trace.SampleBytes[i];
            bytes[type] += amount;
            samples[type]++;
            switch (trace.SampleKinds[i])
            {
                case AllocationKind.Large:
                    large[type] += amount;
                    break;
                case AllocationKind.Pinned:
                    pinned[type] += amount;
                    break;
            }

            Check(i, cancellationToken);
        }

        var rows = new List<AllocationTypeTotal>();
        for (var t = 0; t < count; t++)
        {
            if (samples[t] > 0)
            {
                rows.Add(new AllocationTypeTotal(bytes[t], large[t], pinned[t], t, samples[t]));
            }
        }

        rows.Sort((a, b) => b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes) : string.CompareOrdinal(trace.Types[a.Type], trace.Types[b.Type]));
        return [.. rows];
    }

    /// <summary>The <paramref name="count"/> call stacks with the most sampled bytes, then the most samples, then by node.</summary>
    public static AllocationStackTotal[] TopStacks(AllocationTrace trace, AllocationFilter filter, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var nodes = trace.StackFrames.Length;
        var bytes = new long[nodes];
        var samples = new int[nodes];
        Own(trace, filter, bytes, samples, cancellationToken);
        var rows = new List<AllocationStackTotal>();
        for (var n = 0; n < nodes; n++)
        {
            if (samples[n] > 0)
            {
                rows.Add(new AllocationStackTotal(bytes[n], n, samples[n]));
            }
        }

        rows.Sort((a, b) => b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes) : b.Samples != a.Samples ? b.Samples.CompareTo(a.Samples) : a.Node.CompareTo(b.Node));
        return [.. rows.Take(count)];
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

    /// <summary>
    /// Adds the sampled bytes and samples of <paramref name="filter"/> to their innermost stack node, into
    /// <paramref name="bytes"/> and <paramref name="samples"/> (at least one per node, cleared by the caller).
    /// </summary>
    internal static void Own(AllocationTrace trace, AllocationFilter filter, Span<long> bytes, Span<int> samples, CancellationToken cancellationToken)
    {
        var (start, end) = SampleRange(trace, filter);
        for (var i = start; i < end; i++)
        {
            if (filter.Type < 0 || trace.SampleTypes[i] == filter.Type)
            {
                var node = trace.SampleStacks[i];
                bytes[node] += trace.SampleBytes[i];
                samples[node]++;
            }

            Check(i, cancellationToken);
        }
    }

    private static void Check(int i, CancellationToken cancellationToken)
    {
        if ((i & CheckEvery) == CheckEvery)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
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
