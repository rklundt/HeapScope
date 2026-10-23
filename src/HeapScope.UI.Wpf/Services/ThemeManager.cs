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

        dictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/HeapScope;component/Themes/{theme}.xaml") });
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
