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

using HeapScope.Analysis;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Core's vocabulary on the wire (ADR-016, sprint 1.9): Contracts mirrors Core's enums value for value (checked by the
/// architecture test's mirror tests), and this is the one place that converts between them.
/// </summary>
internal static class ContractMapping
{
    public static SnapshotKindDto ToDto(this SnapshotKind kind) => (SnapshotKindDto)(int)kind;

    public static SnapshotFeatures ToDto(this SnapshotCapabilities capabilities) => (SnapshotFeatures)(uint)capabilities;

    public static HeapKindDto ToDto(this HeapKind kind) => (HeapKindDto)(int)kind;

    public static RootKindDto ToDto(this RootKind kind) => (RootKindDto)(int)kind;

    public static GroupKey ToGroupKey(this TypeGrouping grouping) => (GroupKey)(int)grouping;

    /// <summary>Why <paramref name="missing"/> (its lowest flag) is unavailable on a snapshot of <paramref name="kind"/>.</summary>
    public static string ExplainMissing(SnapshotCapabilities missing, SnapshotKind kind) =>
        CapabilityDescriptions.ExplainMissing((SnapshotFeatures)(uint)LowestFlag((int)missing), kind.ToDto());

    /// <summary>The lowest set bit of a flags value: the capability a refusal names.</summary>
    public static int LowestFlag(int flags) => flags & -flags;
}
