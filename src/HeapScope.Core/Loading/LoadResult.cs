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

using HeapScope.Core.Graph;

namespace HeapScope.Core.Loading;

/// <summary>The outcome of loading a snapshot file (sprint 1.8: shared by every loader). Owns the graph until it is handed on.</summary>
public sealed class LoadResult(HeapGraph graph, SnapshotInfo info, SnapshotCapabilities capabilities, LoadDiagnostics diagnostics, LoadTimings timings)
{
    public HeapGraph Graph { get; } = graph ?? throw new ArgumentNullException(nameof(graph));

    public SnapshotInfo Info { get; } = info ?? throw new ArgumentNullException(nameof(info));

    public SnapshotCapabilities Capabilities { get; } = capabilities;

    public LoadDiagnostics Diagnostics { get; } = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

    public LoadTimings Timings { get; } = timings ?? throw new ArgumentNullException(nameof(timings));
}
