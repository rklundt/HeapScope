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
        try
        {
            return BuildApp(appArgs, headless).StartWithClassicDesktopLifetime(appArgs);
        }
        catch (Exception ex)
        {
            // A failure before the host exists (no display, a missing native library, a bad option) reached no log, and a
            // desktop launch has no console (sprint 2.3.7 review M18): it is written to the file log directly.
            LogFatalStart(appArgs, ex);
            Console.Error.WriteLine($"HeapScope could not start: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The app builder; headless runs draw with Skia off screen, so layout and text are measured as on screen.</summary>
    public static AppBuilder BuildApp(string[] args, bool headless)
    {
        // Avalonia's own warnings (binding errors, platform problems) go to the HeapScope log once it exists (review M18).
        var builder = AppBuilder.Configure(() => new App(args)).AfterSetup(_ => Avalonia.Logging.Logger.Sink = new Services.AvaloniaLogSink());
        return headless
            ? builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            : builder.UsePlatformDetect();
    }

    private static void LogFatalStart(string[] args, Exception exception)
    {
        var directoryAt = Array.FindIndex(args, a => string.Equals(a, "--" + Application.Logging.FileLogging.DirectoryKey, StringComparison.OrdinalIgnoreCase));
        var directory = directoryAt >= 0 && directoryAt + 1 < args.Length ? args[directoryAt + 1] : null;
        using var provider = new Application.Logging.FileLoggerProvider(directory, app: "ui");
        Microsoft.Extensions.Logging.LoggerExtensions.LogCritical(provider.CreateLogger("HeapScope.UI.Program"), exception, "HeapScope could not start; arguments {Arguments}", string.Join(' ', args));
    }
}
