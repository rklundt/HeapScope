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

namespace HeapScope.Core;

/// <summary>
/// Paths recorded in a snapshot (module paths, the runtime's folder), which come from the system that took it, not the
/// one reading it (sprint 2.3.5): a Windows path read on Linux keeps its backslashes, so <see cref="Path.GetFileName(string)"/>
/// would return it whole.
/// </summary>
public static class RecordedPath
{
    private static readonly char[] s_separators = ['\\', '/'];

    /// <summary>The part after the last backslash or slash, whichever system wrote the path.</summary>
    public static string FileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path[(path.LastIndexOfAny(s_separators) + 1)..];
    }
}
