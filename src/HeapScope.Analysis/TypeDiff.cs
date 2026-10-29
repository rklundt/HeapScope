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
/// into the row on each side. In Type grouping, <paramref name="TypeName"/> and <paramref name="Module"/> are the key that
/// opens the row's objects on the side front ends open (the current snapshot, or the baseline for a gone row), as that
/// side names the type; null when the row merges several types of that side under one name (sprint 1.8: a full dump's
/// nested types in a diff against a gcdump).
/// </summary>
/// <param name="ComparesCopies">False when the two sides are different snapshot kinds: their types are matched by mapped
/// names, so the number of types per row says nothing about copies (sprint 1.8 review).</param>
public sealed record DiffRow(
    string Name,
    long BaselineCount,
    ulong BaselineBytes,
    long CurrentCount,
    ulong CurrentBytes,
    int BaselineTypes = 1,
    int CurrentTypes = 1,
    string? TypeName = null,
    string? Module = null,
    bool ComparesCopies = true)
{
    /// <summary>Current objects with no object of their type at their address in the baseline (sprint 3.3), when matched.</summary>
    public long? NewObjects { get; init; }

    /// <summary>Baseline objects with no object of their type at their address in the current snapshot, when matched.</summary>
    public long? DeadObjects { get; init; }

    /// <summary>The row's retained lower bound in the baseline (sprint 3.3), when asked for.</summary>
    public ulong? BaselineRetained { get; init; }

    /// <summary>The row's retained lower bound in the current snapshot, when asked for.</summary>
    public ulong? CurrentRetained { get; init; }

    /// <summary>The row's bytes per heap kind in the baseline (<see cref="TypeAggregation.HeapKindSlots"/>).</summary>
    public ulong[]? BaselineKinds { get; init; }

    /// <summary>The row's bytes per heap kind in the current snapshot.</summary>
    public ulong[]? CurrentKinds { get; init; }

    public long? RetainedDelta => BaselineRetained is { } before && CurrentRetained is { } after ? (long)after - (long)before : null;

    /// <summary>The byte delta of one heap kind (a side without the row counts 0).</summary>
    public long KindDelta(HeapKind kind) => (long)(CurrentKinds?[(int)kind] ?? 0) - (long)(BaselineKinds?[(int)kind] ?? 0);

    /// <summary>Rows are equal by value, their per-kind bytes compared element by element.</summary>
    public bool Equals(DiffRow? other) =>
        other is not null
        && (Name, BaselineCount, BaselineBytes, CurrentCount, CurrentBytes, BaselineTypes, CurrentTypes, TypeName, Module, ComparesCopies, NewObjects, DeadObjects, BaselineRetained, CurrentRetained)
            == (other.Name, other.BaselineCount, other.BaselineBytes, other.CurrentCount, other.CurrentBytes, other.BaselineTypes, other.CurrentTypes, other.TypeName, other.Module, other.ComparesCopies, other.NewObjects, other.DeadObjects, other.BaselineRetained, other.CurrentRetained)
        && (BaselineKinds ?? []).AsSpan().SequenceEqual(other.BaselineKinds ?? [])
        && (CurrentKinds ?? []).AsSpan().SequenceEqual(other.CurrentKinds ?? []);

    public override int GetHashCode() => HashCode.Combine(Name, BaselineCount, BaselineBytes, CurrentCount, CurrentBytes, NewObjects, DeadObjects);

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
    public bool CopiesChanged => ComparesCopies && !IsNew && !IsGone && BaselineTypes != CurrentTypes;
}

/// <summary>
/// Compares the per-type totals of two snapshots (sprint 1.4, ADR-018): rows are matched by type name plus module file
/// name, never by object identity and never by the types view's display names (their <c>#n</c> follows each snapshot's
/// own type order). Method tables that share a key in one snapshot (one assembly in two load contexts) are summed.
/// </summary>
public static class TypeDiff
{
    /// <param name="baselineNames">Maps the baseline's type names before matching, or null (sprint 1.8: a full dump's names in
    /// their gcdump form when it is compared with a gcdump, <see cref="TypeNameForms.AsGcDumpName"/>).</param>
    /// <param name="currentNames">Maps the current snapshot's type names before matching, or null.</param>
    public static List<DiffRow> Build(
        HeapGraph baselineGraph,
        TypeAggregation baseline,
        HeapGraph currentGraph,
        TypeAggregation current,
        GroupKey groupBy,
        Func<string, string>? baselineNames = null,
        Func<string, string>? currentNames = null,
        TypeInterfaces? baselineInterfaces = null,
        TypeInterfaces? currentInterfaces = null,
        DiffExtras? extras = null)
    {
        ArgumentNullException.ThrowIfNull(baselineGraph);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(currentGraph);
        ArgumentNullException.ThrowIfNull(current);

        var rows = new Dictionary<(string Name, string Module), Totals>(groupBy == GroupKey.Assembly ? TypeKeyComparer.Assembly : TypeKeyComparer.Type);
        if (groupBy == GroupKey.Interface && (baselineInterfaces is null || currentInterfaces is null))
        {
            throw new ArgumentNullException(nameof(baselineInterfaces), "Interface grouping needs both snapshots' interfaces.");
        }

        extras ??= new DiffExtras();
        Collect(baselineGraph, baseline, groupBy, baselineNames, baselineInterfaces, rows, isBaseline: true, extras.DeadPerBaselineType, extras.BaselineRetained);
        Collect(currentGraph, current, groupBy, currentNames, currentInterfaces, rows, isBaseline: false, extras.NewPerCurrentType, extras.CurrentRetained);
        PlaceGroupRetained(rows, extras.BaselineRetained, isBaseline: true);
        PlaceGroupRetained(rows, extras.CurrentRetained, isBaseline: false);

        // A type name seen with more than one module shows the module, so the rows stay distinguishable.
        var modulesPerName = rows.Keys.GroupBy(k => k.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byType = groupBy == GroupKey.Type;
        var mapped = baselineNames is not null || currentNames is not null;
        return [.. rows.Select(r =>
        {
            // The side front ends open: the current snapshot, or the baseline when the type is gone.
            var open = r.Value.CurCount == 0 ? r.Value.BaseName : r.Value.CurName;
            return new DiffRow(
                modulesPerName[r.Key.Name] > 1 ? $"{r.Key.Name} [{(r.Key.Module.Length > 0 ? r.Key.Module : "unknown module")}]" : r.Key.Name,
                r.Value.BaseCount,
                r.Value.BaseBytes,
                r.Value.CurCount,
                r.Value.CurBytes,
                r.Value.BaseTypes,
                r.Value.CurTypes,
                byType && open is not Ambiguous ? open ?? r.Key.Name : null,
                byType && open is not Ambiguous ? r.Key.Module : null,
                ComparesCopies: !mapped)
            {
                NewObjects = extras.NewPerCurrentType is null ? null : r.Value.New,
                DeadObjects = extras.DeadPerBaselineType is null ? null : r.Value.Dead,
                BaselineRetained = extras.BaselineRetained is null ? null : r.Value.BaseRetained,
                CurrentRetained = extras.CurrentRetained is null ? null : r.Value.CurRetained,
                BaselineKinds = extras.Kinds ? r.Value.BaseKinds ?? new ulong[TypeAggregation.HeapKindSlots] : null,
                CurrentKinds = extras.Kinds ? r.Value.CurKinds ?? new ulong[TypeAggregation.HeapKindSlots] : null,
            };
        })];
    }

    private static void Collect(
        HeapGraph graph,
        TypeAggregation aggregation,
        GroupKey groupBy,
        Func<string, string>? names,
        TypeInterfaces? interfaces,
        Dictionary<(string Name, string Module), Totals> rows,
        bool isBaseline,
        long[]? unmatchedPerType,
        GroupRetained? retained)
    {
        for (var t = 0; t < aggregation.TypeCount; t++)
        {
            if (aggregation.Counts[t] == 0)
            {
                continue;
            }

            var module = TypeGroups.ModuleKey(graph.Types.GetModule(t));
            var original = graph.Types.GetName(t);
            var name = names is null ? original : names(original);
            IEnumerable<(string, string)> keys = groupBy switch
            {
                GroupKey.Namespace => [(TypeGroups.NamespaceOf(name), string.Empty)],
                GroupKey.Assembly => [(module.Length > 0 ? module : "(unknown assembly)", string.Empty)],
                // Checked in Build: interface grouping comes with both tables.
                GroupKey.Interface => interfaces!.Keys(t).Select(i => (i, string.Empty)),
                _ => [(name, module)],
            };

            // Retained bytes (sprint 3.3): per type in Type grouping (summed over its method tables), per group otherwise.
            var typeRetained = retained is { ByType: true } ? retained.Bytes[t] : 0UL;
            foreach (var key in keys)
            {
                rows.TryGetValue(key, out var row);
                var kinds = (isBaseline ? row.BaseKinds : row.CurKinds) ?? new ulong[TypeAggregation.HeapKindSlots];
                for (var k = 0; k < TypeAggregation.HeapKindSlots; k++)
                {
                    kinds[k] += aggregation.BytesByKind[(t * TypeAggregation.HeapKindSlots) + k];
                }

                var unmatched = unmatchedPerType?[t] ?? 0;
                rows[key] = isBaseline
                    ? row with { BaseCount = row.BaseCount + aggregation.Counts[t], BaseBytes = row.BaseBytes + aggregation.Bytes[t], BaseTypes = row.BaseTypes + 1, BaseName = Merge(row.BaseName, original), Dead = row.Dead + unmatched, BaseRetained = row.BaseRetained + typeRetained, BaseKinds = kinds }
                    : row with { CurCount = row.CurCount + aggregation.Counts[t], CurBytes = row.CurBytes + aggregation.Bytes[t], CurTypes = row.CurTypes + 1, CurName = Merge(row.CurName, original), New = row.New + unmatched, CurRetained = row.CurRetained + typeRetained, CurKinds = kinds };
            }
        }
    }

    /// <summary>Puts each group's retained bytes on its row (namespace and assembly grouping; Type grouping sums per type).</summary>
    private static void PlaceGroupRetained(Dictionary<(string Name, string Module), Totals> rows, GroupRetained? retained, bool isBaseline)
    {
        if (retained is null || retained.ByType)
        {
            return;
        }

        for (var g = 0; g < retained.Names.Length; g++)
        {
            var key = (retained.Names[g], string.Empty);
            if (rows.TryGetValue(key, out var row))
            {
                rows[key] = isBaseline ? row with { BaseRetained = retained.Bytes[g] } : row with { CurRetained = retained.Bytes[g] };
            }
        }
    }

    /// <summary>Marks a row whose side merged differently named types (an instances query takes one name).</summary>
    private const string Ambiguous = "\0ambiguous";

    private static string Merge(string? seen, string original) => seen is null || seen == original ? original : Ambiguous;

    private readonly record struct Totals(
        long BaseCount,
        ulong BaseBytes,
        int BaseTypes,
        long CurCount,
        ulong CurBytes,
        int CurTypes,
        string? BaseName,
        string? CurName,
        long New = 0,
        long Dead = 0,
        ulong BaseRetained = 0,
        ulong CurRetained = 0,
        ulong[]? BaseKinds = null,
        ulong[]? CurKinds = null);
}

/// <summary>
/// What a diff adds to its rows (sprint 3.3): object matches by address (unmatched objects per type index of each side)
/// and retained bytes per group of each side.
/// </summary>
public sealed class DiffExtras
{
    /// <summary>New objects per type index of the current snapshot (<see cref="ObjectIdentity.NewPerType"/>).</summary>
    public long[]? NewPerCurrentType { get; init; }

    /// <summary>Dead objects per type index of the baseline.</summary>
    public long[]? DeadPerBaselineType { get; init; }

    public GroupRetained? BaselineRetained { get; init; }

    public GroupRetained? CurrentRetained { get; init; }

    /// <summary>Whether rows carry their bytes per heap kind (both snapshots have generations).</summary>
    public bool Kinds { get; init; }
}

/// <summary>
/// Retained bytes of a snapshot's groups (<see cref="TypeGroups.GroupOfType"/>'s numbering): per type index in Type
/// grouping (<paramref name="ByType"/>), otherwise per group with its <paramref name="Names"/>.
/// </summary>
public sealed record GroupRetained(ulong[] Bytes, string[] Names, bool ByType);
