using HeapScope.Core.Graph;

namespace HeapScope.Analysis;

/// <summary>What summary rows are grouped by (mirrored on the wire by Contracts' <c>TypeGrouping</c>).</summary>
public enum GroupKey
{
    Type = 0,
    Namespace = 1,
    Assembly = 2,
}

/// <summary>
/// One summary row: a type, namespace or assembly with its totals. Type rows also carry their raw type name and module
/// file name (empty when none), the key that opens the row's instances (sprint 1.5).
/// </summary>
public sealed record SummaryRow(string Name, long Count, ulong Bytes, ulong[] BytesByKind, string? TypeName = null, string? Module = null);

/// <summary>Builds summary rows from a <see cref="TypeAggregation"/> (sprint 1.3).</summary>
public static class TypeGroups
{
    /// <summary>
    /// Rows for every present type (one per type table entry, never merged by name), or merged per namespace or
    /// assembly.
    /// </summary>
    public static List<SummaryRow> Build(HeapGraph graph, TypeAggregation aggregation, GroupKey groupBy)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(aggregation);
        if (groupBy == GroupKey.Type)
        {
            return ByType(graph, aggregation);
        }

        var rows = new Dictionary<string, (long Count, ulong Bytes, ulong[] Kinds)>(StringComparer.Ordinal);
        for (var t = 0; t < aggregation.TypeCount; t++)
        {
            if (aggregation.Counts[t] == 0)
            {
                continue;
            }

            var name = graph.Types.GetName(t);
            var key = groupBy switch
            {
                GroupKey.Namespace => NamespaceOf(name),
                _ => graph.Types.GetModule(t) is { Length: > 0 } module ? module : "(unknown assembly)",
            };

            if (!rows.TryGetValue(key, out var row))
            {
                row = (0, 0, new ulong[TypeAggregation.HeapKindSlots]);
            }

            for (var k = 0; k < TypeAggregation.HeapKindSlots; k++)
            {
                row.Kinds[k] += aggregation.BytesByKind[(t * TypeAggregation.HeapKindSlots) + k];
            }

            rows[key] = (row.Count + aggregation.Counts[t], row.Bytes + aggregation.Bytes[t], row.Kinds);
        }

        return [.. rows.Select(r => new SummaryRow(r.Key, r.Value.Count, r.Value.Bytes, r.Value.Kinds))];
    }

    /// <summary>
    /// The display name of each present type. Distinct types can share a name (the same assembly loaded into two
    /// load contexts, or same-named types in different modules); those rows stay apart and get the module's file name,
    /// then a number, so no two rows share a name and a stale copy of a type is visible on its own row.
    /// </summary>
    public static string[] DisplayNames(HeapGraph graph, TypeAggregation aggregation)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(aggregation);
        var names = new string[aggregation.TypeCount];
        var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var t = 0; t < aggregation.TypeCount; t++)
        {
            if (aggregation.Counts[t] == 0)
            {
                continue;
            }

            names[t] = graph.Types.GetName(t);
            if (!byName.TryGetValue(names[t], out var types))
            {
                byName[names[t]] = types = [];
            }

            types.Add(t);
        }

        foreach (var (name, types) in byName)
        {
            if (types.Count == 1)
            {
                continue;
            }

            var withModule = types.ToDictionary(t => t, t => $"{name} [{ModuleFileName(graph.Types.GetModule(t))}]");
            foreach (var group in types.GroupBy(t => withModule[t], StringComparer.Ordinal))
            {
                var number = 0;
                foreach (var t in group)
                {
                    number++;
                    names[t] = group.Count() == 1 ? group.Key : $"{group.Key} #{number}";
                }
            }
        }

        return names;
    }

    /// <summary>
    /// The namespace of a type name: the part before the last dot of the outermost type, ignoring generic arguments,
    /// nested types and array ranks (<c>System.Collections.Generic.Dictionary&lt;K, V&gt;+Entry[]</c> gives
    /// <c>System.Collections.Generic</c>). Names without a namespace give "(global)".
    /// </summary>
    public static string NamespaceOf(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var end = typeName.IndexOfAny(['<', '[', '+', ',']);
        var outer = end < 0 ? typeName : typeName[..end];
        var dot = outer.LastIndexOf('.');
        return dot <= 0 ? "(global)" : outer[..dot];
    }

    private static List<SummaryRow> ByType(HeapGraph graph, TypeAggregation aggregation)
    {
        var names = DisplayNames(graph, aggregation);
        var rows = new List<SummaryRow>();
        for (var t = 0; t < aggregation.TypeCount; t++)
        {
            if (aggregation.Counts[t] == 0)
            {
                continue;
            }

            var kinds = new ulong[TypeAggregation.HeapKindSlots];
            Array.Copy(aggregation.BytesByKind, t * TypeAggregation.HeapKindSlots, kinds, 0, TypeAggregation.HeapKindSlots);
            rows.Add(new SummaryRow(names[t], aggregation.Counts[t], aggregation.Bytes[t], kinds, graph.Types.GetName(t), ModuleKey(graph.Types.GetModule(t))));
        }

        return rows;
    }

    /// <summary>The module part of a type key (ADR-018): the file name, or empty when the type has no module.</summary>
    public static string ModuleKey(string? module) => string.IsNullOrEmpty(module) ? string.Empty : Path.GetFileName(module);

    private static string ModuleFileName(string? module) =>
        string.IsNullOrEmpty(module) ? "unknown module" : Path.GetFileName(module);
}
