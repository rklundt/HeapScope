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

namespace HeapScope.Analysis;

/// <summary>
/// One row of a trend (sprint 3.3): a type, namespace, assembly or interface with its objects and bytes in each snapshot,
/// in order. In Type grouping <see cref="TypeName"/> and <see cref="Module"/> open the row's objects.
/// </summary>
public sealed record TrendRow(string Name, long[] Counts, ulong[] Bytes, string? TypeName = null, string? Module = null)
{
    public long CountDelta => Counts[^1] - Counts[0];

    public long BytesDelta => (long)Bytes[^1] - (long)Bytes[0];

    /// <summary>Steps between consecutive snapshots in which the bytes grew.</summary>
    public int GrowthSteps
    {
        get
        {
            var steps = 0;
            for (var i = 1; i < Bytes.Length; i++)
            {
                if (Bytes[i] > Bytes[i - 1])
                {
                    steps++;
                }
            }

            return steps;
        }
    }
}

/// <summary>
/// Per-row totals across several snapshots (sprint 3.3): rows matched by type name and module file (ADR-018) as each
/// snapshot names its types, like a diff of each consecutive pair.
/// </summary>
public static class TypeTrend
{
    public static List<TrendRow> Build(IReadOnlyList<(HeapGraph Graph, TypeAggregation Aggregation, TypeInterfaces? Interfaces)> snapshots, GroupKey groupBy)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count < 2)
        {
            throw new ArgumentException("A trend needs at least two snapshots.", nameof(snapshots));
        }

        var n = snapshots.Count;
        var rows = new Dictionary<(string Name, string Module), (long[] Counts, ulong[] Bytes)>(groupBy == GroupKey.Assembly ? TypeKeyComparer.Assembly : TypeKeyComparer.Type);
        for (var s = 0; s < n; s++)
        {
            var (graph, aggregation, interfaces) = snapshots[s];
            if (groupBy == GroupKey.Interface && interfaces is null)
            {
                throw new ArgumentException("Interface grouping needs every snapshot's interfaces.", nameof(snapshots));
            }

            for (var t = 0; t < aggregation.TypeCount; t++)
            {
                if (aggregation.Counts[t] == 0)
                {
                    continue;
                }

                var module = TypeGroups.ModuleKey(graph.Types.GetModule(t));
                var name = graph.Types.GetName(t);
                IEnumerable<(string, string)> keys = groupBy switch
                {
                    GroupKey.Namespace => [(TypeGroups.NamespaceOf(name), string.Empty)],
                    GroupKey.Assembly => [(module.Length > 0 ? module : "(unknown assembly)", string.Empty)],
                    GroupKey.Interface => interfaces!.Keys(t).Select(i => (i, string.Empty)),
                    _ => [(name, module)],
                };
                foreach (var key in keys)
                {
                    if (!rows.TryGetValue(key, out var row))
                    {
                        rows[key] = row = (new long[n], new ulong[n]);
                    }

                    row.Counts[s] += aggregation.Counts[t];
                    row.Bytes[s] += aggregation.Bytes[t];
                }
            }
        }

        // A type name seen with more than one module shows the module, as in a diff.
        var modulesPerName = rows.Keys.GroupBy(k => k.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byType = groupBy == GroupKey.Type;
        return [.. rows.Select(r => new TrendRow(
            modulesPerName[r.Key.Name] > 1 ? $"{r.Key.Name} [{(r.Key.Module.Length > 0 ? r.Key.Module : "unknown module")}]" : r.Key.Name,
            r.Value.Counts,
            r.Value.Bytes,
            byType ? r.Key.Name : null,
            byType ? r.Key.Module : null))];
    }
}
