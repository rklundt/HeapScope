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

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace HeapScope.UI.Controls;

/// <summary>
/// The grids' drill-down for lists (sprint 2.7): <c>OpenCommand</c> runs on the selected item for Enter or a double-click
/// on an item (the Bookmarks list), so a list is used by keyboard as a grid is.
/// </summary>
public static class ListBehaviors
{
    public static readonly AttachedProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.RegisterAttached<ListBox, ICommand?>("OpenCommand", typeof(ListBehaviors));

    static ListBehaviors()
    {
        OpenCommandProperty.Changed.AddClassHandler<ListBox>((list, e) =>
        {
            list.DoubleTapped -= OnDoubleTapped;
            list.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (e.NewValue is not null)
            {
                list.DoubleTapped += OnDoubleTapped;
                list.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            }
        });
    }

    public static ICommand? GetOpenCommand(ListBox list) => list?.GetValue(OpenCommandProperty);

    public static void SetOpenCommand(ListBox list, ICommand? value) => list?.SetValue(OpenCommandProperty, value);

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a double-click on an item, not on one of its buttons.
        if (sender is ListBox list && e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: { } item })
        {
            e.Handled = Run(list, item);
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && sender is ListBox { SelectedItem: { } item } list)
        {
            e.Handled = Run(list, item);
        }
    }

    private static bool Run(ListBox list, object item)
    {
        if (GetOpenCommand(list) is not { } command || !command.CanExecute(item))
        {
            return false;
        }

        command.Execute(item);
        return true;
    }
}
