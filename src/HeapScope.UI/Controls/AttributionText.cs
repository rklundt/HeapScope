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


using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;

namespace HeapScope.UI.Controls;

/// <summary>
/// The Attribution Notice's text (ADDITIONAL-TERMS.md 1.1 to 1.3): real selectable text, in which the run with the
/// <c>link</c> class links to <see cref="LinkUri"/>, opened in the default browser on a click (when no text is being
/// selected) or with Enter. Avalonia has no inline hyperlink, so the run is found by hit testing (sprint 2.3.6).
/// </summary>
public sealed class AttributionText : SelectableTextBlock
{
    public static readonly Avalonia.StyledProperty<Uri?> LinkUriProperty =
        Avalonia.AvaloniaProperty.Register<AttributionText, Uri?>(nameof(LinkUri));

    public AttributionText()
    {
        Focusable = true;
    }

    /// <summary>Where the link run leads.</summary>
    public Uri? LinkUri
    {
        get => GetValue(LinkUriProperty);
        set => SetValue(LinkUriProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>The link run's address and its character range in the text, or null when there is none.</summary>
    internal (Uri Uri, int Start, int Length)? Link()
    {
        var position = 0;
        foreach (var inline in Inlines ?? [])
        {
            if (inline is not Run run)
            {
                continue;
            }

            var length = run.Text?.Length ?? 0;
            if (run.Classes.Contains("link") && LinkUri is { } uri)
            {
                return (uri, position, length);
            }

            position += length;
        }

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        Cursor = IsOverLink(e) ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var selecting = SelectionStart != SelectionEnd;
        base.OnPointerReleased(e);
        if (!selecting && e.InitialPressMouseButton == MouseButton.Left && IsOverLink(e))
        {
            Open();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Enter && Link() is not null)
        {
            Open();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private bool IsOverLink(PointerEventArgs e)
    {
        if (Link() is not { } link)
        {
            return false;
        }

        var point = e.GetPosition(this);
        var hit = TextLayout.HitTestPoint(new Avalonia.Point(point.X - Padding.Left, point.Y - Padding.Top));
        var index = hit.TextPosition;
        return hit.IsInside && index >= link.Start && index < link.Start + link.Length;
    }

    private void Open()
    {
        if (Link() is { } link && TopLevel.GetTopLevel(this) is { } top)
        {
            _ = top.Launcher.LaunchUriAsync(link.Uri);
        }
    }
}
