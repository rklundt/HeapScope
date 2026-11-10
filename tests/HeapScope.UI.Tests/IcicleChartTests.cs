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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using HeapScope.Contracts;
using HeapScope.UI.Controls;

namespace HeapScope.UI.Tests;

/// <summary>The Retainers tab's icicle chart (sprint 2.4): it draws headless, a click selects a bar, a double-click focuses it.</summary>
public sealed class IcicleChartTests(UiSession ui)
{
    private static readonly IcicleSegmentDto[] s_segments =
    [
        new(0, 0, 1000, string.Empty, "All reachable objects", 0),
        new(1, 0, 750, "o:10", "App.Cache", 0x10),
        new(1, 750, 250, "o:20", "App.Other", 0x20),
        new(2, 0, 500, "o:30", "App.Entry", 0x30),
    ];

    /// <summary>Most bars are slivers with no room for a label, so hovering is how a user learns what one is (3.10, the owner's own test).</summary>
    [Fact]
    public async Task Hovering_a_bar_says_what_it_is_and_moving_off_it_clears_that()
    {
        await ui.RunAsync(() =>
        {
            var chart = new IcicleChart { Segments = s_segments, Total = 1000, Width = 400 };
            var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { chart } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                window.CaptureRenderedFrame().ShouldNotBeNull();
                window.MouseMove(chart.TranslatePoint(new Point(350, 1.5 * chart.RowHeight), window)!.Value);
                Dispatcher.UIThread.RunJobs();
                ToolTip.GetTip(chart).ShouldBeOfType<string>().ShouldStartWith("App.Other\nKeeps ");

                window.MouseMove(chart.TranslatePoint(new Point(100, 2.5 * chart.RowHeight), window)!.Value);
                Dispatcher.UIThread.RunJobs();
                ToolTip.GetTip(chart).ShouldBeOfType<string>().ShouldStartWith("App.Entry\nKeeps ");

                // Past the entry's end, on its level: no bar.
                window.MouseMove(chart.TranslatePoint(new Point(350, 2.5 * chart.RowHeight), window)!.Value);
                Dispatcher.UIThread.RunJobs();
                ToolTip.GetTip(chart).ShouldBeNull();
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>Slivers are one bar that says how many, and a click on it selects nothing (3.10 review M44).</summary>
    [Fact]
    public async Task A_group_of_narrow_bars_says_what_it_holds_and_a_click_selects_nothing()
    {
        await ui.RunAsync(() =>
        {
            IcicleSegmentDto[] segments =
            [
                new(0, 0, 1000, string.Empty, "All reachable objects", 0),
                new(1, 0, 900, "o:10", "App.Cache", 0x10),
                new(1, 900, 5, "o:20", "App.A", 0x20),
                new(1, 905, 5, "o:30", "App.B", 0x30),
                new(1, 910, 5, "o:40", "App.C", 0x40),
            ];
            var chart = new IcicleChart { Segments = segments, Total = 1000, Width = 400 };
            var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { chart } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                window.CaptureRenderedFrame().ShouldNotBeNull();
                var group = chart.TranslatePoint(new Point(365, 1.5 * chart.RowHeight), window)!.Value;
                window.MouseMove(group);
                Dispatcher.UIThread.RunJobs();
                ToolTip.GetTip(chart).ShouldBeOfType<string>().ShouldStartWith("+3 smaller\nTogether keep at least ");

                window.MouseDown(group, MouseButton.Left);
                window.MouseUp(group, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                chart.SelectedSegment.ShouldBeNull();
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_click_selects_a_bar_and_a_double_click_focuses_it()
    {
        await ui.RunAsync(() =>
        {
            var focused = new List<IcicleSegmentDto?>();
            var chart = new IcicleChart { Segments = s_segments, Total = 1000, FocusCommand = new RelayCommand<IcicleSegmentDto?>(focused.Add) };
            var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { chart } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                chart.Bounds.Height.ShouldBe(3 * chart.RowHeight, "three levels");
                window.CaptureRenderedFrame().ShouldNotBeNull();

                // The entry sits at level 2 over the left half: 0 to 200 pixels of 400.
                var entry = chart.TranslatePoint(new Point(100, (2.5 * chart.RowHeight)), window)!.Value;
                window.MouseDown(entry, MouseButton.Left);
                window.MouseUp(entry, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                chart.SelectedSegment.ShouldBe(s_segments[3]);
                focused.ShouldBeEmpty();

                var other = chart.TranslatePoint(new Point(350, 1.5 * chart.RowHeight), window)!.Value;
                window.MouseDown(other, MouseButton.Left);
                window.MouseUp(other, MouseButton.Left);
                window.MouseDown(other, MouseButton.Left);
                window.MouseUp(other, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                chart.SelectedSegment.ShouldBe(s_segments[2]);
                focused.ShouldBe([s_segments[2]]);

                // Beside the bars nothing changes.
                var empty = chart.TranslatePoint(new Point(300, 2.5 * chart.RowHeight), window)!.Value;
                window.MouseDown(empty, MouseButton.Left);
                window.MouseUp(empty, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                chart.SelectedSegment.ShouldBe(s_segments[2]);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }
}
