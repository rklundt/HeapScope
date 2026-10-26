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
/// <param name="LoaderVersion">
/// The <see cref="Core.Loading.ISnapshotLoader.Version"/> of the loader that built the graph (sprint 1.8 review M7): a
/// cache from an older version of the loader's semantics is rebuilt. Optional in the JSON; older caches read 0.
/// </param>
/// <param name="StaticFieldRoots">Static field roots the loader found (sprint 2.3.7: kept so a reopen from the cache can still say it).</param>
/// <param name="IncompleteSegments">Heap segments the loader could not walk completely: why a snapshot is not complete (sprint 2.3.7).</param>
/// <param name="Dac">The data access component that read the dump and why (full dumps, sprint 2.3.7).</param>
public sealed record IndexCacheMetadata(
    SnapshotInfo Info,
    SnapshotCapabilities Capabilities,
    string WriterVersion,
    IReadOnlyList<string>? DebuggableModules = null,
    long UnreadableObjects = 0,
    int LoaderVersion = 0,
    int StaticFieldRoots = 0,
    int IncompleteSegments = 0,
    Core.Loading.DacChoice? Dac = null)
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
