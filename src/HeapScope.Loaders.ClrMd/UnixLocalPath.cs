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


namespace HeapScope.Loaders.ClrMd;

/// <summary>A mounted file system as the local path rule sees it: its mount point, drive type and format.</summary>
public sealed record MountPoint(string Path, DriveType Type, string Format);

/// <summary>
/// The local path rule of ADR-017 on Linux and macOS (sprint 2.3.5 review H1). Every absolute path there has the root
/// <c>/</c>, so the rule looks at the mount that holds the path: it walks the path one component at a time, resolving
/// symbolic links, and requires every step to lie on a local disk (a fixed file system, container overlays included, or
/// <c>tmpfs</c>). Network mounts (NFS, SMB, autofs, FUSE), <c>/proc</c>, <c>/sys</c> and <c>/dev</c> are refused, and
/// the mount is checked before anything on it is looked at, so a path recorded in a dump never triggers an automount.
/// </summary>
public static class UnixLocalPath
{
    private const int MaxLinks = 40;

    /// <summary>The file systems mounted on this machine; one that cannot be described is left out (and so refused).</summary>
    public static IReadOnlyList<MountPoint> CurrentMounts()
    {
        var mounts = new List<MountPoint>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                mounts.Add(new MountPoint(drive.Name, drive.DriveType, drive.DriveFormat));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return mounts;
    }

    /// <summary>
    /// True when <paramref name="path"/> is absolute and it, every folder on the way to it and every link target lie on a
    /// local mount of <paramref name="mounts"/>. <paramref name="linkTarget"/> reads a link (null for anything else); the
    /// default reads the file system.
    /// </summary>
    public static bool IsLocal(string path, IReadOnlyList<MountPoint> mounts, Func<string, string?>? linkTarget = null)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        linkTarget ??= ReadLink;
        var pending = new Stack<string>();
        Push(pending, path);
        var current = "/";
        var links = 0;
        if (!IsOnLocalMount(current, mounts))
        {
            return false;
        }

        while (pending.TryPop(out var part))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Parent(current);
                continue;
            }

            var next = current == "/" ? "/" + part : current + "/" + part;
            if (!IsOnLocalMount(next, mounts))
            {
                return false;
            }

            string? target;
            try
            {
                target = linkTarget(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinks || target.Length == 0)
            {
                return false;
            }

            if (target[0] == '/')
            {
                current = "/";
            }

            Push(pending, target);
        }

        return true;
    }

    /// <summary>True when the mount holding <paramref name="path"/> (the longest mount point it lies under) is local.</summary>
    public static bool IsOnLocalMount(string path, IReadOnlyList<MountPoint> mounts)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        MountPoint? best = null;
        foreach (var mount in mounts)
        {
            var point = mount.Path.Length > 1 ? mount.Path.TrimEnd('/') : mount.Path;
            var under = point == "/" || path == point || path.StartsWith(point + "/", StringComparison.Ordinal);
            if (under && (best is null || point.Length > best.Path.TrimEnd('/').Length))
            {
                best = mount with { Path = point };
            }
        }

        return best is { } local
            && (local.Type == DriveType.Fixed || (local.Type == DriveType.Ram && local.Format == "tmpfs"));
    }

    // A link's target as written (null for anything else, or a path that does not exist), read without following it.
    private static string? ReadLink(string path) => new FileInfo(path).LinkTarget;

    private static void Push(Stack<string> pending, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            pending.Push(parts[i]);
        }
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }
}
