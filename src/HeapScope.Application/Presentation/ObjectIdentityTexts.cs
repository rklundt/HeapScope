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

/// <summary>Texts about objects matched across two snapshots by address, said the same way by the app and the CLI.</summary>
public static class ObjectIdentityTexts
{
    /// <summary>
    /// The new and dead objects of a comparison and what they mean: a collection that moved an object makes it dead and
    /// new. Numbers in <paramref name="provider"/>'s format.
    /// </summary>
    public static string Matched(long newObjects, long deadObjects, IFormatProvider provider) => string.Create(
        provider,
        $"{newObjects:N0} new and {deadObjects:N0} dead objects, matched by address: an object a collection moved counts as dead and new (Gen0 and Gen1 survivors usually move).");
}
