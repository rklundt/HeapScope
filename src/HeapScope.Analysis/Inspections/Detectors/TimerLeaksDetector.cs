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

using System.Globalization;
using HeapScope.Analysis.Retention;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

/// <summary>
/// Timer leaks (sprint 2.6): at least <see cref="MinTimers"/> periodic timers in the runtime's timer queue whose
/// callbacks belong to one type. The queue holds every started timer, so each keeps its callback's target alive until it
/// is disposed. Only periodic timers in the queue count (review H4): a one-shot timer (<c>Task.Delay</c>,
/// <c>CancelAfter</c>) releases itself when it fires, and a paused or fired one is out of the queue. A
/// <c>System.Timers.Timer</c>'s callback is the timer itself: its <c>Elapsed</c> handlers' targets are counted. A callback
/// on a static method or a lambda cache belongs to the timer's state.
/// </summary>
internal sealed class TimerLeaksDetector : IInspection
{
    public const long MinTimers = 500;

    /// <summary>The runtime's "infinite" due time and period (<c>Timeout.UnsignedInfinite</c>).</summary>
    private const long Infinite = uint.MaxValue;

    private const string StaticMethod = "(static method)";

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Target, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Timers, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.Type, InspectionColumnKind.Text),
        new(InspectionColumns.TypeName, InspectionColumnKind.Text),
        new(InspectionColumns.Module, InspectionColumnKind.Text),
        new(InspectionColumns.Scope, InspectionColumnKind.Text),
    ];

    public string Name => "timer-leaks";

    public string Title => "Timer leaks";

    public string Cause => "Started periodic timers that are never stopped or disposed: the runtime's timer queue keeps each one, and the object its callback belongs to, alive.";

    public string Fix => "Dispose the timer (or stop it) when its owner is done, typically in the owner's Dispose.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public bool UsesReachable => true;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinTimers={MinTimers};Timers=periodic,queued");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var ct = context.CancellationToken;
        var reachable = DetectorGraph.Reachable(context);
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        long active = 0, canceled = 0, paused = 0, oneShot = 0, unreadable = 0;
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, DetectorGraph.TypesNamed(graph, "System.Threading.TimerQueueTimer"), context, "Reading timers"))
        {
            var address = graph.Addresses[obj];
            var (isCanceled, due, period) = (Fields.Boolean(reader, address, RuntimeLayouts.Timers.Canceled), Fields.Integer(reader, address, RuntimeLayouts.Timers.DueTime), Fields.Integer(reader, address, RuntimeLayouts.Timers.Period));
            if (isCanceled is null || due is null || period is null)
            {
                unreadable++;
                continue;
            }

            if (isCanceled.Value)
            {
                canceled++;
                continue;
            }

            if (due == Infinite)
            {
                paused++;
                continue;
            }

            if (period == Infinite)
            {
                oneShot++;
                continue;
            }

            active++;
            var bytes = (long)graph.GetSize(obj);
            var callback = Fields.Reference(reader, address, RuntimeLayouts.Timers.TimerCallback) is { } c ? Fields.DelegateTarget(reader, c.Address) : null;
            string kind;
            List<(ulong Address, string Type)?> found;
            var outerTimer = -1;
            if (callback is { Type: "System.Timers.Timer" } timer)
            {
                kind = "System.Timers.Timer";
                found = Fields.Reference(reader, timer.Address, RuntimeLayouts.Timers.OnIntervalElapsed) is { } elapsed ? Fields.DelegateTargets(reader, elapsed.Address, 64, ct) : [];
                if (graph.TryGetIndex(timer.Address, out var outer))
                {
                    bytes += (long)graph.GetSize(outer);
                    outerTimer = outer;
                }
            }
            else
            {
                // A static callback or a lambda cache names no owner: the state passed to the timer does (review H4).
                kind = "System.Threading.Timer";
                found = [callback is { } own && !DetectorGraph.IsCompilerGenerated(own.Type) ? own : Fields.Reference(reader, address, RuntimeLayouts.Timers.State) ?? callback];
            }

            // Each timer once per owner type, however many handlers it has.
            owners.Clear();
            foreach (var owner in found)
            {
                var type = owner?.Type ?? StaticMethod;
                if (!owners.Add(type))
                {
                    continue;
                }

                var entry = targets.GetValueOrDefault(type) ?? new Target(kind, owner is null ? string.Empty : ModuleOf(graph, owner.Value));
                entry.Timers++;
                entry.Bytes += bytes;
                entry.Members.Add(obj);
                if (outerTimer >= 0)
                {
                    entry.Members.Add(outerTimer);
                }
                targets[type] = entry;
            }
        }

        var rows = targets
            .Where(t => t.Value.Timers >= MinTimers)
            .Select(t => new DetectorTypeRow(t.Key, t.Value.Kind, t.Value.Timers, t.Value.Bytes, t.Key == StaticMethod ? string.Empty : t.Key, t.Value.Module, [.. t.Value.Members]))
            .OrderByDescending(r => r.Objects).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{active} periodic timers in the queue, {canceled} canceled, {paused} paused or fired, {oneShot} one-shot, {unreadable} unreadable; by callback owner: {string.Join("; ", targets.OrderByDescending(t => t.Value.Timers).Take(Math.Max(10, rows.Length)).Select(t => $"{t.Key} {t.Value.Timers} ({t.Value.Kind})"))}");
        return new InspectionResult(
            rows.Length,
            rows.Sum(r => r.Bytes),
            null,
            null,
            new RowTable<DetectorTypeRow>(s_columns, rows, 120, (row, cells) =>
            {
                cells[0] = row.Key;
                cells[1] = InspectionRows.Number(row.Objects);
                cells[2] = InspectionRows.Number(row.Bytes);
                cells[3] = row.Detail;
                cells[4] = row.TypeName;
                cells[5] = row.Module;
                cells[6] = InspectionScopes.Reachable;
            },
            row => row.Members),
            basis);
    }

    private static string ModuleOf(HeapGraph graph, (ulong Address, string Type) owner) =>
        graph.TryGetIndex(owner.Address, out var index) ? DetectorGraph.Module(graph, graph.TypeIndices[index]) : string.Empty;

    private sealed class Target(string kind, string module)
    {
        public string Kind { get; } = kind;

        public string Module { get; } = module;

        public long Timers { get; set; }

        public long Bytes { get; set; }

        /// <summary>The timers counted (the queue entry and, for a System.Timers.Timer, the timer): what the row opens.</summary>
        public List<int> Members { get; } = [];
    }
}
