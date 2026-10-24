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

using System.Security.Cryptography;
using System.Text;

namespace HeapScope.Storage;

/// <summary>
/// Decides where a snapshot's index cache lives (ARCHITECTURE.md 7): next to the snapshot
/// (<c>&lt;snapshot&gt;.hsidx</c>) when that folder accepts a new file, otherwise under a fallback root
/// (<c>%LOCALAPPDATA%\HeapScope\cache</c> by default) named after a hash of the snapshot's full path.
/// </summary>
public sealed class IndexCacheLocator
{
    private readonly Func<string, bool> _canWrite;

    public IndexCacheLocator(string? fallbackRoot = null, Func<string, bool>? canWriteDirectory = null)
    {
        FallbackRoot = fallbackRoot ?? DefaultFallbackRoot;
        _canWrite = canWriteDirectory ?? CanWriteDirectory;
    }

    public static string DefaultFallbackRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope", "cache");

    public string FallbackRoot { get; }

    public string GetCachePath(string snapshotPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        var full = Path.GetFullPath(snapshotPath);
        var directory = Path.GetDirectoryName(full)!;
        if (_canWrite(directory))
        {
            return full + IndexCacheFormat.Extension;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
        return Path.Combine(FallbackRoot, hash + IndexCacheFormat.Extension);
    }

    /// <summary>True when a probe file can be created (and is removed) in <paramref name="directory"/>.</summary>
    public static bool CanWriteDirectory(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".heapscope-probe-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
