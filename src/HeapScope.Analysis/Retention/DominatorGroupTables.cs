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

namespace HeapScope.Analysis.Retention;

/// <summary>
/// Each type's group and each group's name and module, for one grouping of one graph: what the grouped rows of
/// <see cref="DominatorTreeRows"/> sum by. Nothing is kept here; the worker builds them once per snapshot and grouping
/// and keeps them with the snapshot, so a click does not rebuild them.
/// </summary>
public sealed class DominatorGroupTables
{
    private DominatorGroupTables(DominatorGrouping grouping, int[] groupOfType, List<(string Name, string? Module)> groups)
    {
        Grouping = grouping;
        GroupOfType = groupOfType;
        Groups = groups;
    }

    public DominatorGrouping Grouping { get; }

    /// <summary>The group of each type index.</summary>
    public int[] GroupOfType { get; }

    /// <summary>Each group's name, and its module file name when grouped by type.</summary>
    public IReadOnlyList<(string Name, string? Module)> Groups { get; }

    /// <summary>The tables of <paramref name="grouping"/> (by type or by namespace) for <paramref name="graph"/>.</summary>
    public static DominatorGroupTables Build(HeapGraph graph, DominatorGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (grouping is not (DominatorGrouping.Type or DominatorGrouping.Namespace))
        {
            throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "Group tables group by type or by namespace.");
        }

        var ids = new Dictionary<(string Name, string Module), int>(TypeKeyComparer.Type);
        var groups = new List<(string Name, string? Module)>();
        var groupOfType = new int[graph.Types.Count];
        for (var t = 0; t < groupOfType.Length; t++)
        {
            var name = graph.Types.GetName(t);
            var key = grouping == DominatorGrouping.Namespace
                ? (TypeGroups.NamespaceOf(name), string.Empty)
                : (name, TypeGroups.ModuleKey(graph.Types.GetModule(t)));
            if (!ids.TryGetValue(key, out var id))
            {
                id = groups.Count;
                ids.Add(key, id);
                groups.Add((key.Item1, key.Item2.Length == 0 ? null : key.Item2));
            }

            groupOfType[t] = id;
        }

        return new DominatorGroupTables(grouping, groupOfType, groups);
    }
}
