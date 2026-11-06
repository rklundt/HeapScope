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


using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using HeapScope.Application.Monitoring;
using HeapScope.Contracts;
using HeapScope.UI.Controls;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Tests;

/// <summary>ADR-040: every chart, the icons and data-named colors follow a theme change while the app runs (Backlog L13, L14).</summary>
public class ThemeChangeTests(UiSession ui)
{
    [Fact]
    public async Task A_timeline_redraws_in_the_new_theme_s_colors()
    {
        var samples = new[]
        {
            new HeapSample(DateTimeOffset.UnixEpoch, 500, 100, 100, 100, 100, 100, 0, 0, 0, 0, 0, 0),
            new HeapSample(DateTimeOffset.UnixEpoch.AddSeconds(10), 500, 100, 100, 100, 100, 100, 0, 0, 0, 0, 0, 0),
        };
        await ShowsThemeColorsAsync(() => new TimelineChart { Samples = samples }, "HeapKind.Gen0");
    }

    [Fact]
    public async Task A_data_named_background_follows_the_theme()
    {
        await ui.RunAsync(async () =>
        {
            var app = Avalonia.Application.Current!;
            var border = new Border { Width = 40, Height = 40 };
            ThemeBrush.SetBackground(border, "HeapKind.Gen2");
            var window = new Window { Width = 100, Height = 100, Content = border };
            window.Show();
            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    app.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    ((ISolidColorBrush)border.Background!).Color.ShouldBe(Color(theme, "HeapKind.Gen2"));
                }
            }
            finally
            {
                window.Close();
                app.RequestedThemeVariant = ThemeVariant.Default;
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_icicle_chart_redraws_in_the_new_theme_s_colors()
    {
        IcicleSegmentDto[] segments = [new(0, 0, 100, "a", "App.Root", 0x1000), new(1, 0, 100, "b", "App.Child", 0x2000)];
        await ShowsThemeColorsAsync(() => new IcicleChart { Segments = segments, Total = 100 }, "Icicle.Bar1");
    }

    [Fact]
    public async Task A_retention_diagram_redraws_in_the_new_theme_s_colors()
    {
        KeyPathDto[] paths = [new(new PathRootDto(RootKindDto.StaticField, false, "static field s_list"), [new PathStepDto("App.List", false)], "App.Item", 10, 100, 0x1000)];
        await ShowsThemeColorsAsync(() => new KeyPathDiagramView { Diagram = KeyPathDiagram.Build(paths, showFramework: false) }, "Icicle.Path");
    }

    [Fact]
    public async Task An_allocation_timeline_redraws_in_the_new_theme_s_colors()
    {
        AllocationBucketDto[] buckets = [new(0, 1000, 1), new(1, 2000, 1)];
        await ShowsThemeColorsAsync(() => new AllocationTimeline { Buckets = buckets, BucketSeconds = 1, DurationSeconds = 2 }, "Allocation.Bar");
    }

    [Fact]
    public async Task An_icon_redraws_in_the_new_theme_s_text_color()
    {
        await ShowsColorsAsync(
            () => new DuotoneIcon { Icon = "Live", Width = 200, Height = 200 },
            (control, _) => ((ISolidColorBrush)((DuotoneIcon)control).Foreground!).Color,
            mustDiffer: true);
    }

    private Task ShowsThemeColorsAsync(Func<Control> chart, string key) =>
        ShowsColorsAsync(chart, (_, theme) => Color(theme, key), mustDiffer: true);

    /// <summary>The frame holds the expected color in each theme, so a chart that kept the old one fails.</summary>
    private Task ShowsColorsAsync(Func<Control> chart, Func<Control, ThemeVariant, Color> expected, bool mustDiffer) => ui.RunAsync(async () =>
    {
        var app = Avalonia.Application.Current!;
        var control = chart();
        var window = new Window { Width = 400, Height = 300, Content = control };
        window.Show();
        try
        {
            var seen = new List<Color>();
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                var color = expected(control, theme);
                seen.Add(color);
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
                Holds(frame, color).ShouldBeTrue($"{color} in {theme}");
            }

            if (mustDiffer)
            {
                seen[0].ShouldNotBe(seen[1], "the themes differ here, or this proves nothing");
            }
        }
        finally
        {
            window.Close();
            app.RequestedThemeVariant = ThemeVariant.Default;
        }

        await Task.CompletedTask;
    });

    private static bool Holds(WriteableBitmap frame, Color color)
    {
        using var pixels = frame.Lock();
        var (first, third) = pixels.Format == PixelFormat.Bgra8888 ? (color.B, color.R) : (color.R, color.B);
        for (var y = 0; y < pixels.Size.Height; y++)
        {
            for (var x = 0; x < pixels.Size.Width; x++)
            {
                var offset = (y * pixels.RowBytes) + (x * 4);
                if (Marshal.ReadByte(pixels.Address, offset) == first
                    && Marshal.ReadByte(pixels.Address, offset + 1) == color.G
                    && Marshal.ReadByte(pixels.Address, offset + 2) == third)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Color Color(ThemeVariant theme, string key)
    {
        Avalonia.Application.Current!.TryGetResource(key, theme, out var brush).ShouldBeTrue();
        return ((ISolidColorBrush)brush!).Color;
    }
}
