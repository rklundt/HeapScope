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
