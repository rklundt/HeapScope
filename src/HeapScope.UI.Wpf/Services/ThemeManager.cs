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

using System.Windows;
using Microsoft.Win32;

namespace HeapScope.UI.Wpf.Services;

/// <summary>
/// Loads the light or dark theme (sprint 1.3): the Windows app theme by default (Settings, Personalization, Colors),
/// or <c>Ui:Theme</c> = Light or Dark from configuration.
/// </summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>The theme to use: the configured one when valid, otherwise the Windows app theme.</summary>
    public static string Choose(string? configured)
    {
        if (string.Equals(configured, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        if (string.Equals(configured, Dark, StringComparison.OrdinalIgnoreCase))
        {
            return Dark;
        }

        return WindowsUsesDarkApps() ? Dark : Light;
    }

    /// <summary>Merges the theme dictionary into the application resources (replacing a previous theme).</summary>
    public static void Apply(System.Windows.Application application, string theme)
    {
        ArgumentNullException.ThrowIfNull(application);
        var dictionaries = application.Resources.MergedDictionaries;
        foreach (var old in dictionaries.Where(d => d.Source?.OriginalString.Contains("/Themes/", StringComparison.Ordinal) == true).ToList())
        {
            dictionaries.Remove(old);
        }

        dictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/HeapScope.Wpf;component/Themes/{theme}.xaml") });
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }
}
