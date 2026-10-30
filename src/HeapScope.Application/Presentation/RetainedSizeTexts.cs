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

/// <summary>Texts about retained sizes, said the same way by the app and the CLI.</summary>
public static class RetainedSizeTexts
{
    /// <summary>
    /// How the dominators were obtained and how much of the heap the roots reach, in one line, with the worker's caveat
    /// (an incomplete snapshot, a gcdump's unrooted objects) when it has one. Numbers in <paramref name="provider"/>'s
    /// format: the current culture in the app, the invariant culture in the CLI.
    /// </summary>
    public static string Source(DominatorIndexInfo dominators, IFormatProvider provider)
    {
        ArgumentNullException.ThrowIfNull(dominators);
        return string.Create(
            provider,
            $"Retained sizes: dominators {ValueBoundTexts.DominatorsSource(dominators.FromSidecar)} in {dominators.Seconds:N1} s, {dominators.ReachableObjects:N0} objects reachable from the roots.")
            + (dominators.Note is { } note ? " " + note : string.Empty);
    }
}
