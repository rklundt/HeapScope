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
        top.ShouldEndWith("The tree below lists them.");
        IcicleGeometry.Describe(Bar(new IcicleSegmentDto(2, 0, 10, "g", "+2 smaller", 0), count: 2), 100)
            .ShouldEndWith("Double-click the bar above them, or expand its row in the tree below, to see them.");
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
