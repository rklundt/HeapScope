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
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The objects one object references (sprint 1.5), in the graph's order. With an object reader each reference is named
/// by the field path or array index that holds it; otherwise by its position (<c>#1</c>, <c>#2</c>).
/// </summary>
internal sealed class ReferencesSource : IResultSource
{
    /// <summary>Array elements walked to name references.</summary>
    public const int MaxNamedElements = 1_000_000;

    private static readonly ResultColumn[] s_columns =
    [
        new(ReferenceColumns.Field, ResultColumnKind.Text),
        new(ReferenceColumns.Address, ResultColumnKind.Address, IsKey: true),
        new(ReferenceColumns.Type, ResultColumnKind.Text),
        new(ReferenceColumns.Bytes, ResultColumnKind.Bytes),
    ];

    private readonly HeapGraph _graph;
    private readonly int[] _targets;
    private readonly IReadOnlyDictionary<ulong, string>? _paths;

    private ReferencesSource(HeapGraph graph, int[] targets, IReadOnlyDictionary<ulong, string>? paths)
    {
        _graph = graph;
        _targets = targets;
        _paths = paths;
        EstimatedBytes = (4L * targets.Length) + (paths is null ? 0 : paths.Values.Sum(p => 48L + (2L * p.Length)));
    }

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => _targets.Length;

    public long EstimatedBytes { get; }

    public static ReferencesSource Create(HeapGraph graph, IObjectReader? reader, int objectIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var targets = graph.GetReferences(objectIndex).ToArray();
        var paths = targets.Length > 0 ? reader?.ReadReferencePaths(graph.Addresses[objectIndex], MaxNamedElements, cancellationToken) : null;
        return new ReferencesSource(graph, targets, paths);
    }

    public void FormatRow(int index, string?[] cells)
    {
        var target = _targets[index];
        var address = _graph.Addresses[target];
        cells[0] = _paths is null
            ? string.Create(CultureInfo.InvariantCulture, $"#{index + 1}")
            : _paths.GetValueOrDefault(address, string.Empty);
        cells[1] = string.Create(CultureInfo.InvariantCulture, $"0x{address:x}");
        cells[2] = _graph.Types.GetName(_graph.TypeIndices[target]);
        cells[3] = _graph.GetSize(target).ToString(CultureInfo.InvariantCulture);
    }
}
