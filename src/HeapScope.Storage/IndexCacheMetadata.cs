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

using HeapScope.Core;

namespace HeapScope.Storage;

/// <summary>Descriptive data stored in the cache's metadata section (UTF-8 JSON).</summary>
/// <param name="Info">The snapshot's metadata.</param>
/// <param name="Capabilities">What the snapshot can answer (service-backed capabilities need the original file).</param>
/// <param name="WriterVersion">Version of the HeapScope build that wrote the cache.</param>
/// <param name="DebuggableModules">
/// Modules of the captured process compiled as debuggable code (their stack roots can be missing). Added in 1.2 as an
/// optional JSON field: caches written before it read as null, so the format version does not change.
/// </param>
/// <param name="UnreadableObjects">Heap entries the loader skipped because their type could not be read.</param>
public sealed record IndexCacheMetadata(
    SnapshotInfo Info,
    SnapshotCapabilities Capabilities,
    string WriterVersion,
    IReadOnlyList<string>? DebuggableModules = null,
    long UnreadableObjects = 0)
{
    /// <summary>Informational version of this Storage assembly, without source revision.</summary>
    public static string CurrentWriterVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(IndexCacheMetadata).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

/// <summary>The JSON document of the metadata section.</summary>
internal sealed record MetadataDocument(
    IndexCacheMetadata Metadata,
    long DroppedReferenceCount,
    long DroppedRootCount);
