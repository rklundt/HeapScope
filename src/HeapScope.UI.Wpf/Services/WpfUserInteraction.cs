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
using HeapScope.UI.Services;
using Microsoft.Win32;

namespace HeapScope.UI.Wpf.Services;

/// <summary>Standard Windows dialogs. Modal dialogs pump messages, so they never block the dispatcher.</summary>
public sealed class WpfUserInteraction : IUserInteraction
{
    public Task<string?> PickDumpFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a memory dump",
            Filter = "Memory dumps (*.dmp)|*.dmp|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return Task.FromResult(dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FileName : null);
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(MessageBox.Show(System.Windows.Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
}
