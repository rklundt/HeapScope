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

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// Replaces ClrMD's default file locator, which can reach symbol servers, with one that only accepts files that
/// already exist on this machine, on a local fixed drive, at the recorded path and match the requested image (ADR-017).
/// Paths recorded in the dump never lead to a network share.
/// </summary>
internal sealed class LocalOnlyFileLocator : IFileLocator
{
    public static LocalOnlyFileLocator Instance { get; } = new();

    public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties) =>
        DacLocator.IsLocalFixedPath(fileName) && File.Exists(fileName) && DacLocator.Matches(fileName, buildTimeStamp, imageSize) ? fileName : null;

    public string? FindPEImage(string fileName, SymbolProperties archivedUnder, ImmutableArray<byte> buildIdOrUUID, OSPlatform originalPlatform, bool checkProperties) => null;
}
