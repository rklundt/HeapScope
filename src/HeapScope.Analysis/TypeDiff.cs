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
/// One row of a snapshot diff: a type, namespace or assembly in the baseline and the current snapshot.
/// <paramref name="BaselineTypes"/> and <paramref name="CurrentTypes"/> count the distinct types (method tables) summed
/// into the row on each side.
/// </summary>
public sealed record DiffRow(
    string Name,
    long BaselineCount,
    ulong BaselineBytes,
    long CurrentCount,
    ulong CurrentBytes,
    int BaselineTypes = 1,
    int CurrentTypes = 1,
    string? TypeName = null,
    string? Module = null)
{
    public long CountDelta => CurrentCount - BaselineCount;

    public long BytesDelta => (long)CurrentBytes - (long)BaselineBytes;

    /// <summary>Present only in the current snapshot.</summary>
    public bool IsNew => BaselineCount == 0;

    /// <summary>Present only in the baseline.</summary>
    public bool IsGone => CurrentCount == 0;

    /// <summary>
    /// Present on both sides with a different number of distinct types: in Type grouping, the same name and module loaded
    /// a different number of times, such as a stale copy of a plugin kept alive next to its reloaded copy. The summed
    /// counts alone could hide it (the old copy shrinking while the new one grows).
    /// </summary>
    public bool CopiesChanged => !IsNew && !IsGone && BaselineTypes != CurrentTypes;
}

/// <summary>
/// Compares the per-type totals of two snapshots (sprint 1.4, ADR-018): rows are matched by type name plus module file
/// name, never by object identity and never by the types view's display names (their <c>#n</c> follows each snapshot's
/// own type order). Method tables that share a key in one snapshot (one assembly in two load contexts) are summed.
/// </summary>
public static class TypeDiff
{
    public static List<DiffRow> Build(HeapGraph baselineGraph, TypeAggregation baseline, HeapGraph currentGraph, TypeAggregation current, GroupKey groupBy)
    {
        ArgumentNullException.ThrowIfNull(baselineGraph);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(currentGraph);
        ArgumentNullException.ThrowIfNull(current);

        var rows = new Dictionary<(string Name, string Module), Totals>();
        Collect(baselineGraph, baseline, groupBy, rows, isBaseline: true);
        Collect(currentGraph, current, groupBy, rows, isBaseline: false);

        // A type name seen with more than one module shows the module, so the rows stay distinguishable.
        var modulesPerName = rows.Keys.GroupBy(k => k.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byType = groupBy == GroupKey.Type;
        return [.. rows.Select(r => new DiffRow(
            modulesPerName[r.Key.Name] > 1 ? $"{r.Key.Name} [{(r.Key.Module.Length > 0 ? r.Key.Module : "unknown module")}]" : r.Key.Name,
            r.Value.BaseCount,
            r.Value.BaseBytes,
            r.Value.CurCount,
            r.Value.CurBytes,
            r.Value.BaseTypes,
            r.Value.CurTypes,
            byType ? r.Key.Name : null,
            byType ? r.Key.Module : null))];
    }

    private static void Collect(
        HeapGraph graph,
        TypeAggregation aggregation,
        GroupKey groupBy,
        Dictionary<(string Name, string Module), Totals> rows,
        bool isBaseline)
    {
        for (var t = 0; t < aggregation.TypeCount; t++)
        {
            if (aggregation.Counts[t] == 0)
            {
                continue;
            }

            var module = TypeGroups.ModuleKey(graph.Types.GetModule(t));
            var key = groupBy switch
            {
                GroupKey.Namespace => (TypeGroups.NamespaceOf(graph.Types.GetName(t)), string.Empty),
                GroupKey.Assembly => (module.Length > 0 ? module : "(unknown assembly)", string.Empty),
                _ => (graph.Types.GetName(t), module),
            };

            rows.TryGetValue(key, out var row);
            rows[key] = isBaseline
                ? row with { BaseCount = row.BaseCount + aggregation.Counts[t], BaseBytes = row.BaseBytes + aggregation.Bytes[t], BaseTypes = row.BaseTypes + 1 }
                : row with { CurCount = row.CurCount + aggregation.Counts[t], CurBytes = row.CurBytes + aggregation.Bytes[t], CurTypes = row.CurTypes + 1 };
        }
    }

    private readonly record struct Totals(long BaseCount, ulong BaseBytes, int BaseTypes, long CurCount, ulong CurBytes, int CurTypes);
}
