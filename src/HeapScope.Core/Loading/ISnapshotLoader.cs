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

namespace HeapScope.Core.Loading;

/// <summary>
/// Turns one kind of snapshot file into a <see cref="LoadResult"/> (sprint 1.8). Fails with
/// <see cref="SnapshotFormatException"/> when the file is not one it can read, with a message that says what to do.
/// </summary>
public interface ISnapshotLoader
{
    /// <summary>The kind of file this loader reads.</summary>
    SnapshotKind Kind { get; }

    /// <summary>
    /// The version of what the loader records (capabilities, names, roots, facts). Bump it when a change makes graphs or
    /// metadata built before it wrong: index caches from an older version are rebuilt (sprint 1.8 review M7).
    /// </summary>
    int Version { get; }

    LoadResult Load(string path, IProgress<LoadProgress>? progress = null, CancellationToken cancellationToken = default);
}
