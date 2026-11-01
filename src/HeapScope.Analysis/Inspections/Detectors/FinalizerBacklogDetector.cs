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
/// A finalizer backlog (sprint 3.5): at least <see cref="MinReady"/> objects dead and waiting on the finalizer queue. The
/// finalizer thread runs them one at a time; when one blocks or they arrive faster than it runs them, they and everything
/// they reference stay alive. Types with at least <see cref="MinTypeObjects"/> queued objects are rows.
/// </summary>
internal sealed class FinalizerBacklogDetector : IInspection
{
    public const long MinReady = 1_000;
    public const long MinTypeObjects = 100;

    public string Name => "finalizer-backlog";

    public string Title => "Finalizer backlog";

    public string Cause => "Many objects wait for the finalizer thread: each one, and what it references, stays alive until its finalizer runs. A queue that does not drain means the thread is blocked or too slow.";

    public string Fix => "Find what blocks the finalizer thread (its stack in a debugger: a lock, a wait, a call to a single-threaded COM object); dispose these objects and call GC.SuppressFinalize so they need no finalizer.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.RootDetails;

    public SnapshotFacts RequiresFacts => SnapshotFacts.FinalizerRegistrations;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinReady={MinReady};MinTypeObjects={MinTypeObjects}");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var ready = Finalization.ReadyObjects(graph);
        var types = new TypeTally();
        foreach (var obj in ready.Order())
        {
            types.Add(graph.TypeIndices[obj], obj, graph.GetSize(obj));
        }

        long registered = 0;
        var flags = graph.Flags.Span;
        for (var i = 0; i < flags.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (flags[i].HasFlag(ObjectFlags.HasFinalizer))
            {
                registered++;
            }
        }

        var ordered = types.Types.Select(t => (Name: graph.Types.GetName(t.Type), t.Type, t.Members, t.Bytes))
            .OrderByDescending(t => t.Members.Count).ThenByDescending(t => t.Bytes).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        var reported = ready.Count >= MinReady ? ordered.Where(t => t.Members.Count >= MinTypeObjects).ToList() : [];
        var rest = ready.Count >= MinReady ? ordered.Where(t => t.Members.Count < MinTypeObjects).ToList() : [];
        var rows = reported.Select(t => new DetectorTypeRow(t.Name, string.Empty, t.Members.Count, t.Bytes, t.Name, DetectorGraph.Module(graph, t.Type), [.. t.Members])).ToList();
        if (rest.Count > 0)
        {
            // The threshold is met as a whole: types with fewer objects each are one row, never left unreported (the re-review).
            rows.Add(new DetectorTypeRow(string.Create(CultureInfo.InvariantCulture, $"(other types: {rest.Count})"), string.Empty, rest.Sum(t => (long)t.Members.Count), rest.Sum(t => t.Bytes), string.Empty, string.Empty, [.. rest.SelectMany(t => t.Members)]));
        }

        var note = rows.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ready.Count:N0} objects wait for their finalizer. The bytes are the objects' own; what they reference stays alive too until their finalizers have run. A queue can be long just after a large collection: a second dump a few seconds later, compared with this one, shows whether the same objects still wait (survived by address).")
            : null;
        var others = ordered.Skip(reported.Count).ToList();
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{ready.Count} objects on the finalizer queue in {types.Count} types (threshold {MinReady}, rows from {MinTypeObjects} per type); {registered} objects registered for finalization; reported: {Describe(reported)}; others: {others.Count} types with {others.Sum(o => (long)o.Members.Count)} objects, largest {Describe(others.Take(5))}");
        return new InspectionResult(rows.Count, rows.Sum(r => r.Bytes), null, note, DetectorTables.Types([.. rows], null, InspectionScopes.Finalizable), basis);
    }

    private static string Describe(IEnumerable<(string Name, int Type, List<int> Members, long Bytes)> types)
    {
        var text = string.Join("; ", types.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Name} {t.Members.Count} ({t.Bytes} bytes)")));
        return text.Length == 0 ? "none" : text;
    }
}
