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

using System.Diagnostics.CodeAnalysis;
using HeapScope.Core.Graph;

namespace HeapScope.Core;

/// <summary>
/// An <see cref="ISnapshot"/> over a graph with no optional services (synthetic graphs, tests, the worker's
/// diagnostic jobs). Capabilities that need a service (<see cref="ServiceBackedCapabilities"/>) are removed, so a
/// snapshot never claims what it cannot answer (ADR-003). A cache-backed full dump needs its own
/// <see cref="ISnapshot"/> that reopens the dump for those services.
/// </summary>
public sealed class GraphSnapshot : ISnapshot
{
    /// <summary>Capabilities that are only real when the snapshot provides the matching service.</summary>
    public const SnapshotCapabilities ServiceBackedCapabilities =
        SnapshotCapabilities.FieldValues | SnapshotCapabilities.StringContents | SnapshotCapabilities.ThreadStacks;

    private readonly LeaseTracker _leases = new();

    public GraphSnapshot(SnapshotInfo info, SnapshotCapabilities capabilities, HeapGraph graph)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Capabilities = capabilities & ~ServiceBackedCapabilities;
    }

    public SnapshotInfo Info { get; }

    public SnapshotCapabilities Capabilities { get; }

    public HeapGraph Graph { get; }

    public bool TryGetService<TService>([NotNullWhen(true)] out TService? service)
        where TService : class
    {
        service = null;
        return false;
    }

    public SnapshotLease AcquireLease() => _leases.Acquire();

    /// <summary>
    /// Refuses new leases and releases the graph (and any mapped view it owns) once the last lease is returned. Never
    /// blocks or throws; safe to call more than once and from several threads.
    /// </summary>
    public void Dispose() => _leases.Close(Graph.Dispose);
}
