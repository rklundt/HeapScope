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
/// A full-dump snapshot (sprint 1.5): the graph plus an <see cref="IObjectReader"/> over the dump it came from, so it
/// answers <see cref="SnapshotCapabilities.FieldValues"/> and <see cref="SnapshotCapabilities.StringContents"/>.
/// <see cref="SnapshotCapabilities.ThreadStacks"/> is still removed: no service answers it yet. The reader is disposed
/// with the graph, after the last lease.
/// </summary>
public sealed class DumpSnapshot : ISnapshot
{
    /// <summary>Capabilities the object reader provides.</summary>
    public const SnapshotCapabilities ReaderCapabilities = SnapshotCapabilities.FieldValues | SnapshotCapabilities.StringContents;

    private readonly LeaseTracker _leases = new();
    private readonly IObjectReader _reader;

    public DumpSnapshot(SnapshotInfo info, SnapshotCapabilities capabilities, HeapGraph graph, IObjectReader reader)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        Capabilities = (capabilities & ~GraphSnapshot.ServiceBackedCapabilities) | ReaderCapabilities;
    }

    public SnapshotInfo Info { get; }

    public SnapshotCapabilities Capabilities { get; }

    public HeapGraph Graph { get; }

    public bool TryGetService<TService>([NotNullWhen(true)] out TService? service)
        where TService : class
    {
        service = _reader as TService;
        return service is not null;
    }

    public SnapshotLease AcquireLease() => _leases.Acquire();

    /// <summary>Refuses new leases; the graph and the reader are released when the last lease is returned.</summary>
    public void Dispose() => _leases.Close(() =>
    {
        Graph.Dispose();
        (_reader as IDisposable)?.Dispose();
    });
}
