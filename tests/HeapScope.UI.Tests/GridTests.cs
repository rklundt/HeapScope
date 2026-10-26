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
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using HeapScope.UI.Controls;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Tests;

/// <summary>
/// The grids over data-virtualized row lists (sprint 2.3.5 prototype, 2.3.6 review item): the DataGrid reads rows by
/// index through <see cref="VirtualCollectionView{TRow}"/>, so a 1.5 million row list makes only the rows on screen and
/// is never enumerated, whatever the user does with the grid.
/// </summary>
public sealed class GridTests(UiSession ui)
{
    private const int Rows = 1_500_000;

    [Fact]
    public async Task Showing_scrolling_and_the_keyboard_touch_only_the_rows_on_screen()
    {
        await ui.RunAsync(() =>
        {
            var (window, grid, rows, created) = Show();
            var before = VirtualCollectionViews.Enumerations;

            grid.ScrollIntoView(rows[Rows / 2], null);
            Dispatcher.UIThread.RunJobs();
            grid.SelectedIndex = Rows / 2;
            grid.Focus();
            foreach (var key in new[] { PhysicalKey.ArrowDown, PhysicalKey.PageDown, PhysicalKey.End, PhysicalKey.PageUp, PhysicalKey.Home })
            {
                Press(window, key);
            }

            // Select all and copy: the selection is single, and a copy takes the selected row only.
            Press(window, PhysicalKey.A, RawInputModifiers.Control);
            Press(window, PhysicalKey.C, RawInputModifiers.Control);

            VirtualCollectionViews.Enumerations.ShouldBe(before, "no grid path walks the whole list");
            created().ShouldBeLessThan(2_000, "only the rows shown were made");
            grid.SelectedItems.Count.ShouldBeLessThanOrEqualTo(1);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_header_click_asks_the_view_model_to_sort_and_the_arrow_follows_its_state()
    {
        await ui.RunAsync(() =>
        {
            var (window, grid, _, _) = Show();
            var sorted = new List<string>();
            GridBehaviors.SetSortCommand(grid, new RelayCommand<string>(path => sorted.Add(path!)));
            GridBehaviors.SetSortState(grid, "Bytes|desc");
            Dispatcher.UIThread.RunJobs();

            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(h => Equals(h.Content, "Name"));
            var center = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            sorted.ShouldBe(["Name"], "the worker sorts; the grid never sorts its rows itself");
            var view = grid.ItemsSource.ShouldBeOfType<VirtualCollectionView<TypeRowItem>>();
            view.SortDescriptions.ShouldHaveSingleItem().PropertyPath.ShouldBe("Bytes");
            view.SortDescriptions[0].Direction.ShouldBe(System.ComponentModel.ListSortDirection.Descending);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Double_click_and_enter_open_the_row()
    {
        await ui.RunAsync(() =>
        {
            var (window, grid, rows, _) = Show();
            var opened = new List<object?>();
            GridBehaviors.SetOpenCommand(grid, new RelayCommand<object?>(opened.Add));
            Dispatcher.UIThread.RunJobs();

            var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext == rows[0]);
            var center = row.TranslatePoint(new Point(20, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            grid.SelectedIndex = 1;
            grid.Focus();
            Press(window, PhysicalKey.Enter);

            opened.ShouldContain(rows[0]);
            opened.ShouldContain(rows[1]);
            window.Close();
            return Task.CompletedTask;
        });
    }

    private static (Window Window, DataGrid Grid, VirtualRowList<TypeRowItem> Rows, Func<int> Created) Show()
    {
        var created = 0;
        var rows = new VirtualRowList<TypeRowItem>(null, Rows, i =>
        {
            created++;
            return new TypeRowItem(i);
        }, (_, _) => { });
        var grid = new DataGrid { CanUserSortColumns = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(TypeRowItem.Name)), SortMemberPath = "Name", Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Bytes", Binding = new Binding(nameof(TypeRowItem.Bytes)), SortMemberPath = "Bytes" });
        GridBehaviors.SetRows(grid, rows);
        var window = new Window { Content = grid, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, grid, rows, () => created);
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }
}
