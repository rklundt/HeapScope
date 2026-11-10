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
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HeapScope.UI.Controls;

/// <summary>
/// An info icon (ADR-040): says what the thing beside it is for. Hovering shows the text; a click, Enter or Space opens it to stay and be
/// selected, and Escape closes it. It is a control of its own, so it answers beside a disabled button too, which a tooltip on that
/// button would not.
/// </summary>
public sealed class InfoIcon : Button
{
    private const double TextWidth = 380;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InfoIcon, string?>(nameof(Text));

    private readonly TextBlock _tip = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = TextWidth };
    private readonly SelectableTextBlock _opened = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = TextWidth };

    public InfoIcon()
    {
        Classes.Add("infoIcon");
        VerticalAlignment = VerticalAlignment.Center;
        Content = new DuotoneIcon { Icon = "Info", Width = 14, Height = 14 };
        Flyout = new Flyout { Content = _opened, Placement = PlacementMode.Bottom };
        AutomationProperties.SetName(this, "What this is");
    }

    /// <summary>What to say.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _tip.Text = Text;
            _opened.Text = Text;
            ToolTip.SetTip(this, string.IsNullOrEmpty(Text) ? null : _tip);
            AutomationProperties.SetHelpText(this, Text);
        }
    }

    protected override void OnClick()
    {
        // The open text takes over from the hover text.
        ToolTip.SetIsOpen(this, false);
        base.OnClick();
    }
}
