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
using Avalonia.Headless;

namespace HeapScope.UI;

/// <summary>
/// Entry point (sprint 2.3.6, ADR-025): the desktop app on Windows, Linux and macOS. With <c>--headless</c> the same app
/// runs without a display (Avalonia's headless platform), for smoke runs in CI.
/// </summary>
internal static class Program
{
    public const string HeadlessSwitch = "--headless";

    /// <summary>Whether the app runs on the headless platform (no display).</summary>
    public static bool IsHeadless { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var headless = IsHeadless = args.Contains(HeadlessSwitch, StringComparer.OrdinalIgnoreCase);
        var appArgs = args.Where(a => !string.Equals(a, HeadlessSwitch, StringComparison.OrdinalIgnoreCase)).ToArray();
        return BuildApp(appArgs, headless).StartWithClassicDesktopLifetime(appArgs);
    }

    /// <summary>The app builder; headless runs draw with Skia off screen, so layout and text are measured as on screen.</summary>
    public static AppBuilder BuildApp(string[] args, bool headless)
    {
        var builder = AppBuilder.Configure(() => new App(args)).LogToTrace();
        return headless
            ? builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            : builder.UsePlatformDetect();
    }
}
