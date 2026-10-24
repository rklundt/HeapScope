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
using System.Text;
using HeapScope.Core;
using HeapScope.Core.Synthetic;

namespace HeapScope.Storage.Tests;

/// <summary>
/// Hostile and damaged files (sprint 0.5 review): each case patches the directory or a section and then recomputes
/// the checksums, so the reader's shape checks (not the checksums) must catch it. Every case must return a reason in
/// both modes, never throw or read outside the file.
/// </summary>
public sealed class MalformedCacheTests : IDisposable
{
    private const int HeaderSize = 64;
    private const int EntrySize = 40;
    private const int ChecksumOffset = 48;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly string _cache;

    public MalformedCacheTests()
    {
        Directory.CreateDirectory(_directory);
        _cache = Path.Combine(_directory, "cache.hsidx");
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(2_000, 2, TypeCount: 20, RootCount: 5, Seed: 3));
        var metadata = new IndexCacheMetadata(new SnapshotInfo(SnapshotKind.FullDump, "x.dmp"), SnapshotCapabilities.FullDump, "test");
        IndexCacheWriter.Write(_cache, graph, metadata, new SnapshotFingerprint(1, 2, 3), cancellationToken: TestContext.Current.CancellationToken);
    }

    public static TheoryData<string, MemoryMode, IndexCacheInvalidReason> Cases()
    {
        var data = new TheoryData<string, MemoryMode, IndexCacheInvalidReason>();
        foreach (var mode in Enum.GetValues<MemoryMode>())
        {
            data.Add("negative element count", mode, IndexCacheInvalidReason.Malformed);
            data.Add("overflowing offset", mode, IndexCacheInvalidReason.Truncated);
            data.Add("offset past the end", mode, IndexCacheInvalidReason.Truncated);
            data.Add("unknown section id", mode, IndexCacheInvalidReason.Malformed);
            data.Add("repeated section id", mode, IndexCacheInvalidReason.Malformed);
            data.Add("wrong element size", mode, IndexCacheInvalidReason.Malformed);
            data.Add("misaligned offset", mode, IndexCacheInvalidReason.Malformed);
            data.Add("offset inside the header", mode, IndexCacheInvalidReason.Malformed);
            data.Add("too many sections", mode, IndexCacheInvalidReason.HeaderCorrupted);
            data.Add("string count overflow", mode, IndexCacheInvalidReason.Malformed);
            data.Add("null metadata", mode, IndexCacheInvalidReason.Malformed);
            data.Add("header damaged", mode, IndexCacheInvalidReason.HeaderCorrupted);
            data.Add("truncated", mode, IndexCacheInvalidReason.Truncated);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Damaged_or_hostile_cache_returns_a_reason(string damage, MemoryMode mode, IndexCacheInvalidReason expected)
    {
        Damage(damage);

        var result = IndexCacheReader.Open(_cache, null, mode, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(expected, result.Detail);
        result.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Damage(string damage)
    {
        var bytes = File.ReadAllBytes(_cache);
        var count = BitConverter.ToInt32(bytes, 16);
        int EntryOf(IndexSection section)
        {
            for (var i = 0; i < count; i++)
            {
                if (BitConverter.ToInt32(bytes, HeaderSize + (i * EntrySize)) == (int)section)
                {
                    return HeaderSize + (i * EntrySize);
                }
            }

            throw new InvalidOperationException(section.ToString());
        }

        var edges = EntryOf(IndexSection.Edges);
        switch (damage)
        {
            case "negative element count":
                Write(bytes, edges + 24, -1L);
                Write(bytes, edges + 16, -4L);
                break;
            case "overflowing offset":
                Write(bytes, edges + 8, 0x7FFF_FFFF_FFFF_F000L);
                break;
            case "offset past the end":
                Write(bytes, edges + 8, ((bytes.Length / 4096) + 4) * 4096L);
                break;
            case "unknown section id":
                Write(bytes, edges, 99);
                break;
            case "repeated section id":
                Write(bytes, EntryOf(IndexSection.Sizes), (int)IndexSection.Edges);
                break;
            case "wrong element size":
                Write(bytes, edges + 4, 8);
                break;
            case "misaligned offset":
                Write(bytes, edges + 8, BitConverter.ToInt64(bytes, edges + 8) + 4);
                break;
            case "offset inside the header":
                Write(bytes, edges + 8, 0L);
                break;
            case "too many sections":
                Write(bytes, 16, 1000);
                break;
            case "string count overflow":
                ReplaceSection(bytes, EntryOf(IndexSection.Strings), section => Write(section, 0, int.MaxValue - 1));
                break;
            case "null metadata":
                ReplaceSection(bytes, EntryOf(IndexSection.Metadata), section =>
                {
                    var json = Encoding.UTF8.GetBytes("{\"Metadata\":null,\"DroppedReferenceCount\":0,\"DroppedRootCount\":0}");
                    Array.Fill(section, (byte)' ');
                    json.CopyTo(section, 0);
                });
                break;
            case "header damaged":
                bytes[30] ^= 0x55;
                File.WriteAllBytes(_cache, bytes);
                return;
            case "truncated":
                File.WriteAllBytes(_cache, bytes[..^7]);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage));
        }

        RewriteHeaderChecksum(bytes);
        File.WriteAllBytes(_cache, bytes);
    }

    /// <summary>Changes a section's content and fixes its checksum, so only content validation can catch it.</summary>
    private static void ReplaceSection(byte[] file, int entry, Action<byte[]> change)
    {
        var offset = BitConverter.ToInt64(file, entry + 8);
        var length = (int)BitConverter.ToInt64(file, entry + 16);
        var section = file.AsSpan((int)offset, length).ToArray();
        change(section);
        section.CopyTo(file, (int)offset);
        Write(file, entry + 32, XxHash3.HashToUInt64(section));
    }

    private static void RewriteHeaderChecksum(byte[] file)
    {
        var count = Math.Clamp(BitConverter.ToInt32(file, 16), 0, (4096 - HeaderSize) / EntrySize);
        Write(file, ChecksumOffset, 0UL);
        Write(file, ChecksumOffset, XxHash3.HashToUInt64(file.AsSpan(0, HeaderSize + (count * EntrySize))));
    }

    private static void Write(byte[] target, int offset, int value) => BitConverter.GetBytes(value).CopyTo(target, offset);

    private static void Write(byte[] target, int offset, long value) => BitConverter.GetBytes(value).CopyTo(target, offset);

    private static void Write(byte[] target, int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(target, offset);
}
