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
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// The icicle chart's geometry (sprint 2.4), independent of the UI toolkit: each bar's rectangle, the bar under a point,
/// and the bars on the path from the top to a selected one.
/// </summary>
public static class IcicleGeometry
{
    /// <summary>A bar narrower than this many pixels is not drawn on its own (and cannot be hit).</summary>
    public const double MinWidth = 1;

    /// <summary>Neighbors with one parent narrower than this many pixels are one "+N smaller" bar: too thin to point at, or to read.</summary>
    public const double GroupBelow = 6;

    /// <summary>
    /// The rectangle of each bar, in level order: each bar at least <see cref="MinWidth"/> wide, except that two or more neighbors under
    /// <see cref="GroupBelow"/> with one parent are one group, which hides what lies below them (3.10 review M44). Here, not in the
    /// worker: how narrow a bar is depends on the window.
    /// </summary>
    public static IReadOnlyList<IcicleBar> Bars(IReadOnlyList<IcicleSegmentDto> segments, long total, double width, double rowHeight)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var bars = new List<IcicleBar>();
        if (total <= 0 || width <= 0)
        {
            return bars;
        }

        var scale = width / total;
        // One walk along each level, in step with the level above and the groups so far: a chart can hold 20,000 segments, and this runs
        // on every draw. Both lists are sorted by offset, and spans at one level do not overlap.
        var groups = new List<IcicleSegmentDto>();
        var row = new List<IcicleSegmentDto>();
        var parents = new List<IcicleSegmentDto>();
        foreach (var level in segments.GroupBy(static s => s.Level).OrderBy(static l => l.Key))
        {
            row.Clear();
            var nextGroup = 0;
            foreach (var segment in level.OrderBy(static s => s.Offset))
            {
                if (Find(groups, segment, ref nextGroup) is null)
                {
                    row.Add(segment);
                }
            }

            var run = new List<IcicleSegmentDto>();
            var nextParent = 0;
            var runParent = -1;
            var added = new List<IcicleSegmentDto>();
            foreach (var segment in row)
            {
                var parent = Find(parents, segment, ref nextParent) is null ? -1 : nextParent;
                if (run.Count > 0 && (segment.Width * scale >= GroupBelow || parent != runParent))
                {
                    AddRun(run, bars, added, scale, rowHeight);
                }

                runParent = parent;
                if (segment.Width * scale < GroupBelow)
                {
                    run.Add(segment);
                }
                else
                {
                    bars.Add(BarOf(segment, 1, scale, rowHeight));
                }
            }

            AddRun(run, bars, added, scale, rowHeight);
            if (added.Count > 0)
            {
                groups.AddRange(added);
                groups.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
            }

            (parents, row) = (row, parents);
        }

        return bars;
    }

    /// <summary>The span in the sorted <paramref name="spans"/> that holds the segment; segments are asked about in offset order.</summary>
    private static IcicleSegmentDto? Find(List<IcicleSegmentDto> spans, IcicleSegmentDto segment, ref int next)
    {
        while (next < spans.Count && spans[next].Offset + Math.Max(1, spans[next].Width) <= segment.Offset)
        {
            next++;
        }

        return next < spans.Count && spans[next].Offset <= segment.Offset ? spans[next] : null;
    }

    private static IcicleBar BarOf(IcicleSegmentDto segment, int count, double scale, double rowHeight) =>
        new(segment, segment.Offset * scale, segment.Level * rowHeight, segment.Width * scale, rowHeight, count);

    private static void AddRun(List<IcicleSegmentDto> run, List<IcicleBar> bars, List<IcicleSegmentDto> grouped, double scale, double rowHeight)
    {
        if (run.Count == 1 && run[0].Width * scale >= MinWidth)
        {
            bars.Add(BarOf(run[0], 1, scale, rowHeight));
        }
        else if (run.Count > 1)
        {
            var first = run[0];
            var last = run[^1];
            var group = new IcicleSegmentDto(
                first.Level,
                first.Offset,
                last.Offset + last.Width - first.Offset,
                string.Create(CultureInfo.InvariantCulture, $"group:{first.Level}:{first.Offset}"),
                string.Create(CultureInfo.CurrentCulture, $"+{run.Count:N0} smaller"),
                0);
            grouped.Add(group);
            bars.Add(BarOf(group, run.Count, scale, rowHeight));
        }

        run.Clear();
    }

    /// <summary>Where a group's bars are, whatever the tree's grouping.</summary>
    private const string TreeHasThem = "The tree below has them (expand a group or a \"more\" row if they are not shown).";

    /// <summary>What a bar stands for, in words: most bars are too narrow to carry a label.</summary>
    public static string Describe(IcicleBar bar, long total)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var segment = bar.Segment;
        var share = total > 0 ? (double)segment.Width / total : 0;
        if (!bar.IsGroup)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{segment.Name}\nKeeps {Format.Bytes(segment.Width)} alive, {share:P1} of this chart");
        }

        // Level 1 sits under the chart's top bar, the focus itself: a double-click there shows the same chart (3.11 review), so
        // the tree below is where they are. Deeper, the bar above is their parent, and focusing it puts them in the tree. Under
        // Group children by Type or Namespace the tree lists groups, and past 100 rows a "more" row: the text holds for both.
        // A group's width adds up its members' retained sizes; what they keep alive together is counted in the bar above.
        var where = segment.Level <= 1
            ? TreeHasThem
            : "Double-click the bar above them to focus on it; " + char.ToLowerInvariant(TreeHasThem[0]) + TreeHasThem[1..];
        return string.Create(CultureInfo.CurrentCulture, $"{segment.Name}\nTogether keep at least {Format.Bytes(segment.Width)} alive, {share:P1} of this chart\n{where}");
    }

    /// <summary>What a bar this wide can show, or null when it has no room for words: a group's count must not be cut to "+12 ...".</summary>
    public static string? Label(IcicleBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var full = string.Create(CultureInfo.CurrentCulture, $"{bar.Segment.Name} ({Format.Bytes(bar.Segment.Width)})");
        return bar switch
        {
            { IsGroup: true, Width: >= 110 } => full,
            { IsGroup: true, Width: >= 24 } => string.Create(CultureInfo.CurrentCulture, $"+{bar.Count:N0}"),
            { IsGroup: false, Width: >= 40 } => full,
            _ => null,
        };
    }

    /// <summary>The height that shows every level.</summary>
    public static double Height(IReadOnlyList<IcicleSegmentDto>? segments, double rowHeight) =>
        segments is { Count: > 0 } ? (segments.Max(s => s.Level) + 1) * rowHeight : 0;

    /// <summary>The bar under (<paramref name="x"/>, <paramref name="y"/>), if any.</summary>
    public static IcicleBar? BarAt(IReadOnlyList<IcicleBar> bars, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(bars);
        foreach (var bar in bars)
        {
            if (x >= bar.X && x < bar.X + bar.Width && y >= bar.Y && y < bar.Y + bar.Height)
            {
                return bar;
            }
        }

        return null;
    }

    /// <summary>The segment a click selects at a point: a group is no one object, so it selects nothing.</summary>
    public static IcicleSegmentDto? HitTest(IReadOnlyList<IcicleBar> bars, double x, double y) =>
        BarAt(bars, x, y) is { IsGroup: false } bar ? bar.Segment : null;

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
public sealed record IcicleBar(IcicleSegmentDto Segment, double X, double Y, double Width, double Height, int Count = 1)
{
    /// <summary>Whether this bar stands for several narrow segments.</summary>
    public bool IsGroup => Count > 1;
}
