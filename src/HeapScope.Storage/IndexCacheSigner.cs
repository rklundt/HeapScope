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

namespace HeapScope.Storage;

/// <summary>
/// Signs index caches and their sidecars, and checks the signature on open (ADR-036). Every reader and writer takes one
/// explicitly: the worker the user's key (<see cref="ForThisUser"/>), a tool or test that reads only caches it wrote itself
/// <see cref="None"/>, written out, so a missing key can never quietly turn the check off (3.7 review M11). The HMAC-SHA256 of
/// the header region (the header and the section directory, which carries every section's checksum) sits in the
/// region's last 32 bytes.
/// </summary>
public sealed class IndexCacheSigner
{
    internal const int SignatureLength = 32;

    private readonly byte[]? _key;

    private IndexCacheSigner(byte[]? key) => _key = key;

    /// <summary>Signs nothing and accepts any signature: for tools and tests that read only caches they wrote.</summary>
    public static IndexCacheSigner None { get; } = new(null);

    /// <summary>Signs and checks with <paramref name="key"/> (32 bytes).</summary>
    public static IndexCacheSigner WithKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != SignatureLength)
        {
            throw new ArgumentException($"An index cache key has {SignatureLength} bytes.", nameof(key));
        }

        return new([.. key]);
    }

    /// <summary>
    /// This user's key from <paramref name="folder"/>, created on first use. When the key file can be neither read nor
    /// created (a folder of another user, a full disk), a random key for this process: caches are still signed and
    /// checked, only rebuilt on the next run (3.7 review H4). Never <see cref="None"/>.
    /// </summary>
    public static (IndexCacheSigner Signer, IndexCacheKeyOutcome Outcome) ForThisUser(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var path = Path.Combine(folder, IndexCacheKey.FileName);
        try
        {
            var (key, how) = IndexCacheKey.LoadOrCreate(path);
            return (new IndexCacheSigner(key), new IndexCacheKeyOutcome(path, how, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new IndexCacheSigner(RandomNumberGenerator.GetBytes(SignatureLength)), new IndexCacheKeyOutcome(path, IndexCacheKeySource.ThisRunOnly, ex));
        }
    }

    /// <summary>Whether this signer signs and checks (false only for <see cref="None"/>).</summary>
    public bool Signs => _key is not null;

    /// <summary>Writes the signature into the last bytes of <paramref name="region"/>.</summary>
    internal void Sign(Span<byte> region)
    {
        if (_key is not null)
        {
            HMACSHA256.HashData(_key, region[..^SignatureLength], region[^SignatureLength..]);
        }
    }

    /// <summary>True for <see cref="None"/>, or when the region's signature is this key's.</summary>
    internal bool Verify(ReadOnlySpan<byte> region)
    {
        if (_key is null)
        {
            return true;
        }

        Span<byte> expected = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(_key, region[..^SignatureLength], expected);
        return CryptographicOperations.FixedTimeEquals(expected, region[^SignatureLength..]);
    }
}
