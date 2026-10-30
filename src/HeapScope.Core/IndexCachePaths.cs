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

namespace HeapScope.Core;

/// <summary>
/// Where a snapshot's index cache and its sidecars are (ARCHITECTURE.md 7; sprint 3.4 moved the rule here so deleting a
/// snapshot finds them): next to the snapshot (<c>app.dmp.hsidx</c>, sidecars <c>app.dmp.rev.hsidx</c>) or under the
/// fallback root, named after a hash of the snapshot's full path.
/// </summary>
public static class IndexCachePaths
{
    /// <summary>The index cache file extension.</summary>
    public const string Extension = ".hsidx";

    /// <summary>The reverse references sidecar (sprint 2.0).</summary>
    public const string ReverseKind = "rev";

    /// <summary>The dominator tree sidecar (sprint 2.1).</summary>
    public const string DominatorKind = "dom";

    /// <summary>Every sidecar kind; a deletion removes exactly these (sprint 3.4 review: never a sibling dump's cache).</summary>
    public static IReadOnlyList<string> SidecarKinds { get; } = [ReverseKind, DominatorKind];

    /// <summary>The fallback folder for snapshots in folders HeapScope cannot write.</summary>
    public static string DefaultFallbackRoot => HeapScopeFolders.Default.Cache;

    /// <summary>The cache next to the snapshot.</summary>
    public static string Beside(string snapshotPath) => Path.GetFullPath(snapshotPath) + Extension;

    /// <summary>The cache in the fallback root.</summary>
    public static string InFallback(string snapshotPath, string fallbackRoot)
    {
        var full = Path.GetFullPath(snapshotPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
        return Path.Combine(fallbackRoot, hash + Extension);
    }

    /// <summary>A sidecar of an optional index next to the cache at <paramref name="cachePath"/>.</summary>
    public static string Sidecar(string cachePath, string kind) => Path.ChangeExtension(cachePath, "." + kind + Extension);

    /// <summary>
    /// Every cache file of a snapshot that exists: the base cache and its known sidecars, next to it and in the fallback
    /// root, and the writer's temporary files of each (<c>&lt;cache&gt;.&lt;32 hex digits&gt;.tmp</c>).
    /// </summary>
    public static IReadOnlyList<string> Existing(string snapshotPath, string? fallbackRoot = null)
    {
        var found = new List<string>();
        foreach (var cache in new[] { Beside(snapshotPath), InFallback(snapshotPath, fallbackRoot ?? DefaultFallbackRoot) })
        {
            var folder = Path.GetDirectoryName(cache)!;
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in SidecarKinds.Select(kind => Sidecar(cache, kind)).Prepend(cache))
            {
                if (File.Exists(file))
                {
                    found.Add(file);
                }

                var name = Path.GetFileName(file);
                found.AddRange(Directory.EnumerateFiles(folder, name + ".*.tmp").Where(t => IsTemporary(Path.GetFileName(t), name)));
            }
        }

        return found;
    }

    private static bool IsTemporary(string file, string cacheName)
    {
        var middle = file.AsSpan(cacheName.Length + 1, file.Length - cacheName.Length - 1 - ".tmp".Length);
        return middle.Length == 32 && !middle.ContainsAnyExcept("0123456789abcdefABCDEF");
    }
}
