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
/// Collections a static field holds directly that only grow (sprint 2.6): with a baseline snapshot, those that grew by at
/// least <see cref="MinGrowth"/> elements and <see cref="MinGrowthShare"/>; without one, those of at least
/// <see cref="MinLarge"/> elements (large; growth unknown). Matched across snapshots by the static field's name.
/// </summary>
internal sealed class GrowingStaticCollectionsDetector : IInspection
{
    public const long MinGrowth = 1_000;
    public const double MinGrowthShare = 0.10;
    public const long MinLarge = 10_000;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Field, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Type, InspectionColumnKind.Text),
        new(InspectionColumns.Count, InspectionColumnKind.Integer),
        new(InspectionColumns.Baseline, InspectionColumnKind.Integer),
        new(InspectionColumns.Growth, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.Address, InspectionColumnKind.Address),
    ];

    public string Name => "growing-static-collections";

    public string Title => "Growing static collections";

    public string Cause => "A collection held by a static field only grows: what is added is never removed, so it all stays alive for the life of the process.";

    public string Fix => "Bound it: evict old entries, cap its size or use a cache with expiration (MemoryCache), or remove entries when their work is done.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues | SnapshotCapabilities.RootDetails;

    public InspectionKind Kind => InspectionKind.Leak;

    public bool UsesBaseline => true;

    /// <summary>The collections counted, by their generic definition's name (the rest are listed as not counted).</summary>
    internal const string Counted = "List,Queue,Stack,ArrayList,Dictionary,HashSet,Hashtable,ConcurrentDictionary,reference arrays";

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinGrowth={MinGrowth};MinGrowthShare={MinGrowthShare:P0};MinLargeWithoutBaseline={MinLarge};Counted={Counted}");

    public InspectionResult Run(InspectionContext context)
    {
        var ct = context.CancellationToken;
        var skipped = new Exclusions();
        var current = Collections(context.Graph, Readers.Of(context), ct, skipped);

        // The worker refuses a baseline it cannot read (review H8): one without a reader here is a caller's mistake.
        var baseline = context.Baseline is { } b
            ? Collections(b.Graph, b.Reader ?? throw new InvalidOperationException("The baseline snapshot has no field values; choose a full dump."), ct, new Exclusions())
            : null;
        var rows = new List<Row>();
        var nearMisses = new List<Row>();
        foreach (var (field, found) in current)
        {
            if (baseline is null)
            {
                // Without growth to go on, the runtime's and the framework's own large tables are by design.
                if (found.Count >= MinLarge && DetectorGraph.IsFramework(field))
                {
                    skipped.Add("framework static without a baseline", field);
                }
                else if (found.Count >= MinLarge)
                {
                    rows.Add(new Row(field, found, null, null));
                }

                continue;
            }

            var before = baseline.TryGetValue(field, out var then) ? then.Count : 0;
            var growth = found.Count - before;
            if (growth >= MinGrowth && growth >= MinGrowthShare * Math.Max(before, 1))
            {
                rows.Add(new Row(field, found, before, growth));
            }
            else if (growth > 0)
            {
                nearMisses.Add(new Row(field, found, before, growth));
            }
        }

        var ordered = rows.OrderByDescending(r => r.Growth ?? r.Collection.Count).ThenBy(r => r.Field, StringComparer.Ordinal).ToArray();
        var note = baseline is null
            ? string.Create(CultureInfo.InvariantCulture, $"No baseline snapshot: these are the application's static collections of at least {MinLarge:N0} elements (the framework's own are left out); whether they grow is unknown. Choose an earlier snapshot of the same process as the baseline (in the CLI, --baseline) to see growth.")
            : BaselineWarning(context.Info, context.Baseline!.Info);
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{current.Count} static collections counted{(baseline is null ? string.Empty : $", {baseline.Count} in the baseline")}; {skipped}; reported: {Describe(ordered)}; largest growth below the thresholds: {Describe(nearMisses.OrderByDescending(r => r.Growth).Take(5))}");
        return new InspectionResult(
            ordered.Length,
            ordered.Sum(r => r.Collection.Bytes),
            null,
            note,
            new RowTable<Row>(s_columns, ordered, 120, (row, cells) =>
            {
                cells[0] = row.Field;
                cells[1] = row.Collection.Type;
                cells[2] = InspectionRows.Number(row.Collection.Count);
                cells[3] = row.Before is { } before ? InspectionRows.Number(before) : null;
                cells[4] = row.Growth is { } growth ? InspectionRows.Number(growth) : null;
                cells[5] = InspectionRows.Number(row.Collection.Bytes);
                cells[6] = InspectionRows.Hex(row.Collection.Address);
            }),
            basis);
    }

    /// <summary>Why a growth comparison may mislead (review M8): a baseline captured later, or from another process.</summary>
    internal static string? BaselineWarning(SnapshotInfo current, SnapshotInfo baseline)
    {
        if (current.CapturedAtUtc is { } now && baseline.CapturedAtUtc is { } then && then > now)
        {
            return "The baseline was captured after this snapshot, so what grew shows as shrinking and is not reported. Choose an earlier snapshot as the baseline.";
        }

        if (current.ProcessId is { } pid && baseline.ProcessId is { } basePid && (pid != basePid || current.ProcessName != baseline.ProcessName))
        {
            return string.Create(CultureInfo.InvariantCulture, $"The baseline is from another process ({baseline.ProcessName} {basePid}; this one is {current.ProcessName} {pid}): growth compares two runs, not one process over time.");
        }

        return null;
    }

    private static string Describe(IEnumerable<Row> rows)
    {
        var text = string.Join("; ", rows.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Field} {r.Collection.Type} {r.Collection.Count}{(r.Before is { } before ? $" (was {before})" : string.Empty)}")));
        return text.Length == 0 ? "none" : text;
    }

    /// <summary>The collections static fields hold, by "DeclaringType.field", with their element counts.</summary>
    internal static Dictionary<string, Collection> Collections(HeapGraph graph, IObjectReader reader, CancellationToken cancellationToken, Exclusions skipped)
    {
        var found = new Dictionary<string, Collection>(StringComparer.Ordinal);
        foreach (var root in graph.Roots.Roots.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.Kind != RootKind.StaticField || graph.Strings[root.DetailId] is not { Length: > 0 } field)
            {
                continue;
            }

            var obj = root.ObjectIndex;
            var type = graph.Types.GetName(graph.TypeIndices[obj]);
            if (found.ContainsKey(field))
            {
                skipped.Add("field name seen twice", field);
            }
            else if (Count(graph, reader, obj, type, cancellationToken) is { } count)
            {
                found[field] = new Collection(type, count, Bytes(graph, reader, obj, type), graph.Addresses[obj]);
            }
            else if (type.Contains("Collection", StringComparison.Ordinal) || type.Contains("Concurrent", StringComparison.Ordinal) || type.Contains("Linked", StringComparison.Ordinal) || type.Contains("Sorted", StringComparison.Ordinal))
            {
                // A collection the detector does not count (Backlog): named, so its absence is explained.
                var open = type.IndexOf('<', StringComparison.Ordinal);
                skipped.Add("collection type not counted", open < 0 ? type : type[..open]);
            }
        }

        return found;
    }

    /// <summary>The collection object and the arrays it holds directly; a ConcurrentDictionary's through its tables (review M10).</summary>
    private static long Bytes(HeapGraph graph, IObjectReader reader, int obj, string type)
    {
        var bytes = (long)graph.GetSize(obj);
        foreach (var child in graph.GetReferences(obj))
        {
            if (InspectionRows.IsArray(graph, child))
            {
                bytes += (long)graph.GetSize(child);
            }
        }

        if (type.StartsWith("System.Collections.Concurrent.ConcurrentDictionary<", StringComparison.Ordinal)
            && Fields.Reference(reader, graph.Addresses[obj], "_tables._buckets") is { } buckets
            && graph.TryGetIndex(buckets.Address, out var index))
        {
            bytes += (long)graph.GetSize(index);
        }

        return bytes;
    }

    /// <summary>
    /// A collection's element count from its own fields; null for an object that is not a known collection. Lists,
    /// queues, stacks, dictionaries, sets, hash tables, concurrent dictionaries and reference arrays are counted; the
    /// other collections are not (Backlog).
    /// </summary>
    private static long? Count(HeapGraph graph, IObjectReader reader, int obj, string type, CancellationToken cancellationToken)
    {
        var address = graph.Addresses[obj];
        if (InspectionRows.IsArray(graph, obj))
        {
            // An array of values holds no objects: only reference arrays are collections that keep things alive.
            return graph.Types.Kinds[graph.TypeIndices[obj]].HasFlag(TypeKind.ReferenceArray) ? InspectionRows.ArrayLength(graph, obj) : null;
        }

        var open = type.IndexOf('<', StringComparison.Ordinal);
        var name = open < 0 ? type : type[..open];
        switch (name)
        {
            case "System.Collections.Generic.List":
            case "System.Collections.Generic.Queue":
            case "System.Collections.Generic.Stack":
            case "System.Collections.ArrayList":
                return Fields.Integer(reader, address, RuntimeLayouts.Collections.Size);
            case "System.Collections.Generic.Dictionary":
            case "System.Collections.Generic.HashSet":
                return Fields.Integer(reader, address, RuntimeLayouts.Collections.Count) is { } count ? count - (Fields.Integer(reader, address, RuntimeLayouts.Collections.FreeCount) ?? 0) : null;
            case "System.Collections.Hashtable":
                return Fields.Integer(reader, address, RuntimeLayouts.Collections.Count);
            case "System.Collections.Concurrent.ConcurrentDictionary":
                if (Fields.Reference(reader, address, "_tables._countPerLock") is not { } counts)
                {
                    return null;
                }

                long total = 0;
                foreach (var element in reader.ReadFields(counts.Address, 4096, 0, cancellationToken))
                {
                    total += element.Value.Kind == ObjectValueKind.Integer ? element.Value.Integer : 0;
                }

                return total;
            default:
                return null;
        }
    }

    internal sealed record Collection(string Type, long Count, long Bytes, ulong Address);

    private sealed record Row(string Field, Collection Collection, long? Before, long? Growth);
}
