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


namespace HeapScope.Core;

/// <summary>
/// Creates HeapScope's own folders and files readable by the user only (sprint 2.3.6, 2.3.5 review L5; ARCHITECTURE.md
/// 12): on Linux and macOS folders get 0700 and files 0600 instead of the process's umask, which often lets other users
/// read them. On Windows the user's profile ACL already does this, so nothing changes there. Applies to what HeapScope
/// writes (sessions, logs, index caches and their sidecars, timelines, traces), not to a folder the user picked.
/// </summary>
public static class PrivateFiles
{
    /// <summary>Owner read and write: the mode of a file HeapScope creates off Windows.</summary>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Owner read, write and search: the mode of a folder HeapScope creates off Windows.</summary>
    public const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates <paramref name="path"/> and missing parents, owner-only off Windows; an existing folder is kept as it is.</summary>
    public static DirectoryInfo CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Directory.CreateDirectory(path);
        }

        // .NET gives the mode to the last folder only; missing parents would get the umask's mode, so each is made here.
        var missing = new Stack<string>();
        for (var folder = Path.GetFullPath(path); !Directory.Exists(folder); folder = Path.GetDirectoryName(folder)!)
        {
            missing.Push(folder);
            if (Path.GetDirectoryName(folder) is null)
            {
                break;
            }
        }

        while (missing.TryPop(out var folder))
        {
            Directory.CreateDirectory(folder, FolderMode);
        }

        return new DirectoryInfo(path);
    }

    /// <summary>Opens <paramref name="path"/> like the <see cref="FileStream"/> constructor; a file it creates is owner-only off Windows.</summary>
    public static FileStream Open(string path, System.IO.FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None)
    {
        var settings = new FileStreamOptions { Mode = mode, Access = access, Share = share, BufferSize = bufferSize, Options = options };
        if (!OperatingSystem.IsWindows() && mode is System.IO.FileMode.CreateNew or System.IO.FileMode.Create or System.IO.FileMode.OpenOrCreate)
        {
            settings.UnixCreateMode = FileMode;
        }

        return new FileStream(path, settings);
    }
}
