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

namespace HeapScope.Core.Loading;

/// <summary>
/// Data quality facts found while loading, shown to the user as notes (ARCHITECTURE.md 6.1). Never contains field
/// values or string contents.
/// </summary>
/// <param name="DebuggableModules">Modules compiled as debuggable code: stack roots in their frames can be missing.</param>
/// <param name="UnreadableObjects">Heap entries skipped because their type could not be read.</param>
/// <param name="StaticFieldRoots">Static field roots found by enumerating statics.</param>
/// <param name="IncompleteSegments">Heap segments whose walk covered less than their object range (missing memory).</param>
/// <param name="Dac">The data access component that read the dump and why (full dumps only, sprint 2.3.7).</param>
public sealed record LoadDiagnostics(IReadOnlyList<string> DebuggableModules, long UnreadableObjects, int StaticFieldRoots, int IncompleteSegments = 0, DacChoice? Dac = null);
