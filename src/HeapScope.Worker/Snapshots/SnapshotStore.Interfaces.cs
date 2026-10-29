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

using System.Diagnostics;
using HeapScope.Analysis;
using HeapScope.Contracts;
using HeapScope.Core;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Snapshots;

/// <summary>The interfaces of each snapshot's types (sprint 3.2, ADR-031).</summary>
internal sealed partial class SnapshotStore
{
    /// <summary>
    /// Each type's interfaces, read once from the dump and kept with the snapshot (the 3.2 review: a derived slot with its
    /// own lease, shared progress and cancellation, dropped on close). Returns null when the snapshot is not loaded or
    /// closes meanwhile. The caller checks the snapshot has a dump reader.
    /// </summary>
    public async Task<TypeInterfaces?> GetInterfacesAsync(Guid snapshotId, IObjectReader reader, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            return null;
        }

        using var subscription = entry.Derived.InterfaceProgress.Subscribe(progress);
        using var waiter = entry.Derived.JoinInterfaces(lifetime => Task.Run(
            () =>
            {
                using var lease = entry.Snapshot.AcquireLease();
                var graph = entry.Snapshot.Graph;
                var stopwatch = Stopwatch.StartNew();
                const string phase = "Reading the types' interfaces";
                var throttled = new Rpc.ThrottledProgress<JobProgress>(entry.Derived.InterfaceProgress);
                var failures = new List<(int Type, string Reason)>();
                var table = TypeInterfaces.Read(graph, reader, failures, new PhaseProgress(throttled, phase, graph.Types.Count), lifetime);
                throttled.Flush();

                // The types left unread are named (metadata), so a row of the grid can be explained (the 3.2 review).
                logger.LogInformation(
                    "Interfaces of snapshot {Id}: {Types} type entries, {Unread} not read in {Seconds:0.00} s{Failures}",
                    entry.Descriptor.SnapshotId,
                    graph.Types.Count,
                    table.UnreadTypes,
                    stopwatch.Elapsed.TotalSeconds,
                    failures.Count == 0 ? string.Empty : "; not read: " + string.Join(", ", failures.Take(20).Select(f => $"{graph.Types.GetName(f.Type)} ({f.Reason})")) + (failures.Count > 20 ? $" and {failures.Count - 20} more" : string.Empty));
                return table;
            },
            lifetime));
        try
        {
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Closed before or during the read.
            return null;
        }
    }
}
