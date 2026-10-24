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

using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Navigation;
using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.UI.Wpf.Views;

/// <summary>Shell window. View-only code; behavior lives in <see cref="ShellViewModel"/>.</summary>
public partial class MainWindow : System.Windows.Window
{
    private readonly ShellViewModel _viewModel;

    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    // The mouse's back and forward buttons and Alt+Left and Alt+Right move through the Snapshot tab's history (sprint 1.5).
    // They are routed here because no WPF gesture covers the extra mouse buttons.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            e.Handled = Navigate(back: e.ChangedButton == MouseButton.XButton1);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        {
            e.Handled = Navigate(back: e.SystemKey == Key.Left);
        }
    }

    // Opens the attribution link (ADDITIONAL-TERMS.md 1.1) in the default browser.
    private void OnAttributionNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        e.Handled = true;
    }

    private bool Navigate(bool back)
    {
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
