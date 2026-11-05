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
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using HeapScope.Application.Monitoring;
using HeapScope.UI.Controls;

namespace HeapScope.UI.Tests;

/// <summary>ADR-040: charts and data-named colors follow a theme change while the app runs (Backlog L13, L14).</summary>
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

    private Task ShowsThemeColorsAsync(Func<Control> chart, string key) => ui.RunAsync(async () =>
    {
        var app = Avalonia.Application.Current!;
        var window = new Window { Width = 400, Height = 200, Content = chart() };
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
                using var pixels = frame.Lock();
                // Gen0 is the bottom layer, so the last row but one, in the middle, is its color.
                var offset = ((pixels.Size.Height - 2) * pixels.RowBytes) + (pixels.Size.Width / 2 * 4);
                var (first, second, third) = (Marshal.ReadByte(pixels.Address, offset), Marshal.ReadByte(pixels.Address, offset + 1), Marshal.ReadByte(pixels.Address, offset + 2));
                var drawn = pixels.Format == PixelFormat.Bgra8888
                    ? global::Avalonia.Media.Color.FromRgb(third, second, first)
                    : global::Avalonia.Media.Color.FromRgb(first, second, third);
                drawn.ShouldBe(Color(theme, key), $"{theme}");
            }
        }
        finally
        {
            window.Close();
            app.RequestedThemeVariant = ThemeVariant.Default;
        }

        await Task.CompletedTask;
    });

    private static Color Color(ThemeVariant theme, string key)
    {
        Avalonia.Application.Current!.TryGetResource(key, theme, out var brush).ShouldBeTrue();
        return ((ISolidColorBrush)brush!).Color;
    }
}
