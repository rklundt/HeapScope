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
/// Async methods that never complete (sprint 3.5): at least <see cref="MinPending"/> reachable state machines of one
/// application async method whose task has not ended. An await on a task nothing completes (a TaskCompletionSource never
/// set, a channel never written) keeps the method's state machine, and everything its locals hold, alive. The
/// framework's own methods (a server's connection loops, readers) wait by design and are left out (review of 3.5).
/// </summary>
internal sealed class PendingAsyncMethodsDetector : IInspection
{
    public const long MinPending = 1_000;

    // Task.m_stateFlags: the task ran to completion, faulted or was canceled.
    private const long Ended = 0x1000000 | 0x200000 | 0x400000;

    public string Name => "pending-async-methods";

    public string Title => "Async methods that never complete";

    public string Cause => "Many calls of one async method wait on something that never happens: each waiting call keeps its state machine and what its locals hold alive.";

    public string Fix => "Complete or cancel what the method awaits (set the TaskCompletionSource, pass a CancellationToken, add a timeout), and do not keep tasks of abandoned work.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public bool UsesReachable => true;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinPending={MinPending};Ended=m_stateFlags & 0x1600000;Framework=left out");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var reachable = DetectorGraph.Reachable(context);
        var methods = new string?[graph.Types.Count];
        for (var t = 0; t < methods.Length; t++)
        {
            methods[t] = AsyncMethods.MethodOf(graph.Types.GetName(t));
        }

        var boxes = DetectorGraph.TypeMask(graph, t => methods[t] is not null);
        var boxTypes = boxes.Count(b => b);
        var found = new Dictionary<string, Method>(StringComparer.Ordinal);
        var skipped = new Exclusions();
        long ended = 0, unreadable = 0, scanned = 0;
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, boxes, context, "Reading async state machines"))
        {
            if ((++scanned & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report("Reading async state machines", scanned, 0);
            }

            var type = graph.TypeIndices[obj];

            // Set for every box type above, and obj is one of them.
            var key = methods[type]!;
            var flags = Fields.Integer(reader, graph.Addresses[obj], RuntimeLayouts.Tasks.StateFlags);
            if (flags is null)
            {
                unreadable++;
                continue;
            }

            if ((flags.Value & Ended) != 0)
            {
                ended++;
                continue;
            }

            if (DetectorGraph.IsFramework(key))
            {
                skipped.Add("framework method waiting by design", key);
                continue;
            }

            if (!found.TryGetValue(key, out var method))
            {
                found[key] = method = new Method(graph.Types.GetName(type), DetectorGraph.Module(graph, type));
            }

            method.Members.Add(obj);
            method.Bytes += (long)graph.GetSize(obj);
        }

        var ordered = found.OrderByDescending(m => m.Value.Members.Count).ThenBy(m => m.Key, StringComparer.Ordinal).ToList();
        var reported = ordered.Where(m => m.Value.Members.Count >= MinPending).ToList();
        var rows = reported
            .Select(m => new DetectorTypeRow(m.Key, m.Value.BoxType, m.Value.Members.Count, m.Value.Bytes, m.Value.BoxType, m.Value.Module, [.. m.Value.Members]))
            .ToArray();
        var notes = new List<string>();
        if (rows.Length > 0)
        {
            notes.Add("Calls in flight wait by design (requests being served, background loops): compare these counts with the work in flight when the dump was taken. The bytes are the state machine boxes' own; open a row's retained size to see what their locals hold.");
        }

        if (unreadable > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"The state of {unreadable:N0} state machines could not be read, so they are not counted."));
        }

        var pending = found.Sum(m => (long)m.Value.Members.Count);
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{boxTypes} state machine box types; {pending} reachable application async calls waiting in {found.Count} methods, {ended} ended but still referenced, {unreadable} unreadable; {skipped}; reported: {Describe(reported)}; largest below the threshold: {Describe(ordered.Skip(reported.Count).Take(5))}; {Reachability.Describe(reachable)}");
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, notes.Count == 0 ? null : string.Join(" ", notes), DetectorTables.Types(rows, "StateMachine"), basis);
    }

    private static string Describe(IEnumerable<KeyValuePair<string, Method>> methods)
    {
        var text = string.Join("; ", methods.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Key} {m.Value.Members.Count} ({m.Value.Bytes} bytes)")));
        return text.Length == 0 ? "none" : text;
    }

    private sealed class Method(string boxType, string module)
    {
        public string BoxType { get; } = boxType;

        public string Module { get; } = module;

        public List<int> Members { get; } = [];

        public long Bytes { get; set; }
    }
}
