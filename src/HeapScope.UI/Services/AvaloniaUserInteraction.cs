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
using Avalonia.Platform.Storage;
using HeapScope.UI.Views;

namespace HeapScope.UI.Services;

/// <summary>
/// The system's file picker and HeapScope's own question window (sprint 2.3.6: Avalonia has no message box). Both are
/// asynchronous: the UI thread keeps running while they are open.
/// </summary>
public sealed class AvaloniaUserInteraction(Func<Window?> owner) : IUserInteraction
{
    private static readonly FilePickerFileType[] s_dumpTypes =
    [
        new("Memory dumps") { Patterns = ["*.dmp", "*.gcdump", "core*"] },
        new("All files") { Patterns = ["*"] },
    ];

    public async Task<string?> PickDumpFileAsync()
    {
        if (owner() is not { } window)
        {
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a memory dump",
            AllowMultiple = false,
            FileTypeFilter = s_dumpTypes,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        MessageWindow.ShowAsync(owner(), title, message, MessageButtons.YesNo);
}
