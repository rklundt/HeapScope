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

namespace HeapScope.Application.Presentation;

/// <summary>
/// Monitor rules as the user wrote them; a null field sets no rule (a front end passes null for an empty form field).
/// </summary>
/// <param name="Every">A snapshot every interval (the grammar's interval).</param>
/// <param name="Above">A snapshot when the GC heap exceeds a size (500MB).</param>
/// <param name="Growth">A snapshot when the GC heap grew by a size or a percent over the first sample.</param>
/// <param name="Max">At most this many snapshots (a whole number).</param>
/// <param name="Rearm">After a size rule fired: once, below (the default) or step.</param>
public sealed record MonitorRulesInput(string? Every = null, string? Above = null, string? Growth = null, string? Max = null, string? Rearm = null);
