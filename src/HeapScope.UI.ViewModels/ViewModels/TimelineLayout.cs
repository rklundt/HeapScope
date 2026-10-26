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
