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

namespace HeapScope.UI.Controls;

/// <summary>
/// A page host of the shell: the page's view is built when the page is first shown and then kept, so switching pages is
/// a visibility change, not a rebuild of its grids, and keeps scroll positions and expanded state (sprint 1.5).
/// </summary>
public sealed class LazyContentControl : ContentControl
{
    public static readonly StyledProperty<object?> PageProperty =
        AvaloniaProperty.Register<LazyContentControl, object?>(nameof(Page));

    public static readonly StyledProperty<bool> IsShownProperty =
        AvaloniaProperty.Register<LazyContentControl, bool>(nameof(IsShown));

    static LazyContentControl()
    {
        IsShownProperty.Changed.AddClassHandler<LazyContentControl>((control, _) => control.Update());
        PageProperty.Changed.AddClassHandler<LazyContentControl>((control, _) => control.Update());
    }

    public LazyContentControl()
    {
        IsVisible = false;
        Focusable = false;
    }

    protected override Type StyleKeyOverride => typeof(ContentControl);

    /// <summary>The page's view model; it becomes the content on first show.</summary>
    public object? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    /// <summary>Whether this page is the selected one.</summary>
    public bool IsShown
    {
        get => GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    private void Update()
    {
        if (IsShown && !ReferenceEquals(Content, Page))
        {
            Content = Page;
        }

        IsVisible = IsShown;
    }
}
