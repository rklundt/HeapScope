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
/// Types that grow (sprint 3.5, dotMemory's "types growth"): with a baseline full dump, the application's types whose
/// reachable objects grew by at least <see cref="MinGrowth"/> and by at least <see cref="MinGrowthShare"/> of the
/// baseline's. Types are matched by name and module (ADR-018); the framework's own types (by namespace or module) are left
/// out, so a steady service's pools and caches stay quiet (the growing static collections detector covers those). Full
/// dumps with their complete graph only: a gcdump names nested types differently and may be sampled (review of 3.5). A
/// baseline captured after this snapshot is not compared.
/// </summary>
internal sealed class TypeGrowthDetector : IInspection
{
    public const long MinGrowth = 1_000;
    public const double MinGrowthShare = 0.5;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Type, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Objects, InspectionColumnKind.Integer),
        new(InspectionColumns.Baseline, InspectionColumnKind.Integer),
        new(InspectionColumns.Growth, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.TypeName, InspectionColumnKind.Text),
        new(InspectionColumns.Module, InspectionColumnKind.Text),
        new(InspectionColumns.Scope, InspectionColumnKind.Text),
    ];

    public string Name => "type-growth";

    public string Title => "Growing types";

    public string Cause => "The application keeps more and more objects of these types: more of them are reachable now than in the baseline.";

    public string Fix => "Open the type's objects and their retention paths or Similar Retention: find what holds the new ones, then remove them when their work is done or bound what keeps them.";

    // Root details: a gcdump has none, even a complete one (the re-review), so the worker refuses one on either side.
    public SnapshotCapabilities Requires => SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.CompleteGraph | SnapshotCapabilities.ExactCounts | SnapshotCapabilities.RootDetails;

    public InspectionKind Kind => InspectionKind.Leak;

    public bool UsesReachable => true;

    public bool UsesBaseline => true;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinGrowth={MinGrowth};MinGrowthShare={MinGrowthShare:P0};Framework=left out by namespace or module");

    public InspectionResult Run(InspectionContext context)
    {
        if (context.Baseline is not { } baseline)
        {
            return new InspectionResult(0, 0, null, "Growth needs a baseline: choose an earlier snapshot of the same process (in the CLI, --baseline).", Table([]), "no baseline");
        }

        if (context.Info.Kind != SnapshotKind.FullDump || baseline.Info.Kind != SnapshotKind.FullDump)
        {
            return new InspectionResult(0, 0, null, "Growth by type compares two full dumps: a gcdump names nested types differently and holds survivors without their roots. Choose a full dump of the same process as the baseline.", Table([]), string.Create(CultureInfo.InvariantCulture, $"baseline {baseline.Info.Kind} against {context.Info.Kind}: not compared"));
        }

        if (context.Info.CapturedAtUtc is { } now && baseline.Info.CapturedAtUtc is { } then && then > now)
        {
            return new InspectionResult(0, 0, null, "The baseline was captured after this snapshot, so growth is not compared. Choose an earlier snapshot as the baseline.", Table([]), "baseline captured later: not compared");
        }

        var graph = context.Graph;
        var reachable = DetectorGraph.Reachable(context);
        var (current, currentObjects, currentBytes) = Census(graph, reachable, context, "Counting reachable objects per type");
        var (before, beforeObjects, beforeBytes) = Census(baseline.Graph, DetectorGraph.Reachable(baseline), context, "Counting the baseline's reachable objects per type");
        var rows = new List<Row>();
        var nearMisses = new List<Row>();
        long framework = 0;
        foreach (var (key, count) in current)
        {
            if (IsFramework(key))
            {
                framework++;
                continue;
            }

            var old = before.TryGetValue(key, out var was) ? was : default;
            var growth = count.Objects - old.Objects;
            var row = new Row(key.Name, key.Module, count.Objects, old.Objects, growth, count.Bytes, old.Bytes);
            if (growth >= MinGrowth && growth >= MinGrowthShare * Math.Max(old.Objects, 1))
            {
                rows.Add(row);
            }
            else if (growth > 0)
            {
                nearMisses.Add(row);
            }
        }

        var ordered = rows.OrderByDescending(r => r.Growth).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();

        // The reported types' objects only, each in an array of its census count, filled in one pass (the re-review): a
        // row opens what it counted, with nothing allocated for the types that did not grow.
        if (ordered.Length > 0)
        {
            var byKey = ordered.ToDictionary(r => (r.Name, r.Module), TypeKeyComparer.Type);
            var keys = Keys(graph);
            var owners = new Row?[keys.Length];
            for (var t = 0; t < keys.Length; t++)
            {
                owners[t] = byKey.GetValueOrDefault(keys[t]);
            }

            foreach (var row in ordered)
            {
                row.Members = new int[row.Objects];
            }

            var filled = new Dictionary<Row, int>(ReferenceEqualityComparer.Instance);
            var types = graph.TypeIndices.Span;
            for (var i = 0; i < types.Length; i++)
            {
                if ((i & 0xFFFF) == 0)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    context.Report("Collecting the growing types' objects", i, types.Length);
                }

                if (owners[types[i]] is { } owner && reachable.IsReachable(i))
                {
                    var at = filled.GetValueOrDefault(owner);

                    // Sized from the census of the same graph and reachability, so it fits exactly.
                    owner.Members![at] = i;
                    filled[owner] = at + 1;
                }
            }
        }

        var notes = new List<string>();
        if (ordered.Length > 0)
        {
            notes.Add("Each row's bytes and set are all of the type's reachable objects now, the baseline's included; Compare's new objects (by address) narrow them down. A cache filling up after a start grows by design.");
        }

        if (GrowingStaticCollectionsDetector.BaselineWarning(context.Info, baseline.Info) is { } warning)
        {
            notes.Add(warning);
        }

        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{current.Count} types with {currentObjects} reachable objects ({currentBytes} bytes) now, {before.Count} types with {beforeObjects} ({beforeBytes} bytes) in the baseline; {framework} framework types left out; reported: {Describe(ordered.Take(50))}{(ordered.Length > 50 ? $" and {ordered.Length - 50} more" : string.Empty)}; largest growth below the thresholds: {Describe(nearMisses.OrderByDescending(r => r.Growth).Take(5))}");
        return new InspectionResult(ordered.Length, ordered.Sum(r => r.Bytes), null, notes.Count == 0 ? null : string.Join(" ", notes), Table(ordered), basis);
    }

    /// <summary>The framework's own types: by namespace, or by a System or Microsoft module.</summary>
    private static bool IsFramework((string Name, string Module) key) => FrameworkTypes.IsFramework(key.Name, key.Module);

    private static RowTable<Row> Table(Row[] rows) => new(s_columns, rows, 120, (row, cells) =>
    {
        cells[0] = row.Name;
        cells[1] = InspectionRows.Number(row.Objects);
        cells[2] = InspectionRows.Number(row.Before);
        cells[3] = InspectionRows.Number(row.Growth);
        cells[4] = InspectionRows.Number(row.Bytes);
        cells[5] = row.Name;
        cells[6] = row.Module;
        cells[7] = InspectionScopes.Reachable;
    }, row => row.Members);

    private static string Describe(IEnumerable<Row> rows)
    {
        var text = string.Join("; ", rows.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Name} [{r.Module}] {r.Objects} (was {r.Before}), {r.Bytes} bytes (was {r.BytesBefore})")));
        return text.Length == 0 ? "none" : text;
    }

    private static (string Name, string Module)[] Keys(HeapGraph graph)
    {
        var keys = new (string Name, string Module)[graph.Types.Count];
        for (var t = 0; t < keys.Length; t++)
        {
            keys[t] = (graph.Types.GetName(t), DetectorGraph.Module(graph, t));
        }

        return keys;
    }

    /// <summary>
    /// Reachable objects and bytes per type key: counted per type index in one pass (no hashing per object, review of
    /// 3.5), then folded by key (several type entries can share one).
    /// </summary>
    private static (Dictionary<(string Name, string Module), (long Objects, long Bytes)> Counts, long Objects, long Bytes) Census(HeapGraph graph, Reachability reachable, InspectionContext context, string phase)
    {
        var objects = new long[graph.Types.Count];
        var bytes = new long[graph.Types.Count];
        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report(phase, i, typeIndices.Length);
            }

            if (reachable.IsReachable(i))
            {
                objects[typeIndices[i]]++;
                bytes[typeIndices[i]] += (long)graph.GetSize(i);
            }
        }

        var counts = new Dictionary<(string Name, string Module), (long Objects, long Bytes)>(TypeKeyComparer.Type);
        var keys = Keys(graph);
        for (var t = 0; t < keys.Length; t++)
        {
            if (objects[t] > 0)
            {
                var known = counts.GetValueOrDefault(keys[t]);
                counts[keys[t]] = (known.Objects + objects[t], known.Bytes + bytes[t]);
            }
        }

        return (counts, objects.Sum(), bytes.Sum());
    }

    private sealed record Row(string Name, string Module, long Objects, long Before, long Growth, long Bytes, long BytesBefore)
    {
        /// <summary>The objects, for reported rows only.</summary>
        public int[]? Members { get; set; }
    }
}
