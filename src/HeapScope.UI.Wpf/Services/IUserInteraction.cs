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

using System.Windows;
using Microsoft.Win32;

namespace HeapScope.UI.Wpf.Services;

/// <summary>Dialogs a view model needs, behind an interface so view models stay testable without a window.</summary>
public interface IUserInteraction
{
    /// <summary>Asks for a dump file; null when the user cancels.</summary>
    string? PickDumpFile();

    /// <summary>Asks a yes or no question; true for yes.</summary>
    bool Confirm(string title, string message);
}

/// <summary>Standard Windows dialogs. Modal dialogs pump messages, so they never block the dispatcher.</summary>
public sealed class WpfUserInteraction : IUserInteraction
{
    public string? PickDumpFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a memory dump",
            Filter = "Memory dumps (*.dmp)|*.dmp|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(System.Windows.Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}

/// <summary>Answers every question with yes and picks no file; used by smoke runs, which must never block.</summary>
public sealed class AutomaticUserInteraction : IUserInteraction
{
    public string? PickDumpFile() => null;

    public bool Confirm(string title, string message) => true;
}
