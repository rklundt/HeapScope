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
using HeapScope.Application.Presentation;
using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

/// <summary>The texts the app and the CLI share (sprint 3.8, P18): one wording, numbers in the front end's culture.</summary>
public sealed class PresentationTextsTests
{
    private static readonly CultureInfo s_german = CultureInfo.GetCultureInfo("de-DE");

    [Fact]
    public void The_retained_size_source_says_how_the_dominators_were_obtained_and_what_the_roots_reach()
    {
        var computed = new DominatorIndexInfo(FromSidecar: false, SnapshotMemoryMode.InMemory, Seconds: 1.5, Bytes: 100, Saved: true, ReachableObjects: 56_000);
        var saved = computed with { FromSidecar = true, Note = "This snapshot is incomplete." };

        RetainedSizeTexts.Source(computed, CultureInfo.InvariantCulture).ShouldBe("Retained sizes: dominators computed in 1.5 s, 56,000 objects reachable from the roots.");
        RetainedSizeTexts.Source(saved, s_german).ShouldBe("Retained sizes: dominators read from the saved index in 1,5 s, 56.000 objects reachable from the roots. This snapshot is incomplete.");
    }

    [Fact]
    public void The_identity_text_counts_new_and_dead_objects_and_says_why_moved_objects_count_as_both()
    {
        ObjectIdentityTexts.Matched(1200, 3, CultureInfo.InvariantCulture).ShouldBe(
            "1,200 new and 3 dead objects, matched by address: an object a collection moved counts as dead and new (Gen0 and Gen1 survivors usually move).");
        ObjectIdentityTexts.Matched(1200, 3, s_german).ShouldStartWith("1.200 new and 3 dead objects");
    }

    [Fact]
    public void The_scope_of_a_full_dump_includes_unreachable_objects_and_of_a_gcdump_only_survivors()
    {
        SnapshotScopeTexts.Snapshot(SnapshotKindDto.FullDump).ShouldBe("all objects in the dump, including unreachable ones not yet collected");
        SnapshotScopeTexts.Snapshot(SnapshotKindDto.GcDump).ShouldBe("the objects that survived the gcdump's collection");
        SnapshotScopeTexts.Snapshot(SnapshotKindDto.GcDump).ShouldNotContain("unreachable");
    }

    [Fact]
    public void The_scope_of_a_comparison_names_both_kinds_and_cautions_against_mixing_them()
    {
        SnapshotScopeTexts.Comparison.ShouldBe("every object each snapshot holds: a full dump includes unreachable ones, a gcdump only survivors of its collection");
        SnapshotScopeTexts.MixedKinds(SnapshotKindDto.FullDump, SnapshotKindDto.FullDump).ShouldBeNull();
        SnapshotScopeTexts.MixedKinds(SnapshotKindDto.GcDump, SnapshotKindDto.GcDump).ShouldBeNull();
        SnapshotScopeTexts.MixedKinds(SnapshotKindDto.FullDump, SnapshotKindDto.GcDump).ShouldNotBeNull().ShouldContain("a full dump against a gcdump");
        SnapshotScopeTexts.MixedKinds(SnapshotKindDto.GcDump, SnapshotKindDto.FullDump).ShouldBe(SnapshotScopeTexts.MixedKinds(SnapshotKindDto.FullDump, SnapshotKindDto.GcDump));
    }
}
