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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeapScope.UI.Controls;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Views;

/// <summary>Shell window. View-only code; behavior lives in <see cref="ShellViewModel"/>.</summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel? _viewModel;

    // The designer's constructor.
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(ShellViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // The mouse's back and forward buttons move along the analysis path (sprint 1.5); the keyboard shortcuts (sprint
        // 2.7) are seen before any control handles them.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Every grid's "Export to CSV..." (sprint 2.7) reaches the shell's export service here.
        AddHandler(GridExports.ExportRequestedEvent, OnExportRequested);
    }

    private void OnExportRequested(object? sender, GridExportRequestedEventArgs e)
    {
        e.Handled = true;
        _ = _viewModel?.Exports.ExportAsync(e.Name, e.Export);
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)
        {
            e.Handled = Navigate(back: kind == PointerUpdateKind.XButton1Pressed);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (Shortcut(e) is { } action)
        {
            e.Handled = action();
        }
    }

    /// <summary>The action of the shortcut <paramref name="e"/> matches (the one table, <see cref="Shortcuts"/>; sprint 2.7).</summary>
    private Func<bool>? Shortcut(KeyEventArgs e)
    {
        var shell = _viewModel!;
        if (Matches(e, ShortcutKeys.Back) || Matches(e, ShortcutKeys.Forward))
        {
            var back = Matches(e, ShortcutKeys.Back);
            return () => Navigate(back);
        }

        for (var i = 0; i < Math.Min(shell.NavigationItems.Count, Shortcuts.MaxPages); i++)
        {
            if (Matches(e, Shortcuts.PageGesture(i)))
            {
                var index = i;
                return () => shell.ShowPage(index);
            }
        }

        foreach (var tab in Enum.GetValues<SnapshotTabs>())
        {
            if (Matches(e, Shortcuts.TabGesture(tab)))
            {
                return () =>
                {
                    shell.ShowSnapshotTab(tab);
                    return true;
                };
            }
        }

        return true switch
        {
            _ when Matches(e, ShortcutKeys.Find) => FocusFilter,
            _ when Matches(e, ShortcutKeys.Export) => Export,
            _ when Matches(e, ShortcutKeys.Bookmark) => Bookmark,
            _ when Matches(e, ShortcutKeys.OpenAsSet) => OpenAsSet,
            _ when Matches(e, ShortcutKeys.FocusPath) => FocusPath,
            _ when Matches(e, ShortcutKeys.OpenDump) => () => Run(shell.Snapshots.OpenDumpCommand),
            _ when Matches(e, ShortcutKeys.Refresh) => () =>
            {
                shell.RefreshPage();
                return true;
            },
            _ when Matches(e, ShortcutKeys.Help) => () =>
            {
                shell.ShowHelp();
                return true;
            },
            _ => null,
        };
    }

    private static bool Matches(KeyEventArgs e, ShortcutKeys key) => Matches(e, Shortcuts.Gesture(key));

    private static bool Matches(KeyEventArgs e, string gesture) => s_gestures.GetOrAdd(gesture, KeyGesture.Parse).Matches(e);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeyGesture> s_gestures = new();

    private static bool Run(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    // The shown page's filter box (the types, diff and instances grids name theirs FilterBox).
    private bool FocusFilter()
    {
        if (this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "FilterBox" && t.IsEffectivelyVisible) is not { } box)
        {
            return false;
        }

        box.Focus(NavigationMethod.Tab);
        box.SelectAll();
        return true;
    }

    private bool Export() =>
        GridExports.Target(FocusManager?.GetFocusedElement() as Visual, this) is { } grid && GridExports.RequestExport(grid);

    private bool Bookmark()
    {
        var shell = _viewModel!;
        if (shell.SelectedItem?.Page != shell.Snapshots)
        {
            return false;
        }

        var row = (FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<DataGrid>(includeSelf: true)?.SelectedItem as IObjectLink;
        _ = shell.Snapshots.ToggleBookmarkAsync(row);
        return true;
    }

    // The focused grid's rows as one set (sprint 3.1): several rows where the grid takes several, else the selected one.
    private bool OpenAsSet()
    {
        if ((FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<DataGrid>(includeSelf: true) is not { } grid)
        {
            return false;
        }

        var (command, parameter) = grid.DataContext switch
        {
            TypeSummaryViewModel types => (types.OpenAsSetCommand, (object?)grid.SelectedItems),
            CompareViewModel compare => (compare.OpenAsSetCommand, grid.SelectedItems),
            InspectionsViewModel inspections => (inspections.OpenAsSetCommand, grid.SelectedItem),
            _ => ((System.Windows.Input.ICommand?)null, null),
        };
        if (command is null || !command.CanExecute(parameter))
        {
            return false;
        }

        command.Execute(parameter);
        return true;
    }

    // The analysis path on the Snapshots page, its current step focused so Up and Down move along it.
    private bool FocusPath()
    {
        var shell = _viewModel!;
        shell.SelectedItem = shell.NavigationItems.First(i => i.Page == shell.Snapshots);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (this.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "PathList" && l.IsEffectivelyVisible) is { } list)
                {
                    var item = list.SelectedIndex >= 0 ? list.ContainerFromIndex(list.SelectedIndex) : null;
                    (item ?? (Control)list).Focus(NavigationMethod.Tab);
                }
            },
            DispatcherPriority.Loaded);
        return true;
    }

    private bool Navigate(bool back)
    {
        if (_viewModel is null)
        {
            return false;
        }

        var navigator = _viewModel.Snapshots.Navigator;
        var command = back ? navigator.BackCommand : navigator.ForwardCommand;
        if (_viewModel.SelectedItem?.Page != _viewModel.Snapshots || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }
}
