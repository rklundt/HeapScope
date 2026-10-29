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

using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels;

/// <summary>A bar of the allocations timeline, in pixels.</summary>
public readonly record struct TimelineBar(double X, double Y, double Width, double Height, AllocationBucketDto Bucket);

/// <summary>
/// Where the allocations timeline draws its bars and how a pointer maps to seconds (sprint 2.5), apart from the control
/// so it is tested without a window. The trace's duration spans the width; a bar's height is its share of the largest.
/// </summary>
public static class AllocationTimelineGeometry
{
    /// <summary>The bars for <paramref name="buckets"/> across <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static IReadOnlyList<TimelineBar> Bars(IReadOnlyList<AllocationBucketDto>? buckets, double bucketSeconds, double durationSeconds, double width, double height)
    {
        if (buckets is null || buckets.Count == 0 || width <= 0 || height <= 0)
        {
            return [];
        }

        var span = Span(buckets, bucketSeconds, durationSeconds);
        var largest = buckets.Max(b => b.Bytes);
        var bars = new List<TimelineBar>(buckets.Count);
        foreach (var bucket in buckets)
        {
            var x = bucket.StartSeconds / span * width;
            var barWidth = Math.Max(1, (bucketSeconds / span * width) - 1);
            var barHeight = largest == 0 ? 0 : Math.Max(bucket.Bytes > 0 ? 1 : 0, (double)bucket.Bytes / largest * height);
            bars.Add(new TimelineBar(x, height - barHeight, barWidth, barHeight, bucket));
        }

        return bars;
    }

    /// <summary>The seconds at <paramref name="x"/>, clamped to the trace.</summary>
    public static double SecondsAt(double x, double width, IReadOnlyList<AllocationBucketDto>? buckets, double bucketSeconds, double durationSeconds)
    {
        if (width <= 0)
        {
            return 0;
        }

        var span = Span(buckets, bucketSeconds, durationSeconds);
        return Math.Clamp(x / width * span, 0, span);
    }

    /// <summary>The x of <paramref name="seconds"/>.</summary>
    public static double XOf(double seconds, double width, IReadOnlyList<AllocationBucketDto>? buckets, double bucketSeconds, double durationSeconds) =>
        width <= 0 ? 0 : seconds / Span(buckets, bucketSeconds, durationSeconds) * width;

    // The time the width stands for: the trace, or its last bucket's end when that is later.
    private static double Span(IReadOnlyList<AllocationBucketDto>? buckets, double bucketSeconds, double durationSeconds)
    {
        var end = buckets is { Count: > 0 } ? buckets[^1].StartSeconds + bucketSeconds : 0;
        return Math.Max(Math.Max(durationSeconds, end), 0.001);
    }
}
