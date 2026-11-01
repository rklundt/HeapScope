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
using HeapScope.Core;

namespace HeapScope.Storage;

/// <summary>
/// The per-user key file that <see cref="IndexCacheSigner.ForThisUser"/> signs index caches with (sprint 3.7, peer review
/// P14): 32 random bytes in HeapScope's data folder, readable by the user only, created on first use under a lock file so
/// processes starting together share one key.
/// </summary>
public static class IndexCacheKey
{
    /// <summary>The key file in HeapScope's data folder.</summary>
    public const string FileName = "index-cache.key";

    internal const int SignatureLength = IndexCacheSigner.SignatureLength;

    internal static (byte[] Key, IndexCacheKeySource How) LoadOrCreate(string path)
    {
        if (TryRead(path, out _) is { } key)
        {
            return (key, IndexCacheKeySource.Loaded);
        }

        // Missing or damaged: created under a lock file, so processes starting together end with one key. A move that
        // must not overwrite is not atomic on every system (macOS), so it cannot settle the race alone (3.7 CI).
        PrivateFiles.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        using (Lock(path + ".lock"))
        {
            if (TryRead(path, out var damaged) is { } other)
            {
                return (other, IndexCacheKeySource.Loaded);
            }

            var created = RandomNumberGenerator.GetBytes(SignatureLength);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = PrivateFiles.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(created);
                }

                File.Move(temp, path, overwrite: true);
                return (created, damaged ? IndexCacheKeySource.ReplacedDamaged : IndexCacheKeySource.Created);
            }
            finally
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The key in the file, or null when it is missing or has the wrong length (<paramref name="damaged"/>).</summary>
    private static byte[]? TryRead(string path, out bool damaged)
    {
        damaged = false;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == SignatureLength)
            {
                return bytes;
            }

            damaged = true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }

        return null;
    }

    /// <summary>Holds <paramref name="path"/> open exclusively (a lock across processes), waiting up to 10 seconds.</summary>
    private static FileStream Lock(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return PrivateFiles.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 500)
            {
                Thread.Sleep(20);
            }
        }
    }
}

/// <summary>Where the process's index cache key came from (3.7 review H4).</summary>
public enum IndexCacheKeySource
{
    /// <summary>Read from the key file.</summary>
    Loaded,

    /// <summary>The key file did not exist: a new key was written (every existing cache is rebuilt once).</summary>
    Created,

    /// <summary>The key file had the wrong length: a new key replaced it (every existing cache is rebuilt once).</summary>
    ReplacedDamaged,

    /// <summary>The key file could be neither read nor written: this process uses a random key of its own.</summary>
    ThisRunOnly,
}

/// <summary>The outcome of <see cref="IndexCacheSigner.ForThisUser"/>: the key file, where the key came from, and the error
/// when it is <see cref="IndexCacheKeySource.ThisRunOnly"/>.</summary>
public sealed record IndexCacheKeyOutcome(string Path, IndexCacheKeySource Source, Exception? Error);
