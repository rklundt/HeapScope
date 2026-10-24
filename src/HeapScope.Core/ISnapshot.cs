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

namespace HeapScope.Core;

/// <summary>
/// One loaded snapshot: metadata, capabilities and the compact object graph (ARCHITECTURE.md 5.1).
/// Capabilities that need the original file are exposed as optional services.
/// </summary>
public interface ISnapshot : IDisposable
{
    SnapshotInfo Info { get; }

    SnapshotCapabilities Capabilities { get; }

    HeapGraph Graph { get; }

    /// <summary>
    /// Takes a lease for the duration of a query (ARCHITECTURE.md 6.5). Spans over the graph must not be used after the
    /// lease is disposed. Disposing the snapshot waits for outstanding leases.
    /// </summary>
    SnapshotLease AcquireLease();

    /// <summary>Returns an optional service (for example a field value reader) when this snapshot provides it.</summary>
    bool TryGetService<TService>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TService? service)
        where TService : class;
}
