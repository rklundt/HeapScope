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

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The icicle chart's geometry (sprint 2.4).</summary>
public sealed class IcicleGeometryTests
{
    private static readonly IcicleSegmentDto[] s_segments =
    [
        new(0, 0, 1000, string.Empty, "All reachable objects", 0),
        new(1, 0, 750, "o:10", "App.Cache", 0x10),
        new(1, 750, 249, "o:20", "App.Other", 0x20),
        new(1, 999, 1, "o:28", "App.Tiny", 0x28),
        new(2, 0, 500, "o:30", "App.Entry", 0x30),
    ];

    [Fact]
    public void Bars_are_as_wide_as_their_share_and_those_under_a_pixel_are_not_drawn()
    {
        var bars = IcicleGeometry.Bars(s_segments, 1000, width: 400, rowHeight: 20);

        bars.Select(b => (b.Segment.Key, b.X, b.Y, Math.Round(b.Width, 6))).ShouldBe(
        [
            (string.Empty, 0.0, 0.0, 400.0),
            ("o:10", 0.0, 20.0, 300.0),
            ("o:20", 300.0, 20.0, 99.6),
            ("o:30", 0.0, 40.0, 200.0),
        ]);
        IcicleGeometry.Height(s_segments, 20).ShouldBe(60);
        IcicleGeometry.Bars(s_segments, 0, 400, 20).ShouldBeEmpty();
    }

    [Fact]
    public void A_bar_describes_itself_in_words()
    {
        static IcicleBar Bar(IcicleSegmentDto segment, int count = 1) => new(segment, 0, 0, 1, 1, count);
        var text = IcicleGeometry.Describe(Bar(new IcicleSegmentDto(1, 0, 256 * 1024, "o:10", "App.Cache", 0x10)), 1024 * 1024);

        text.ShouldStartWith("App.Cache\nKeeps ");
        text.ShouldContain(Format.Bytes(256 * 1024));
        text.ShouldContain((0.25).ToString("P1", System.Globalization.CultureInfo.CurrentCulture));
        text.ShouldNotContain("Double-click");
        IcicleGeometry.Describe(Bar(new IcicleSegmentDto(0, 0, 0, string.Empty, "Nothing", 0)), 0).ShouldContain((0.0).ToString("P1", System.Globalization.CultureInfo.CurrentCulture));
        // Under the top bar, a double-click on it would show the same chart, so the text points at the tree (3.11 review).
        var top = IcicleGeometry.Describe(Bar(new IcicleSegmentDto(1, 0, 10, "g", "+2 smaller", 0), count: 2), 100);
        top.ShouldContain("Together keep at least");
        // Under Group children by Type the tree lists groups, and past 100 rows a "more" row: the text holds for both (3.11 review).
        top.ShouldEndWith("\nThe tree below has them (expand a group or a \"more\" row if they are not shown).");
        IcicleGeometry.Describe(Bar(new IcicleSegmentDto(2, 0, 10, "g", "+2 smaller", 0), count: 2), 100)
            .ShouldEndWith("\nDouble-click the bar above them to focus on it; the tree below has them (expand a group or a \"more\" row if they are not shown).");
    }

    /// <summary>A parent, one wide child, and five children of 1 percent: at 200 pixels each is 2 pixels, too thin to point at.</summary>
    private static readonly IcicleSegmentDto[] s_slivers =
    [
        new(0, 0, 1000, string.Empty, "All", 0),
        new(1, 0, 800, "o:1", "App.Wide", 1),
        new(1, 800, 10, "o:2", "App.A", 2),
        new(1, 810, 10, "o:3", "App.B", 3),
        new(1, 820, 10, "o:4", "App.C", 4),
        new(1, 830, 10, "o:5", "App.D", 5),
        new(1, 840, 10, "o:6", "App.E", 6),
        new(2, 800, 5, "o:7", "App.Under", 7),
        new(2, 0, 400, "o:8", "App.Child", 8),
    ];

    [Fact]
    public void Narrow_neighbors_become_one_bar_that_says_how_many_and_hides_what_is_below_them()
    {
        var bars = IcicleGeometry.Bars(s_slivers, 1000, width: 200, rowHeight: 20);

        var group = bars.Single(static b => b.IsGroup);
        (group.Count, group.Segment.Name, group.Segment.Level, group.Segment.Offset, group.Segment.Width).ShouldBe((5, "+5 smaller", 1, 800L, 50L));
        group.Width.ShouldBe(10, 0.001);
        bars.Select(static b => b.Segment.Key).ShouldNotContain("o:7", "what lies below a group is hidden with it");
        bars.Select(static b => b.Segment.Key).ShouldContain("o:8");
        bars.Where(static b => b.Segment.Level == 1).Sum(static b => b.Segment.Width).ShouldBe(850, "the row still adds up");
    }

    [Theory]
    [InlineData(20, null)]
    [InlineData(30, "+12")]
    [InlineData(109, "+12")]
    public void A_narrow_group_shows_its_count_or_nothing(double width, string? label)
    {
        var bar = new IcicleBar(new IcicleSegmentDto(1, 0, 100, "g", "+12 smaller", 0), 0, 0, width, 20, 12);

        IcicleGeometry.Label(bar).ShouldBe(label);
    }

    [Fact]
    public void A_wide_group_and_a_plain_bar_show_their_name_and_size()
    {
        var segment = new IcicleSegmentDto(1, 0, 100, "g", "+12 smaller", 0);

        IcicleGeometry.Label(new IcicleBar(segment, 0, 0, 110, 20, 12)).ShouldNotBeNull().ShouldStartWith("+12 smaller (");
        IcicleGeometry.Label(new IcicleBar(segment with { Name = "App.Cache" }, 0, 0, 40, 20)).ShouldNotBeNull().ShouldStartWith("App.Cache (");
        IcicleGeometry.Label(new IcicleBar(segment with { Name = "App.Cache" }, 0, 0, 39, 20)).ShouldBeNull();
    }

    [Fact]
    public void Given_room_the_same_bars_stand_alone()
    {
        var bars = IcicleGeometry.Bars(s_slivers, 1000, width: 2000, rowHeight: 20);

        bars.ShouldNotContain(static b => b.IsGroup);
        bars.Count.ShouldBe(s_slivers.Length);
    }

    [Fact]
    public void A_lone_narrow_bar_stays_itself_and_bars_of_two_parents_do_not_mix()
    {
        IcicleSegmentDto[] segments =
        [
            new(0, 0, 500, "p:1", "Left", 1),
            new(0, 500, 500, "p:2", "Right", 2),
            new(1, 490, 10, "o:1", "App.LastOfLeft", 3),
            new(1, 500, 10, "o:2", "App.FirstOfRight", 4),
            new(1, 600, 300, "o:3", "App.Wide", 5),
            new(1, 900, 10, "o:4", "App.Alone", 6),
        ];

        var bars = IcicleGeometry.Bars(segments, 1000, width: 200, rowHeight: 20);

        bars.ShouldNotContain(static b => b.IsGroup);
        bars.Select(static b => b.Segment.Key).ShouldContain("o:1");
        bars.Select(static b => b.Segment.Key).ShouldContain("o:2");
        bars.Select(static b => b.Segment.Key).ShouldContain("o:4");
    }

    [Fact]
    public void A_group_selects_nothing_but_has_something_to_say()
    {
        var bars = IcicleGeometry.Bars(s_slivers, 1000, width: 200, rowHeight: 20);

        IcicleGeometry.HitTest(bars, 165, 25).ShouldBeNull();
        IcicleGeometry.BarAt(bars, 165, 25).ShouldNotBeNull().IsGroup.ShouldBeTrue();
        IcicleGeometry.HitTest(bars, 50, 25).ShouldNotBeNull().Key.ShouldBe("o:1");
    }

    /// <summary>
    /// Groups at two levels, the older one (level 1) to the right of the newer one (level 2): level 3 is walked against both at
    /// once, so each segment is hidden only by the group above it (3.11 review M11). At 200 pixels for 1,000 bytes, a bar under
    /// 30 bytes is narrow.
    /// </summary>
    [Fact]
    public void Groups_at_two_levels_each_hide_only_what_lies_below_them()
    {
        IcicleSegmentDto[] segments =
        [
            new(0, 0, 1000, string.Empty, "All", 0),
            new(1, 0, 800, "A", "A", 1),
            new(1, 800, 10, "B1", "B1", 2),
            new(1, 810, 10, "B2", "B2", 3),
            new(1, 820, 10, "B3", "B3", 4),
            new(1, 830, 170, "C", "C", 5),
            new(2, 0, 600, "A1", "A1", 6),
            new(2, 600, 20, "A2", "A2", 7),
            new(2, 620, 20, "A3", "A3", 8),
            new(2, 640, 150, "A4", "A4", 9),
            new(2, 800, 5, "B1a", "B1a", 10),
            new(2, 830, 100, "C1", "C1", 11),
            new(3, 0, 300, "A1x", "A1x", 12),
            new(3, 600, 10, "A2x", "A2x", 13),
            new(3, 640, 100, "A4x", "A4x", 14),
            new(3, 800, 5, "B1x", "B1x", 15),
            new(3, 830, 50, "C1x", "C1x", 16),
        ];

        var bars = IcicleGeometry.Bars(segments, 1000, width: 200, rowHeight: 20);

        bars.Where(static b => b.IsGroup).Select(static b => (b.Segment.Level, b.Segment.Offset, b.Segment.Width, b.Count))
            .ShouldBe([(1, 800L, 30L, 3), (2, 600L, 40L, 2)], ignoreOrder: true);
        bars.Where(static b => !b.IsGroup).Select(static b => b.Segment.Key)
            .ShouldBe([string.Empty, "A", "C", "A1", "A4", "C1", "A1x", "A4x", "C1x"], ignoreOrder: true);
    }

    /// <summary>A run of narrow bars ends at a wide one: the lone one before it stays itself, the two after it group (3.11 review M11).</summary>
    [Fact]
    public void Narrow_wide_narrow_narrow_makes_one_lone_bar_and_one_group()
    {
        IcicleSegmentDto[] segments =
        [
            new(0, 0, 1000, string.Empty, "All", 0),
            new(1, 0, 10, "N1", "N1", 1),
            new(1, 10, 500, "W", "W", 2),
            new(1, 510, 10, "N2", "N2", 3),
            new(1, 520, 10, "N3", "N3", 4),
        ];

        var bars = IcicleGeometry.Bars(segments, 1000, width: 200, rowHeight: 20);

        bars.Where(static b => b.Segment.Level == 1).Select(static b => (b.Segment.Offset, b.IsGroup, b.Count))
            .ShouldBe([(0L, false, 1), (10L, false, 1), (510L, true, 2)]);
    }

    /// <summary>
    /// Two neighbors narrower than a pixel together are still a group, though it is drawn under a pixel wide: it hides what lies
    /// below them, which a lone bar that narrow does not (3.11 review M11; L9 lists the narrow group).
    /// </summary>
    [Fact]
    public void A_group_under_a_pixel_still_hides_what_lies_below_it()
    {
        IcicleSegmentDto[] segments =
        [
            new(0, 0, 1000, string.Empty, "All", 0),
            new(1, 0, 990, "W", "W", 1),
            new(1, 990, 2, "T1", "T1", 2),
            new(1, 992, 2, "T2", "T2", 3),
            new(2, 990, 1, "Below", "Below", 4),
        ];

        var bars = IcicleGeometry.Bars(segments, 1000, width: 200, rowHeight: 20);

        var group = bars.Single(static b => b.IsGroup);
        (group.Count, group.Segment.Width).ShouldBe((2, 4L));
        group.Width.ShouldBeLessThan(IcicleGeometry.MinWidth);
        bars.Select(static b => b.Segment.Key).ShouldNotContain("Below");
    }

    /// <summary>
    /// At worker scale (a chart can hold 20,000 segments) the bars of a level never overlap, a bar drawn on its own is at least
    /// <see cref="IcicleGeometry.MinWidth"/> wide, and nothing is drawn below a group (3.11 review M11).
    /// </summary>
    [Fact]
    public void At_worker_scale_bars_do_not_overlap_and_nothing_shows_below_a_group()
    {
        var random = new Random(311);
        var segments = new List<IcicleSegmentDto> { new(0, 0, 1_000_000, string.Empty, "All", 0) };
        var parents = new List<IcicleSegmentDto>(segments);
        for (var level = 1; segments.Count < 20_000 && parents.Count > 0; level++)
        {
            var children = new List<IcicleSegmentDto>();
            foreach (var parent in parents)
            {
                // A few wide children, then a tail of small ones, as dominator trees usually look.
                var offset = parent.Offset;
                var left = parent.Width - (parent.Width / 10);
                for (var i = 0; left > 0 && i < 12 && segments.Count + children.Count < 20_000; i++)
                {
                    var width = Math.Max(1, i < 3 ? left / 2 : left / (2 + random.Next(1, 40)));
                    children.Add(new IcicleSegmentDto(level, offset, width, $"{level}:{offset}", "T", (ulong)(segments.Count + children.Count)));
                    offset += width;
                    left -= width;
                }
            }

            segments.AddRange(children);
            parents = children;
        }

        segments.Count.ShouldBe(20_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bars = IcicleGeometry.Bars(segments, 1_000_000, width: 1200, rowHeight: 20);
        watch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"{segments.Count} segments, {bars.Count} bars in {watch.Elapsed.TotalMilliseconds:N1} ms");

        bars.ShouldContain(static b => b.IsGroup);
        foreach (var level in bars.GroupBy(static b => b.Segment.Level))
        {
            var ordered = level.OrderBy(static b => b.Segment.Offset).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                ordered[i].Segment.Offset.ShouldBeGreaterThanOrEqualTo(ordered[i - 1].Segment.Offset + ordered[i - 1].Segment.Width);
            }
        }

        bars.Where(static b => !b.IsGroup).ShouldAllBe(static b => b.Width >= IcicleGeometry.MinWidth);
        var groups = bars.Where(static b => b.IsGroup).Select(static b => b.Segment).ToList();
        bars.ShouldAllBe(b => !groups.Any(g => g.Level < b.Segment.Level && g.Offset <= b.Segment.Offset && b.Segment.Offset < g.Offset + g.Width));
    }

    [Fact]
    public void A_point_hits_the_bar_under_it_and_the_path_to_a_bar_is_its_ancestors()
    {
        var bars = IcicleGeometry.Bars(s_segments, 1000, 400, 20);

        IcicleGeometry.HitTest(bars, 100, 45).ShouldBe(s_segments[4]);
        IcicleGeometry.HitTest(bars, 350, 25).ShouldBe(s_segments[2]);
        IcicleGeometry.HitTest(bars, 300, 45).ShouldBeNull("nothing is drawn there");
        s_segments.Where(s => IcicleGeometry.IsOnPath(s, s_segments[4])).Select(s => s.Key).ShouldBe([string.Empty, "o:10", "o:30"]);
        s_segments.Where(s => IcicleGeometry.IsOnPath(s, null)).ShouldBeEmpty();
    }
}
