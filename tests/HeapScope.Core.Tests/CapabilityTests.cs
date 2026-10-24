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

using HeapScope.Core.Synthetic;

namespace HeapScope.Core.Tests;

public class CapabilityTests
{
    private const SnapshotCapabilities GcDumpLike =
        SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.ObjectAddresses | SnapshotCapabilities.CompleteGraph;

    [Fact]
    public void Require_passes_when_all_capabilities_are_present()
    {
        Should.NotThrow(() => SnapshotCapabilities.FullDump.Require(SnapshotCapabilities.FieldValues | SnapshotCapabilities.StringContents));
    }

    [Fact]
    public void Require_reports_exactly_the_missing_capabilities()
    {
        var ex = Should.Throw<CapabilityNotSupportedException>(
            () => GcDumpLike.Require(SnapshotCapabilities.FieldValues | SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.StringContents));

        ex.Missing.ShouldBe(SnapshotCapabilities.FieldValues | SnapshotCapabilities.StringContents);
        ex.Message.ShouldContain("FieldValues");
    }

    [Fact]
    public void Has_all_and_missing_agree()
    {
        GcDumpLike.HasAll(SnapshotCapabilities.ObjectGraph).ShouldBeTrue();
        GcDumpLike.HasAll(SnapshotCapabilities.Generations).ShouldBeFalse();
        GcDumpLike.Missing(SnapshotCapabilities.FullDump).HasFlag(SnapshotCapabilities.ObjectGraph).ShouldBeFalse();
    }

    [Fact]
    public void Graph_snapshot_exposes_its_parts_and_no_services()
    {
        var info = new SnapshotInfo(SnapshotKind.GcDump, "synthetic");
        using var snapshot = new GraphSnapshot(info, GcDumpLike, SyntheticGraphs.Chain(2));

        snapshot.Info.ShouldBe(info);
        snapshot.Capabilities.ShouldBe(GcDumpLike);
        snapshot.Graph.ObjectCount.ShouldBe(2);
        snapshot.TryGetService<IDisposable>(out var service).ShouldBeFalse();
        service.ShouldBeNull();
    }
}
