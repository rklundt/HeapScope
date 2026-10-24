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
using HeapScope.Analysis;
using HeapScope.Contracts;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The objects referencing a type's objects, one row per referrer type (name and module, ADR-018), sprint 2.1. Rows are
/// computed when the set is created; names are formatted per page. Sorting takes any column by name.
/// </summary>
internal sealed class TypeReferrersSource : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new(TypeReferrerColumns.Type, ResultColumnKind.Text),
        new(TypeReferrerColumns.Module, ResultColumnKind.Text),
        new(TypeReferrerColumns.Objects, ResultColumnKind.Integer),
        new(TypeReferrerColumns.References, ResultColumnKind.Integer),
        new(TypeReferrerColumns.Targets, ResultColumnKind.Integer),
        new(TypeReferrerColumns.Bytes, ResultColumnKind.Bytes),
    ];

    private readonly HeapGraph _graph;
    private readonly List<ReferrerTypeRow> _rows;

    private TypeReferrersSource(HeapGraph graph, List<ReferrerTypeRow> rows, int matchedTypes, long targetObjects)
    {
        _graph = graph;
        _rows = rows;
        MatchedTypes = matchedTypes;
        TargetObjects = targetObjects;
        EstimatedBytes = 40L * rows.Count;
    }

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => _rows.Count;

    public long EstimatedBytes { get; }

    /// <summary>Types (method tables) with the requested name and module.</summary>
    public int MatchedTypes { get; }

    /// <summary>Objects of the matched types.</summary>
    public long TargetObjects { get; }

    /// <summary>References into the matched types' objects.</summary>
    public long ReferenceCount => _rows.Sum(r => r.References);

    /// <summary>
    /// Checks the request and matches its type before any index is needed (review M8): an unknown sort column throws; no
    /// matching type gives an empty source.
    /// </summary>
    /// <exception cref="ArgumentException">The sort column is unknown.</exception>
    public static TypeReferrersSource? EmptyUnlessMatched(HeapGraph graph, TypeReferrersRequest request)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        Sort(graph).Validate(request.SortColumn ?? TypeReferrerColumns.References);
        TypeGroups.MatchTypes(graph, request.TypeName, request.Module, out var matched);
        return matched == 0 ? new TypeReferrersSource(graph, [], 0, 0) : null;
    }

    public static TypeReferrersSource Create(HeapGraph graph, ReverseReferences reverse, TypeReferrersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.TypeName);
        var sortColumn = request.SortColumn ?? TypeReferrerColumns.References;
        var sort = Sort(graph);
        sort.Validate(sortColumn);
        var types = TypeGroups.MatchTypes(graph, request.TypeName, request.Module, out var matched);
        var rows = IncomingReferences.ForTypes(graph, reverse, types, out var targets, cancellationToken).ToList();
        sort.Sort(rows, sortColumn, request.Descending);
        return new TypeReferrersSource(graph, rows, matched, targets);
    }

    public void FormatRow(int index, string?[] cells)
    {
        var row = _rows[index];
        cells[0] = _graph.Types.GetName(row.TypeIndex);
        cells[1] = TypeGroups.ModuleKey(_graph.Types.GetModule(row.TypeIndex));
        cells[2] = row.Objects.ToString(CultureInfo.InvariantCulture);
        cells[3] = row.References.ToString(CultureInfo.InvariantCulture);
        cells[4] = row.Targets.ToString(CultureInfo.InvariantCulture);
        cells[5] = row.Bytes.ToString(CultureInfo.InvariantCulture);
    }

    private static ColumnSort<ReferrerTypeRow> Sort(HeapGraph graph)
    {
        string Name(ReferrerTypeRow r) => graph.Types.GetName(r.TypeIndex);
        return new ColumnSort<ReferrerTypeRow>(Name)
            .On(TypeReferrerColumns.Type, (a, b) => string.CompareOrdinal(Name(a), Name(b)))
            .On(TypeReferrerColumns.Module, (a, b) => string.Compare(TypeGroups.ModuleKey(graph.Types.GetModule(a.TypeIndex)), TypeGroups.ModuleKey(graph.Types.GetModule(b.TypeIndex)), StringComparison.OrdinalIgnoreCase))
            .On(TypeReferrerColumns.Objects, r => r.Objects)
            .On(TypeReferrerColumns.References, r => r.References)
            .On(TypeReferrerColumns.Targets, r => r.Targets)
            .On(TypeReferrerColumns.Bytes, r => r.Bytes);
    }
}
