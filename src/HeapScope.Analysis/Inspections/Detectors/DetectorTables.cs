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

using HeapScope.Analysis.Retention;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

/// <summary>The detectors' type tables: a type, its objects and bytes, an optional detail, and what a row opens.</summary>
internal static class DetectorTables
{
    public static RowTable<DetectorTypeRow> Types(DetectorTypeRow[] rows, string? detailColumn, string scope = InspectionScopes.Reachable)
    {
        InspectionColumn[] columns =
        [
            new(InspectionColumns.Type, InspectionColumnKind.Text, IsKey: true),
            new(InspectionColumns.Objects, InspectionColumnKind.Integer),
            new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
            .. detailColumn is null ? Array.Empty<InspectionColumn>() : [new InspectionColumn(detailColumn, InspectionColumnKind.Text)],
            new(InspectionColumns.TypeName, InspectionColumnKind.Text),
            new(InspectionColumns.Module, InspectionColumnKind.Text),
            new(InspectionColumns.Scope, InspectionColumnKind.Text),
        ];
        var withDetail = detailColumn is not null;
        return new RowTable<DetectorTypeRow>(columns, rows, 120, Format, row => row.Members);

        void Format(DetectorTypeRow row, string?[] cells)
        {
            var at = 0;
            cells[at++] = row.Key;
            cells[at++] = InspectionRows.Number(row.Objects);
            cells[at++] = InspectionRows.Number(row.Bytes);
            if (withDetail)
            {
                cells[at++] = row.Detail;
            }

            cells[at++] = row.TypeName;
            cells[at++] = row.Module;
            cells[at] = scope;
        }
    }
}
