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
using HeapScope.Analysis.Sets;
using HeapScope.Contracts;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>A set's top members (sprint 3.1), most retained first (<see cref="SetDominatorColumns"/>); its rows read the graph under a lease.</summary>
internal sealed class SetDominatorsSource(HeapGraph graph, Dominators dominators, SetDominatorList tops) : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new(SetDominatorColumns.Address, ResultColumnKind.Address, IsKey: true),
        new(SetDominatorColumns.Type, ResultColumnKind.Text),
        new(SetDominatorColumns.Bytes, ResultColumnKind.Bytes),
        new(SetDominatorColumns.Retained, ResultColumnKind.Bytes),
        new(SetDominatorColumns.Members, ResultColumnKind.Integer),
    ];

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => tops.Count;

    public long EstimatedBytes => 8L * tops.Count;

    public void FormatRow(int index, string?[] cells)
    {
        var top = tops.Tops[index];
        cells[0] = string.Create(CultureInfo.InvariantCulture, $"0x{graph.Addresses[top]:x}");
        cells[1] = graph.Types.GetName(graph.TypeIndices[top]);
        cells[2] = graph.GetSize(top).ToString(CultureInfo.InvariantCulture);
        cells[3] = dominators.RetainedSize(top).ToString(CultureInfo.InvariantCulture);
        cells[4] = tops.Below[index].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The first rows' types and retained bytes, for the log (no addresses).</summary>
    public string Describe(int count) =>
        string.Join("; ", Enumerable.Range(0, Math.Min(count, tops.Count)).Select(i => string.Create(CultureInfo.InvariantCulture, $"{graph.Types.GetName(graph.TypeIndices[tops.Tops[i]])} {dominators.RetainedSize(tops.Tops[i])} bytes, {tops.Below[i]} members below")));
}
