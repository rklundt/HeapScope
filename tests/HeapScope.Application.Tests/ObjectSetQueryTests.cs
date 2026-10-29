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

using HeapScope.Application.Snapshots;
using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

/// <summary>Sprint 3.3 (the 3.1 review's M6): a set's other snapshot bound to the worker's current id at each call.</summary>
public sealed class ObjectSetQueryTests
{
    [Fact]
    public void The_other_snapshot_is_bound_into_compared_and_inspection_row_specs_at_any_depth_and_nowhere_else()
    {
        var current = Guid.NewGuid();
        var compared = ObjectSetSpec.RetainedByOf(ObjectSetSpec.ComparedOf(ObjectSetSpec.OfType("App.A", "App.dll"), ObjectChange.Dead, Guid.NewGuid()));
        var query = new ObjectSetQuery(compared);

        var bound = query.Bind(current);

        bound.Baseline.ShouldBeNull("a retained-by set has no other snapshot of its own");
        bound.Inner!.Baseline.ShouldBe(current);
        bound.Inner.Inner!.Baseline.ShouldBeNull();
        bound.Inner.CacheKey.ShouldContain(current.ToString());
        new ObjectSetQuery(ObjectSetSpec.OfInspectionRow("growing-static-collections", 2)).Bind(current).Baseline.ShouldBe(current);
        query.Bind(null).Inner!.Baseline.ShouldBe(compared.Inner!.Baseline, "without a handle the spec keeps its own id");
    }
}
