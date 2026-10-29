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


namespace HeapScope.UI.Services;

/// <summary>
/// Dialogs a view model needs, behind an interface so view models stay testable without a window. Asynchronous: the
/// UI thread keeps running while a dialog is open (sprint 2.3.6, Avalonia has no blocking dialogs).
/// </summary>
public interface IUserInteraction
{
    /// <summary>Asks for a dump file; null when the user cancels.</summary>
    Task<string?> PickDumpFileAsync();

    /// <summary>Asks for an allocation trace (.nettrace, sprint 2.5); null when the user cancels.</summary>
    Task<string?> PickTraceFileAsync() => Task.FromResult<string?>(null);

    /// <summary>Asks a yes or no question; true for yes.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Puts text on the clipboard (the About page's details for support, sprint 2.3.7); nothing where there is none.</summary>
    Task CopyTextAsync(string text) => Task.CompletedTask;

    /// <summary>Asks where to save a file (a grid's CSV export, sprint 2.7); null when the user cancels.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName) => Task.FromResult<string?>(null);

    /// <summary>Tells the user something that needs no answer (sprint 2.7).</summary>
    Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    /// <summary>Opens a file in the system's app for its type (the Live timeline); false when nothing could open it.</summary>
    Task<bool> OpenFileAsync(string path) => Task.FromResult(false);

    /// <summary>Shows a file in the system's file manager, selected where the system can; false when it could not.</summary>
    Task<bool> ShowInFolderAsync(string path) => Task.FromResult(false);
}

/// <summary>Answers every question with yes and picks no file; used by smoke runs, which must never block.</summary>
public sealed class AutomaticUserInteraction : IUserInteraction
{
    public Task<string?> PickDumpFileAsync() => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
}
