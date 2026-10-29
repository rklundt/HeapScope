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
/// A flattened dominator tree (sprint 2.4): its rows in display order, whether they were cut at
/// <see cref="DominatorTreeRows.MaxRows"/>, the focus's ancestors from the top (object indices; a long trail keeps its top
/// and its nearest, <see cref="DominatorTreeRows.TrailHead"/> and <see cref="DominatorTreeRows.TrailTail"/>) and how
/// many there are, the bytes the focus retains and the reachable heap's total.
/// </summary>
public sealed record DominatorTreeView(IReadOnlyList<DominatorRow> Rows, bool Truncated, IReadOnlyList<int> Ancestors, int AncestorDepth, ulong FocusRetained, ulong TotalRetained);
