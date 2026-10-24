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

using System.IO.Hashing;

namespace HeapScope.Storage;

/// <summary>
/// Identifies the snapshot file an index cache was built from (ARCHITECTURE.md 7): length, last write time and an
/// XxHash3 of the first and last MiB. A cache whose fingerprint does not match the snapshot is rebuilt.
/// </summary>
public readonly record struct SnapshotFingerprint(long Length, long LastWriteUtcTicks, ulong ContentHash)
{
    private const int SampleBytes = 1024 * 1024;

    public static SnapshotFingerprint Compute(string snapshotPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        var info = new FileInfo(snapshotPath);
        using var stream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = new XxHash3();
        var buffer = new byte[(int)Math.Min(SampleBytes, stream.Length)];
        stream.ReadExactly(buffer);
        hash.Append(buffer);
        if (stream.Length > SampleBytes)
        {
            var tailLength = (int)Math.Min(SampleBytes, stream.Length - SampleBytes);
            var tail = new byte[tailLength];
            stream.Seek(-tailLength, SeekOrigin.End);
            stream.ReadExactly(tail);
            hash.Append(tail);
        }

        return new SnapshotFingerprint(info.Length, info.LastWriteTimeUtc.Ticks, hash.GetCurrentHashAsUInt64());
    }

    /// <summary>Describes the first difference to <paramref name="other"/>, or null when they match.</summary>
    public string? DescribeDifference(SnapshotFingerprint other) =>
        Length != other.Length ? $"the snapshot size changed ({other.Length:N0} bytes, cache built from {Length:N0})"
        : LastWriteUtcTicks != other.LastWriteUtcTicks ? "the snapshot was modified after the cache was built"
        : ContentHash != other.ContentHash ? "the snapshot content differs from the one the cache was built from"
        : null;
}
