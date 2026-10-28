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

namespace HeapScope.UI.ViewModels;

/// <summary>The commands with a keyboard shortcut (sprint 2.7).</summary>
public enum ShortcutKeys
{
    Back,
    Forward,
    Find,
    Export,
    Bookmark,
    OpenDump,
    FocusPath,
    Refresh,
    Help,

    /// <summary>Opens the selected rows as one object set (sprint 3.1).</summary>
    OpenAsSet,
}

/// <summary>One line of the shortcuts list.</summary>
public sealed record Shortcut(string Gesture, string Description);

/// <summary>
/// HeapScope's keyboard shortcuts in one table (sprint 2.7): the window matches keys against these gestures and the
/// shortcuts list (F1, the About page) shows them, so the two never differ. The command key is Ctrl on Windows and Linux
/// and Cmd on macOS; gestures use Avalonia's syntax.
/// </summary>
public static class Shortcuts
{
    /// <summary>Pages reachable by number (Command+1 and on), in the sidebar's order.</summary>
    public const int MaxPages = 9;

    /// <summary>The command key's name on this system.</summary>
    public static string CommandKey { get; } = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

    /// <summary>The gesture of <paramref name="key"/> on this system.</summary>
    public static string Gesture(ShortcutKeys key) => key switch
    {
        ShortcutKeys.Back => "Alt+Left",
        ShortcutKeys.Forward => "Alt+Right",
        ShortcutKeys.Find => CommandKey + "+F",
        ShortcutKeys.Export => CommandKey + "+E",
        ShortcutKeys.Bookmark => CommandKey + "+D",
        ShortcutKeys.OpenDump => CommandKey + "+O",
        ShortcutKeys.FocusPath => CommandKey + "+L",
        ShortcutKeys.Refresh => "F5",
        ShortcutKeys.Help => "F1",
        ShortcutKeys.OpenAsSet => CommandKey + "+Shift+O",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    /// <summary>The gesture that shows the page at <paramref name="index"/> (0 based) of the sidebar.</summary>
    public static string PageGesture(int index) => $"{CommandKey}+D{index + 1}";

    /// <summary>The gesture that shows the Snapshots page's tab <paramref name="tab"/>.</summary>
    public static string TabGesture(SnapshotTabs tab) => $"Alt+D{(int)tab + 1}";

    /// <summary>Every shortcut with what it does, for the shortcuts list; <paramref name="pages"/> are the sidebar's names.</summary>
    public static IReadOnlyList<Shortcut> List(IReadOnlyList<string> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return
        [
            .. pages.Take(MaxPages).Select((page, i) => new Shortcut(Show(PageGesture(i)), $"Show the {page} page")),
            .. Enum.GetValues<SnapshotTabs>().Select(tab => new Shortcut(Show(TabGesture(tab)), $"Show the Snapshots page's {tab} tab")),
            new(Show(Gesture(ShortcutKeys.Back)), "Back along the analysis path (also the mouse's back button)"),
            new(Show(Gesture(ShortcutKeys.Forward)), "Forward along the analysis path (also the mouse's forward button)"),
            new(Show(Gesture(ShortcutKeys.FocusPath)), "Go to the analysis path; Up and Down move along it"),
            new(Show(Gesture(ShortcutKeys.Find)), "Go to the filter of the current view"),
            new(Show(Gesture(ShortcutKeys.Export)), "Export the grid with focus (or the view's grid) to CSV"),
            new(Show(Gesture(ShortcutKeys.Bookmark)), "Bookmark the shown object, or the selected row's object"),
            new(Show(Gesture(ShortcutKeys.OpenAsSet)), "Open the selected rows (types, compared types, an inspection row) as one set of objects"),
            new(Show(Gesture(ShortcutKeys.OpenDump)), "Open a dump"),
            new(Show(Gesture(ShortcutKeys.Refresh)), "Refresh the page (processes, sessions)"),
            new("Enter", "Open the selected row (also a double-click)"),
            new("Right, Left", "Expand or collapse the selected tree row"),
            new("Tab, Shift+Tab", "Move between the controls of a view"),
            new(Show(Gesture(ShortcutKeys.Help)), "Show this list (the About page)"),
        ];
    }

    // Avalonia names digit keys D1 to D9; people read them as 1 to 9.
    private static string Show(string gesture) => System.Text.RegularExpressions.Regex.Replace(gesture, @"\+D(\d)$", "+$1");
}
