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

namespace HeapScope.Contracts;

/// <summary>
/// Ids of the optional features a snapshot lists in <see cref="SnapshotDescriptor.Availability"/>. Ids are strings, so a
/// new feature is not a protocol change; a front end treats an id the worker did not list as unavailable.
/// </summary>
public static class SnapshotFeatureIds
{
    /// <summary>The objects no root reaches, as a set: a full dump with a complete object graph.</summary>
    public const string UnreachableSet = "unreachable-set";

    /// <summary>Grouping types by the interfaces they implement, read from the dump.</summary>
    public const string InterfaceGrouping = "interface-grouping";

    /// <summary>An object's field values, and the fields named in references, referrers and paths.</summary>
    public const string FieldValues = "field-values";

    /// <summary>String text: a string's value, and value filters and sorts on strings.</summary>
    public const string StringValues = "string-values";

    /// <summary>Gen0, Gen1, Gen2, LOH and POH per object.</summary>
    public const string Generations = "generations";

    /// <summary>The finalizable scope of a type's objects (objects registered for finalization).</summary>
    public const string FinalizableScope = "finalizable-scope";

    /// <summary>Every id this version of HeapScope lists, in a stable order.</summary>
    public static IReadOnlyList<string> All { get; } = [UnreachableSet, InterfaceGrouping, FieldValues, StringValues, Generations, FinalizableScope];
}
