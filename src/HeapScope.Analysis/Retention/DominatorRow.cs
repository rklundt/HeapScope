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

namespace HeapScope.Analysis.Retention;

/// <summary>
/// A row of the flattened dominator tree (sprint 2.4): its key and its parent's, its depth and kind; for an object its
/// index (otherwise -1); a name (the type, the group, or what a "more" row stands for) and the module; the objects the
/// row stands for, their retained and own bytes; how many children expanding it shows; whether it is expanded.
/// </summary>
public readonly record struct DominatorRow(
    string Key,
    string ParentKey,
    int Depth,
    DominatorRowKind Kind,
    int Object,
    string Name,
    string? Module,
    long Objects,
    ulong Retained,
    ulong Bytes,
    int Children,
    bool Expanded);
