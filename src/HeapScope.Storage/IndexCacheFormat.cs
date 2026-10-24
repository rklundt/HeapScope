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

namespace HeapScope.Storage;

/// <summary>Constants of the index cache file format (ARCHITECTURE.md 7). Any layout change bumps <see cref="Version"/>.</summary>
public static class IndexCacheFormat
{
    /// <summary>Current format version.</summary>
    public const int Version = 1;

    /// <summary>"HSIDX" followed by three zero bytes, little-endian.</summary>
    public const ulong Magic = 0x0000_0058_4449_5348;

    /// <summary>Size of the header region (fixed header plus section directory) and the section alignment.</summary>
    public const int PageSize = 4096;

    /// <summary>File name extension of index caches.</summary>
    public const string Extension = ".hsidx";

    internal static long Align(long offset) => (offset + PageSize - 1) & ~(long)(PageSize - 1);
}

/// <summary>Sections of an index cache. Values are persisted and never change.</summary>
public enum IndexSection
{
    Addresses = 1,
    TypeIndices = 2,
    Sizes = 3,
    HeapKinds = 4,
    ObjectFlags = 5,
    EdgeStarts = 6,
    Edges = 7,
    LargeSizes = 8,
    Roots = 9,
    TypeNameIds = 10,
    TypeMethodTables = 11,
    TypeBaseSizes = 12,
    TypeComponentSizes = 13,
    TypeModuleIds = 14,
    TypeKinds = 15,
    Strings = 16,
    Metadata = 17,
}

/// <summary>The fixed part of the header, at offset 0. 64 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct FileHeader
{
    public ulong Magic;
    public int FormatVersion;
    public int HeaderRegionLength;
    public int SectionCount;
    public int Reserved;
    public long SourceLength;
    public long SourceLastWriteUtcTicks;
    public ulong SourceContentHash;

    /// <summary>XxHash3 over the header (with this field zero) and the section directory.</summary>
    public ulong HeaderChecksum;
    public ulong Reserved2;
}

/// <summary>One section directory entry, following the header. 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct SectionEntry
{
    public int Id;
    public int ElementSize;
    public long Offset;
    public long ByteLength;
    public long ElementCount;
    public ulong Checksum;
}
