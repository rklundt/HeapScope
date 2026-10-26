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
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeapScope.UI.Controls;
using HeapScope.UI.Views;

namespace HeapScope.UI.Tests;

/// <summary>
/// The hand-made controls that replace WPF ones (sprint 2.3.6 review): the attribution link, found by hit testing (ADR-022),
/// and the message window that carries every confirmation.
/// </summary>
public sealed class InteractionTests(UiSession ui)
{
    private const string Before = "Powered by ";
    private const string Link = "HeapScope";
    private static readonly Uri s_uri = new("https://github.com/rklundt/HeapScope");

    [Fact]
    public async Task A_click_on_the_link_opens_it_and_a_click_beside_it_does_not()
    {
        await ui.RunAsync(() =>
        {
            var (window, text, opened) = ShowAttribution();
            try
            {
                Click(window, text, Point(text, Before.Length + 3, 0.5));
                opened.ShouldBe([s_uri]);

                Click(window, text, Point(text, 2, 0.5));
                opened.Count.ShouldBe(1, "plain text is not the link");

                // The character under the pointer counts, not the nearer caret position.
                Click(window, text, Point(text, Before.Length + Link.Length - 1, 0.9));
                opened.Count.ShouldBe(2, "the right half of the link's last character is the link");
                Click(window, text, Point(text, Before.Length - 1, 0.9));
                opened.Count.ShouldBe(2, "the right half of the space before the link is not");
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Selecting_text_across_the_link_does_not_open_it_and_Enter_does()
    {
        await ui.RunAsync(() =>
        {
            var (window, text, opened) = ShowAttribution();
            try
            {
                var start = Point(text, 0, 0.2);
                var end = Point(text, Before.Length + 3, 0.5);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                window.MouseUp(end, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                text.SelectionEnd.ShouldNotBe(text.SelectionStart, "the drag selected text");
                opened.ShouldBeEmpty();

                text.Focus();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                opened.ShouldBe([s_uri]);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_message_window_answers_yes_no_and_false_when_closed_or_escaped(bool withOwner)
    {
        await ui.RunAsync(async () =>
        {
            var owner = new Window { Width = 600, Height = 400 };
            if (withOwner)
            {
                owner.Show();
            }

            try
            {
                (await Answer(owner, MessageButtons.YesNo, w => Press(w, "Yes"))).ShouldBeTrue();
                (await Answer(owner, MessageButtons.YesNo, w => Press(w, "No"))).ShouldBeFalse();
                (await Answer(owner, MessageButtons.Ok, w => Press(w, "OK"))).ShouldBeTrue();
                (await Answer(owner, MessageButtons.YesNo, w => w.Close())).ShouldBeFalse();
                // Escape closes the window on the key press (no release: the window is gone by then).
                (await Answer(owner, MessageButtons.YesNo, w => w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None))).ShouldBeFalse();
            }
            finally
            {
                owner.Close();
            }
        });
    }

    private static async Task<bool> Answer(Window owner, MessageButtons buttons, Action<MessageWindow> answer)
    {
        MessageWindow? shown = null;
        void OnShowing(MessageWindow window) => shown = window;
        MessageWindow.Showing += OnShowing;
        try
        {
            var result = MessageWindow.ShowAsync(owner, "Title", "Message", buttons);
            Dispatcher.UIThread.RunJobs();
            shown.ShouldNotBeNull();
            shown.IsVisible.ShouldBeTrue();
            answer(shown);
            Dispatcher.UIThread.RunJobs();
            result.IsCompleted.ShouldBeTrue("the window closed and answered");
            return await result;
        }
        finally
        {
            MessageWindow.Showing -= OnShowing;
        }
    }

    private static void Press(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static (Window Window, AttributionText Text, List<Uri> Opened) ShowAttribution()
    {
        var opened = new List<Uri>();
        var text = new AttributionText { LinkUri = s_uri, Padding = new Thickness(8, 2), FontSize = 14 };
        text.Inlines!.Add(new Run(Before));
        var link = new Run(Link);
        link.Classes.Add("link");
        text.Inlines.Add(link);
        text.Inlines.Add(new Run(" by Ray Klundt"));
        text.Launch = uri =>
        {
            opened.Add(uri);
            return Task.CompletedTask;
        };

        var window = new Window { Content = new StackPanel { Children = { text } }, Width = 600, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, text, opened);
    }

    // A point inside the character at index, at the given fraction of its width, in window coordinates.
    private static Point Point(AttributionText text, int index, double across)
    {
        var bounds = text.TextLayout.HitTestTextPosition(index);
        var inText = new Point(bounds.X + (bounds.Width * across) + text.Padding.Left, bounds.Y + (bounds.Height / 2) + text.Padding.Top);
        return text.TranslatePoint(inText, TopLevel.GetTopLevel(text)!) ?? throw new InvalidOperationException("The text is not in the window.");
    }

    private static void Click(Window window, AttributionText text, Point point)
    {
        // Clear an earlier selection, as a user's click on the text would.
        text.SelectionStart = text.SelectionEnd = 0;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
