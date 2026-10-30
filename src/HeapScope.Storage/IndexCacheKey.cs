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
/// The per-user key that signs index caches and their sidecars (sprint 3.7, peer review P14). A cache's checksums are
/// unkeyed, so anyone can recompute them: an <c>app.dmp.hsidx</c> shipped next to <c>app.dmp</c> (whose time survives
/// unzipping, so the fingerprint matches) decided every count, size and root. With a key set, the writer puts an
/// HMAC-SHA256 of the header region (the header and the section directory, which carries every section's checksum) in the
/// region's last 32 bytes, and the reader refuses a cache whose signature does not match
/// (<see cref="IndexCacheInvalidReason.Untrusted"/>), so it is rebuilt from the dump. Without a key nothing is signed or
/// checked (tests, tools).
/// </summary>
public static class IndexCacheKey
{
    /// <summary>The key file in HeapScope's data folder: 32 random bytes, readable by the user only.</summary>
    public const string FileName = "index-cache.key";

    internal const int SignatureLength = 32;

    private static readonly AsyncLocal<byte[]?> s_scoped = new();
    private static byte[]? s_process;

    /// <summary>The key in effect: a scoped one (tests), else the process's.</summary>
    internal static byte[]? Current => s_scoped.Value ?? s_process;

    /// <summary>
    /// Loads this user's key for the whole process, creating it on first use, from <paramref name="folder"/> (null:
    /// HeapScope's data folder). The worker calls it at startup and logs the outcome. When the key can be neither read nor
    /// created (a folder of another user, a full disk), the process uses a random key of its own instead (3.7 review H4):
    /// caches are still signed and checked, only rebuilt on the next run, and the analysis keeps working. Never no key.
    /// </summary>
    public static IndexCacheKeyOutcome UseForThisUser(string? folder = null)
    {
        var path = Path.Combine(folder ?? HeapScopeFolders.Default.Root, FileName);
        try
        {
            (s_process, var how) = LoadOrCreate(path);
            return new IndexCacheKeyOutcome(path, how, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            s_process = RandomNumberGenerator.GetBytes(SignatureLength);
            return new IndexCacheKeyOutcome(path, IndexCacheKeySource.ThisRunOnly, ex);
        }
    }

    /// <summary>Uses <paramref name="key"/> in the calling flow until the result is disposed (tests).</summary>
    public static IDisposable UseScoped(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var previous = s_scoped.Value;
        s_scoped.Value = key;
        return new Restore(() => s_scoped.Value = previous);
    }

    /// <summary>Writes the signature into the last bytes of <paramref name="region"/> when a key is in effect.</summary>
    internal static void Sign(Span<byte> region)
    {
        if (Current is { } key)
        {
            HMACSHA256.HashData(key, region[..^SignatureLength], region[^SignatureLength..]);
        }
    }

    /// <summary>True when no key is in effect, or the region's signature is the key's.</summary>
    internal static bool Verify(ReadOnlySpan<byte> region)
    {
        if (Current is not { } key)
        {
            return true;
        }

        Span<byte> expected = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(key, region[..^SignatureLength], expected);
        return CryptographicOperations.FixedTimeEquals(expected, region[^SignatureLength..]);
    }

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

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
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

/// <summary>The outcome of <see cref="IndexCacheKey.UseForThisUser"/>: the key file, where the key came from, and the error
/// when it is <see cref="IndexCacheKeySource.ThisRunOnly"/>.</summary>
public sealed record IndexCacheKeyOutcome(string Path, IndexCacheKeySource Source, Exception? Error);
