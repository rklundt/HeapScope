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

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HeapScope.Core.Graph;

namespace HeapScope.Storage;

/// <summary>
/// Writes a <see cref="HeapGraph"/> to an index cache file (ARCHITECTURE.md 7) through an
/// <see cref="IndexCacheFileWriter"/>: sections first (page aligned, in the in-memory layout, each with an XxHash3
/// checksum), then the header last, then it is flushed to disk and renamed over the target. A process that dies at any
/// point leaves either the previous cache or a temporary file without a valid header, never a file that opens.
/// </summary>
public static class IndexCacheWriter
{
    /// <summary>Temporary files older than this are considered left over by a crashed writer.</summary>
    public static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromMinutes(10);

    private static readonly AsyncLocal<Action<string>?> s_beforeHeaderForTesting = new();

    /// <summary>
    /// Test hook: runs after all sections are written and before the header is written (base caches and sidecars).
    /// Flow-local, so it only affects writes started by the test that sets it (tests run in parallel).
    /// </summary>
    internal static Action<string>? BeforeHeaderForTesting
    {
        get => s_beforeHeaderForTesting.Value;
        set => s_beforeHeaderForTesting.Value = value;
    }

    /// <summary>
    /// Writes <paramref name="graph"/> to <paramref name="path"/> and returns the cache's identity (sprint 2.1: sidecars
    /// record it to name the base cache they extend). Progress reports bytes written. Throws
    /// <see cref="IndexCacheInUseException"/> when the existing cache is open (for example mapped); the previous cache
    /// is then kept and no temporary file remains.
    /// </summary>
    public static ulong Write(
        string path,
        HeapGraph graph,
        IndexCacheMetadata metadata,
        SnapshotFingerprint fingerprint,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(metadata);

        using var writer = IndexCacheFileWriter.Create(path, progress, cancellationToken);
        writer.WriteSection(IndexSection.Addresses, graph.Addresses.Span);
        writer.WriteSection(IndexSection.TypeIndices, graph.TypeIndices.Span);
        writer.WriteSection(IndexSection.Sizes, graph.Sizes.Span);
        writer.WriteSection(IndexSection.HeapKinds, graph.HeapKinds.Span);
        writer.WriteSection(IndexSection.ObjectFlags, graph.Flags.Span);
        writer.WriteSection(IndexSection.EdgeStarts, graph.EdgeStarts.Span);
        writer.WriteSection(IndexSection.Edges, graph.Edges.Span);
        writer.WriteSection(IndexSection.LargeSizes, graph.LargeSizes.Span);
        writer.WriteSection(IndexSection.Roots, graph.Roots.Roots.Span);
        writer.WriteSection(IndexSection.TypeNameIds, graph.Types.NameIds.Span);
        writer.WriteSection(IndexSection.TypeMethodTables, graph.Types.MethodTables.Span);
        writer.WriteSection(IndexSection.TypeBaseSizes, graph.Types.BaseSizes.Span);
        writer.WriteSection(IndexSection.TypeComponentSizes, graph.Types.ComponentSizes.Span);
        writer.WriteSection(IndexSection.TypeModuleIds, graph.Types.ModuleIds.Span);
        writer.WriteSection(IndexSection.TypeKinds, graph.Types.Kinds.Span);
        writer.WriteSection<byte>(IndexSection.Strings, EncodeStrings(graph.Strings));
        writer.WriteSection(IndexSection.DependentHandles, graph.DependentHandles.Span);
        writer.WriteSection<byte>(IndexSection.Metadata, JsonSerializer.SerializeToUtf8Bytes(
            new MetadataDocument(metadata, graph.DroppedReferenceCount, graph.DroppedRootCount)));
        return writer.Commit(fingerprint);
    }

    /// <summary>Encodes strings as: count (int32), count + 1 byte offsets (int32), UTF-8 bytes.</summary>
    internal static byte[] EncodeStrings(StringTable strings)
    {
        var encoded = new byte[strings.Count][];
        var total = 0;
        for (var i = 0; i < strings.Count; i++)
        {
            encoded[i] = Encoding.UTF8.GetBytes(strings[i]!);
            total += encoded[i].Length;
        }

        var result = new byte[sizeof(int) * (strings.Count + 2) + total];
        var span = result.AsSpan();
        MemoryMarshal.Write(span, strings.Count);
        var offset = 0;
        var data = sizeof(int) * (strings.Count + 2);
        for (var i = 0; i < strings.Count; i++)
        {
            MemoryMarshal.Write(span[(sizeof(int) * (i + 1))..], offset);
            encoded[i].CopyTo(span[(data + offset)..]);
            offset += encoded[i].Length;
        }

        MemoryMarshal.Write(span[(sizeof(int) * (strings.Count + 1))..], offset);
        return result;
    }
}
