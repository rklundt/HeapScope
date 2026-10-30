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
/// read them. On Windows a file it creates gets an owner-only access list since sprint 3.7 (peer review P15): the profile's
/// own list covers files under the profile, not a dump's index cache in a folder the user picked (<c>--out</c>). Applies
/// to what HeapScope writes (sessions, logs, index caches and their sidecars, timelines, traces, exports), not to a folder
/// the user picked.
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

    /// <summary>
    /// Opens <paramref name="path"/> like the <see cref="FileStream"/> constructor; a file it creates is owner-only off
    /// Windows. <paramref name="preallocationSize"/> reserves disk space for a new file up front, so a full disk fails here
    /// with an <see cref="IOException"/> rather than later (a mapped write to a sparse file on Linux and macOS would
    /// kill the process with SIGBUS; sprint 2.3.6 review).
    /// </summary>
    public static FileStream Open(string path, System.IO.FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None, long preallocationSize = 0)
    {
        var settings = new FileStreamOptions { Mode = mode, Access = access, Share = share, BufferSize = bufferSize, Options = options, PreallocationSize = preallocationSize };
        if (!OperatingSystem.IsWindows() && mode is System.IO.FileMode.CreateNew or System.IO.FileMode.Create or System.IO.FileMode.OpenOrCreate)
        {
            settings.UnixCreateMode = FileMode;
        }

        if (OperatingSystem.IsWindows() && mode is System.IO.FileMode.CreateNew or System.IO.FileMode.Create)
        {
            // The file is created with its owner-only list in one step, so no handle can be opened under the folder's list
            // in between (3.7 review), then opened as asked; an existing file keeps its list when overwritten, so a second
            // Create keeps the new one and still preallocates.
            FileSystemAclExtensions.Create(new FileInfo(path), mode, System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 1, FileOptions.None, OwnerOnly()).Dispose();
            settings.Mode = System.IO.FileMode.Create;
        }

        var stream = new FileStream(path, settings);
        if (OperatingSystem.IsWindows() && mode is System.IO.FileMode.Create)
        {
            // A file that existed before keeps its own list through the step above: it is made owner-only here.
            try
            {
                Protect(path);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        return stream;
    }

    /// <summary>
    /// Makes an existing file readable by the user only (sprint 3.7, peer review P15): 0600 off Windows; on Windows an
    /// access list that grants the current user full control and inherits nothing from its folder. For files HeapScope
    /// created or a capture wrote for it, such as a dump in a folder the user picked.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The file is not the user's to change.</exception>
    public static void Protect(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
            return;
        }

        new FileInfo(path).SetAccessControl(OwnerOnly());
    }

    /// <summary>An access list granting the current Windows user full control and inheriting nothing.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static System.Security.AccessControl.FileSecurity OwnerOnly()
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(user, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Creates HeapScope's own data folder <paramref name="path"/> (with its parents) owner-only, and makes it owner-only
    /// when it exists already with a wider mode (sprint 3.7, peer review P15: a folder another user created first, or one
    /// from an older build, kept its mode). Off Windows only the owner may change a mode, so a folder that is not the
    /// user's fails here instead of holding the user's files.
    /// </summary>
    /// <exception cref="IOException">The folder exists and is not the user's.</exception>
    public static DirectoryInfo CreatePrivateDirectory(string path)
    {
        var existed = Directory.Exists(path);
        var folder = CreateDirectory(path);
        if (existed && !OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != FolderMode)
        {
            try
            {
                File.SetUnixFileMode(path, FolderMode);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException($"The folder {path} belongs to another user, so HeapScope does not keep files in it.", ex);
            }
        }

        return folder;
    }
}
