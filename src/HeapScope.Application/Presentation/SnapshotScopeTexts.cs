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

using HeapScope.Contracts;

namespace HeapScope.Application.Presentation;

/// <summary>
/// What a snapshot's totals count, said the same way by the app and the CLI. A full dump holds dead objects not yet
/// collected; a gcdump's capture ran a full collection first, so it holds only the survivors of that collection.
/// </summary>
public static class SnapshotScopeTexts
{
    /// <summary>What one snapshot's totals count.</summary>
    public static string Snapshot(SnapshotKindDto kind) => kind == SnapshotKindDto.GcDump
        ? "the objects that survived the gcdump's collection"
        : "all objects in the dump, including unreachable ones not yet collected";

    /// <summary>What a comparison's totals count: every object each side holds.</summary>
    public const string Comparison = "every object each snapshot holds: a full dump includes unreachable ones, a gcdump only survivors of its collection";

    /// <summary>
    /// The caution for a full dump compared with a gcdump, or null: the dump counts garbage the gcdump's collection removed
    /// (so it shows as a drop or a rise), and nested types match by the gcdump's shorter names.
    /// </summary>
    public static string? MixedKinds(SnapshotKindDto baseline, SnapshotKindDto current) => baseline == current
        ? null
        : "a full dump against a gcdump: the dump counts unreachable objects the gcdump's collection removed, and nested types are matched by the gcdump's shorter names";
}
