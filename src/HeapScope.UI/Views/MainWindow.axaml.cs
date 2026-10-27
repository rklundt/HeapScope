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
using Avalonia.Input;
using Avalonia.Interactivity;
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

        // The mouse's back and forward buttons and Alt+Left and Alt+Right move through the Snapshot tab's history (sprint
        // 1.5), seen before any control handles them.
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
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right)
        {
            e.Handled = Navigate(back: e.Key == Key.Left);
        }
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
