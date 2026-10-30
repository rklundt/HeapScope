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

// The leak detectors (sprint 2.6, ADR-020 amended): inspections of kind Leak that name a leak pattern, its cause and its
// fix. Each counts reachable objects only (garbage the GC has not collected is no leak), reads the fields it needs
// through the typed reader, and logs its thresholds as parameters and, as its basis, what it scanned, what it left out
// and why, and every row it reports, so a disputed finding is traced to its rule and inputs.

/// <summary>Type masks, reachable objects per type and names, shared by the detectors.</summary>
internal static class DetectorGraph
{
    /// <summary>A flag per type index: whether <paramref name="match"/> holds for it.</summary>
    public static bool[] TypeMask(HeapGraph graph, Func<int, bool> match)
    {
        var mask = new bool[graph.Types.Count];
        for (var t = 0; t < mask.Length; t++)
        {
            mask[t] = match(t);
        }

        return mask;
    }

    public static bool[] TypesNamed(HeapGraph graph, params string[] names) =>
        TypeMask(graph, t => names.Contains(graph.Types.GetName(t), StringComparer.Ordinal));

    /// <summary>The reachable objects of the types in <paramref name="types"/>, in index order.</summary>
    public static List<int> ReachableOf(HeapGraph graph, Reachability reachable, bool[] types, InspectionContext context, string phase)
    {
        var found = new List<int>();
        if (!types.Contains(true))
        {
            return found;
        }

        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report(phase, i, typeIndices.Length);
            }

            if (types[typeIndices[i]] && reachable.IsReachable(i))
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>"Name (count), Name (count)" for the most common names, then how many others.</summary>
    public static string Top(Dictionary<string, long> counts, int shown = 3)
    {
        var top = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        var text = string.Join(", ", top.Take(shown).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key} ({c.Value:N0})")));
        return top.Count > shown ? string.Create(CultureInfo.InvariantCulture, $"{text} and {top.Count - shown:N0} more types") : text;
    }

    /// <summary>The framework's own types, by namespace.</summary>
    public static bool IsFramework(string name) => FrameworkTypes.IsFramework(name);

    /// <summary>A type the compiler generated: a closure, a lambda cache or a state machine.</summary>
    public static bool IsCompilerGenerated(string name) =>
        name.StartsWith('<') || name.Contains("+<", StringComparison.Ordinal) || name.Contains(".<", StringComparison.Ordinal);

    public static string Module(HeapGraph graph, int type) => TypeGroups.ModuleKey(graph.Types.GetModule(type));
}
