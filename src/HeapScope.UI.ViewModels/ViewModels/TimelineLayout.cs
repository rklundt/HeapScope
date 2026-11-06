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
using HeapScope.Application.Monitoring;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// The Live page chart's geometry (sprint 1.7), independent of the UI toolkit that draws it (sprint 2.3.6): the GC heap
/// stacked by heap kind as of the last GC, and on top what was allocated since, over the samples' time span.
/// </summary>
public static class TimelineLayout
{
    /// <summary>
    /// Theme brush keys of the layers, bottom to top: the heap kinds as of the last GC, then what was allocated since
    /// (gray), so the top edge is the heap now (review of 1.7).
    /// </summary>
    public static readonly string[] LayerKeys = ["HeapKind.Gen0", "HeapKind.Gen1", "HeapKind.Gen2", "HeapKind.LargeObjectHeap", "HeapKind.PinnedObjectHeap", "HeapKind.Unknown"];

    /// <summary>The height under the plot that holds the time axis's labels.</summary>
    public const double TimeAxisHeight = 16;

    /// <summary>The largest number of byte gridlines; a chart with more is noise.</summary>
    public const int MaxByteTicks = 4;

    /// <summary>
    /// Round sizes for the gridlines, up to <paramref name="maxBytes"/>: at most <see cref="MaxByteTicks"/> of them, each a
    /// 1, 2 or 5 times a power of ten, in megabytes.
    /// </summary>
    public static IReadOnlyList<long> ByteTicks(long maxBytes)
    {
        const long Megabyte = 1024 * 1024;
        if (maxBytes <= 0)
        {
            return [];
        }

        for (var magnitude = 1L; ; magnitude *= 10)
        {
            foreach (var factor in new long[] { 1, 2, 5 })
            {
                var step = factor * magnitude * Megabyte;
                if (maxBytes / step <= MaxByteTicks)
                {
                    return [.. Enumerable.Range(1, (int)(maxBytes / step)).Select(i => i * step)];
                }
            }
        }
    }

    /// <summary>
    /// The labels of the time axis: the time since <paramref name="runStart"/> (the first sample without one) at round steps, at
    /// most about six across. Only the last samples are kept, so the first in the window may be well into the run.
    /// </summary>
    public static IReadOnlyList<(double X, string Label)> TimeTicks(IReadOnlyList<HeapSample> samples, double width, DateTimeOffset? runStart = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2 || width <= 0)
        {
            return [];
        }

        var span = (samples[^1].At - samples[0].At).TotalSeconds;
        if (span <= 0)
        {
            return [];
        }

        int[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800, 86400];
        var step = steps.FirstOrDefault(s => span / s <= 6, steps[^1]);
        var offset = runStart is { } start ? Math.Max(0, (samples[0].At - start).TotalSeconds) : 0;
        var ticks = new List<(double X, string Label)>();
        for (var at = (long)Math.Ceiling(offset / step) * step; at <= offset + span; at += step)
        {
            var time = TimeSpan.FromSeconds(at);
            var label = time.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
            ticks.Add(((at - offset) / span * width, label));
        }

        return ticks;
    }

    /// <summary>
    /// The top edge of each layer, bottom layer first: x spans the samples' time (left to right), y the stacked bytes
    /// (0 at the bottom, <paramref name="maxBytes"/> at the top).
    /// </summary>
    public static IReadOnlyList<ChartPoint[]> Layers(IReadOnlyList<HeapSample> samples, double width, double height, out long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var layers = new ChartPoint[LayerKeys.Length][];
        for (var k = 0; k < layers.Length; k++)
        {
            layers[k] = new ChartPoint[samples.Count];
        }

        maxBytes = 1;
        foreach (var s in samples)
        {
            maxBytes = Math.Max(maxBytes, Stack(s)[^1]);
        }

        if (samples.Count == 0)
        {
            return layers;
        }

        var start = samples[0].At;
        var span = Math.Max(1, (samples[^1].At - start).TotalSeconds);
        for (var i = 0; i < samples.Count; i++)
        {
            var x = samples.Count == 1 ? width : (samples[i].At - start).TotalSeconds / span * width;
            var stack = Stack(samples[i]);
            for (var k = 0; k < layers.Length; k++)
            {
                layers[k][i] = new ChartPoint(x, height - (stack[k] / (double)maxBytes * height));
            }
        }

        return layers;
    }

    /// <summary>The x of a moment in the samples' span, or null when it falls outside.</summary>
    public static double? XOf(IReadOnlyList<HeapSample> samples, DateTimeOffset at, double width)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2 || at < samples[0].At || at > samples[^1].At)
        {
            return null;
        }

        return (at - samples[0].At).TotalSeconds / (samples[^1].At - samples[0].At).TotalSeconds * width;
    }

    /// <summary>Cumulative bytes per layer (gen0, +gen1, +gen2, +LOH, +POH, +allocated since the last GC).</summary>
    private static long[] Stack(HeapSample s)
    {
        var stack = new long[6];
        stack[0] = s.Gen0Bytes;
        stack[1] = stack[0] + s.Gen1Bytes;
        stack[2] = stack[1] + s.Gen2Bytes;
        stack[3] = stack[2] + s.LargeObjectHeapBytes;
        stack[4] = stack[3] + s.PinnedObjectHeapBytes;
        stack[5] = stack[4] + s.AllocatedSinceGcBytes;
        return stack;
    }
}
