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
/// Disposed but referenced (sprint 2.6): at least <see cref="MinObjects"/> reachable objects of one type whose disposed
/// flag is set (the field names the .NET dispose pattern, its templates and auto-properties use; a bool, or an integer
/// set with Interlocked). Something still holds objects that are done: a list never cleared, a cache, an event.
/// </summary>
internal sealed class DisposedButReferencedDetector : IInspection
{
    public const long MinObjects = 100;

    /// <summary>Disposed flags, as the dispose pattern, its Visual Studio template, auto-properties and common code name them.</summary>
    internal static readonly string[] FlagNames =
    [
        "_disposed", "disposed", "_disposedValue", "disposedValue", "_isDisposed", "isDisposed", "m_disposed", "IsDisposed", "Disposed",
        "<IsDisposed>k__BackingField", "<Disposed>k__BackingField",
    ];

    public string Name => "disposed-but-referenced";

    public string Title => "Disposed but referenced";

    public string Cause => "Objects that were disposed are still referenced: whatever holds them (a list, a cache, an event) keeps memory alive that is no longer used.";

    public string Fix => "Remove disposed objects from the collections and events that hold them, or stop holding them once disposed.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinObjects={MinObjects};Flags={string.Join(",", FlagNames)}");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var ct = context.CancellationToken;
        var reachable = Reachability.For(graph, ct);

        // Reachable objects per type, and each type's first one: only types with enough of them are probed (review H7).
        var perType = new long[graph.Types.Count];
        var first = new int[graph.Types.Count];
        Array.Fill(first, -1);
        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                context.Report("Counting reachable objects", i, typeIndices.Length);
            }

            if (reachable.IsReachable(i))
            {
                var type = typeIndices[i];
                if (perType[type]++ == 0)
                {
                    first[type] = i;
                }
            }
        }

        // One instance tells whether the type has a disposed flag.
        var candidates = new Dictionary<int, string>();
        var noFlag = new Dictionary<string, long>(StringComparer.Ordinal);
        long probed = 0;
        for (var t = 0; t < perType.Length; t++)
        {
            if (perType[t] < MinObjects || graph.Types.Kinds[t].HasFlag(TypeKind.Array) || graph.Types.Kinds[t].HasFlag(TypeKind.String))
            {
                continue;
            }

            if ((++probed & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (FlagNames.FirstOrDefault(f => Fields.Flag(reader, graph.Addresses[first[t]], f) is not null) is { } flag)
            {
                candidates[t] = flag;
            }
            else
            {
                noFlag[graph.Types.GetName(t)] = perType[t];
            }
        }

        var disposed = new TypeTally();
        var mask = DetectorGraph.TypeMask(graph, candidates.ContainsKey);
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, mask, context, "Reading disposed flags"))
        {
            var type = graph.TypeIndices[obj];
            if (Fields.Flag(reader, graph.Addresses[obj], candidates[type]) == true)
            {
                disposed.Add(type, obj, graph.GetSize(obj));
            }
        }

        var rows = disposed.Types
            .Where(d => d.Objects >= MinObjects)
            .Select(d => new DetectorTypeRow(graph.Types.GetName(d.Type), candidates[d.Type], d.Objects, d.Bytes, graph.Types.GetName(d.Type), DetectorGraph.Module(graph, d.Type), [.. d.Members]))
            .OrderByDescending(r => r.Objects).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{reachable.Count} reachable objects; {probed} types with {MinObjects}+ probed, {candidates.Count} with a disposed flag (most objects without one: {DetectorGraph.Top(noFlag, 5)}); disposed: {string.Join("; ", disposed.Types.OrderByDescending(d => d.Objects).Take(Math.Max(10, rows.Length)).Select(d => $"{graph.Types.GetName(d.Type)} {d.Objects} by {candidates[d.Type]}"))}");
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, null, DetectorTables.Types(rows, "Flag"), basis);
    }
}
