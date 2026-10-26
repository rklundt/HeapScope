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

/// <summary>
/// The icicle chart's geometry (sprint 2.4), independent of the UI toolkit: each bar's rectangle, the bar under a point,
/// and the bars on the path from the top to a selected one.
/// </summary>
public static class IcicleGeometry
{
    /// <summary>Bars narrower than this many pixels are not drawn (and cannot be hit).</summary>
    public const double MinWidth = 1;

    /// <summary>The rectangle of each bar that is at least <see cref="MinWidth"/> wide, in level order.</summary>
    public static IReadOnlyList<IcicleBar> Bars(IReadOnlyList<IcicleSegmentDto> segments, long total, double width, double rowHeight)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var bars = new List<IcicleBar>();
        if (total <= 0 || width <= 0)
        {
            return bars;
        }

        var scale = width / total;
        foreach (var segment in segments)
        {
            var w = segment.Width * scale;
            if (w >= MinWidth)
            {
                bars.Add(new IcicleBar(segment, segment.Offset * scale, segment.Level * rowHeight, w, rowHeight));
            }
        }

        return bars;
    }

    /// <summary>The height that shows every level.</summary>
    public static double Height(IReadOnlyList<IcicleSegmentDto>? segments, double rowHeight) =>
        segments is { Count: > 0 } ? (segments.Max(s => s.Level) + 1) * rowHeight : 0;

    /// <summary>The bar under (<paramref name="x"/>, <paramref name="y"/>), if any.</summary>
    public static IcicleSegmentDto? HitTest(IReadOnlyList<IcicleBar> bars, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(bars);
        foreach (var bar in bars)
        {
            if (x >= bar.X && x < bar.X + bar.Width && y >= bar.Y && y < bar.Y + bar.Height)
            {
                return bar.Segment;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="segment"/> is on the path from the top to <paramref name="selected"/> (itself included): a
    /// bar at a level above it whose span holds its left edge. Children always lie inside their parent, so the spans
    /// nest.
    /// </summary>
    public static bool IsOnPath(IcicleSegmentDto segment, IcicleSegmentDto? selected)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return selected is not null && segment.Level <= selected.Level
            && segment.Offset <= selected.Offset && selected.Offset < segment.Offset + Math.Max(1, segment.Width);
    }
}

/// <summary>A drawn bar of the icicle chart: its segment and its rectangle in pixels.</summary>
public sealed record IcicleBar(IcicleSegmentDto Segment, double X, double Y, double Width, double Height);
