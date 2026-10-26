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
using Avalonia.Layout;

namespace HeapScope.UI.Views;

/// <summary>
/// A small modal message or question window (sprint 2.3.6: Avalonia has no message box). Modal to its owner when there
/// is one; the UI thread keeps running while it is open.
/// </summary>
public sealed class MessageWindow : Window
{
    private MessageWindow(string title, string message, MessageButtons buttons)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var text = new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (buttons == MessageButtons.YesNo)
        {
            row.Children.Add(Button("Yes", true, isDefault: true));
            row.Children.Add(Button("No", false, isCancel: true));
        }
        else
        {
            row.Children.Add(Button("OK", true, isDefault: true, isCancel: true));
        }

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children = { row, text },
        };
        DockPanel.SetDock(row, Dock.Bottom);
    }

    /// <summary>Raised when a message window is about to show (tests answer it, sprint 2.3.6 review).</summary>
    internal static event Action<MessageWindow>? Showing;

    /// <summary>Shows the window and returns true for Yes or OK, false for No or when it is closed.</summary>
    public static async Task<bool> ShowAsync(Window? owner, string title, string message, MessageButtons buttons)
    {
        var window = new MessageWindow(title, message, buttons);
        Showing?.Invoke(window);
        if (owner is { IsVisible: true })
        {
            return await window.ShowDialog<bool?>(owner) == true;
        }

        var closed = new TaskCompletionSource<bool>();
        var result = false;
        window.Closed += (_, _) => closed.TrySetResult(result);
        window.Tag = (Action<bool>)(value => result = value);
        window.Show();
        return await closed.Task;
    }

    private Button Button(string text, bool result, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button { Content = text, MinWidth = 80, IsDefault = isDefault, IsCancel = isCancel, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) =>
        {
            (Tag as Action<bool>)?.Invoke(result);
            Close(result);
        };
        return button;
    }
}
