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

using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The one shortcut table (sprint 2.7).</summary>
public sealed class ShortcutsTests
{
    [Fact]
    public void Every_command_has_a_gesture_and_no_two_share_one()
    {
        var gestures = Enum.GetValues<ShortcutKeys>().Select(Shortcuts.Gesture)
            .Concat(Enumerable.Range(0, Shortcuts.MaxPages).Select(Shortcuts.PageGesture))
            .Concat(Enum.GetValues<SnapshotTabs>().Select(Shortcuts.TabGesture))
            .ToList();

        gestures.ShouldBeUnique();
        gestures.ShouldAllBe(g => g.Length > 0);
    }

    [Fact]
    public void The_list_names_each_page_and_tab_and_shows_digits_as_people_read_them()
    {
        var list = Shortcuts.List(["Home", "Snapshots"]);

        list.Select(s => s.Description).ShouldContain("Show the Snapshots page");
        list.ShouldContain(s => s.Gesture == Shortcuts.CommandKey + "+2");
        list.ShouldContain(s => s.Gesture == "Alt+4" && s.Description.Contains("Retainers", StringComparison.Ordinal));
        list.ShouldNotContain(s => s.Gesture.Contains("+D1", StringComparison.Ordinal));
        list.Select(s => s.Gesture).ShouldContain(Shortcuts.CommandKey + "+E");
        Shortcuts.CommandKey.ShouldBe(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl");
    }
}
