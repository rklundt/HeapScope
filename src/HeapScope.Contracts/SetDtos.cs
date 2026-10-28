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

// DTOs are records of primitives, strings, arrays and Contracts enums only (ARCHITECTURE.md 4.4, ADR-016).
// Object sets (sprint 3.1, ADR-030).

/// <summary>What an <see cref="ObjectSetSpec"/> selects.</summary>
public enum ObjectSetKind
{
    /// <summary>Every object of one or more types, each by name and module (<see cref="ObjectSetSpec.Types"/>).</summary>
    Types = 0,

    /// <summary>Every object of the types of a namespace (<see cref="ObjectSetSpec.Name"/>; empty for none).</summary>
    Namespace = 1,

    /// <summary>Every object of the types of an assembly, by module file name (<see cref="ObjectSetSpec.Name"/>).</summary>
    Assembly = 2,

    /// <summary>The objects at <see cref="ObjectSetSpec.Addresses"/> (bookmarks); addresses with no object are left out.</summary>
    Objects = 3,

    /// <summary>The objects a row of an inspection counted (<see cref="ObjectSetSpec.Name"/>, <see cref="ObjectSetSpec.Row"/>).</summary>
    InspectionRow = 4,

    /// <summary>The objects <see cref="ObjectSetSpec.Inner"/> retains, its own reachable members included.</summary>
    RetainedBy = 5,
}

/// <summary>A type by name and module file name (null or empty for none), the key of the summary rows (ADR-018).</summary>
public sealed record TypeKeyDto(string Name, string? Module);

/// <summary>
/// An object set (sprint 3.1, ADR-030): what it selects, not its members. The client keeps it (the analysis path replays
/// it) and sends it with each query; the worker resolves it into the members of one snapshot and caches them. Use the
/// factory methods.
/// </summary>
/// <param name="Types">The type keys of <see cref="ObjectSetKind.Types"/>.</param>
/// <param name="Name">The namespace, the assembly's module, or the inspection's name.</param>
/// <param name="Addresses">The objects of <see cref="ObjectSetKind.Objects"/>, at most <see cref="ObjectSetLimits.MaxAddresses"/>.</param>
/// <param name="Row">The inspection row's index, in the order the inspection lists its rows.</param>
/// <param name="Baseline">The inspection's baseline snapshot, for the rows of a detector that uses one.</param>
/// <param name="Inner">The set whose retained objects <see cref="ObjectSetKind.RetainedBy"/> selects.</param>
public sealed record ObjectSetSpec(
    ObjectSetKind Kind,
    IReadOnlyList<TypeKeyDto>? Types = null,
    string? Name = null,
    IReadOnlyList<ulong>? Addresses = null,
    int Row = -1,
    Guid? Baseline = null,
    ObjectSetSpec? Inner = null)
{
    public static ObjectSetSpec OfTypes(IEnumerable<TypeKeyDto> types) => new(ObjectSetKind.Types, Types: [.. types]);

    public static ObjectSetSpec OfType(string name, string? module) => OfTypes([new TypeKeyDto(name, module)]);

    public static ObjectSetSpec OfNamespace(string name) => new(ObjectSetKind.Namespace, Name: name);

    public static ObjectSetSpec OfAssembly(string module) => new(ObjectSetKind.Assembly, Name: module);

    public static ObjectSetSpec OfObjects(IEnumerable<ulong> addresses) => new(ObjectSetKind.Objects, Addresses: [.. addresses]);

    public static ObjectSetSpec OfInspectionRow(string inspection, int row, Guid? baseline = null) =>
        new(ObjectSetKind.InspectionRow, Name: inspection, Row: row, Baseline: baseline);

    public static ObjectSetSpec RetainedByOf(ObjectSetSpec inner) => new(ObjectSetKind.RetainedBy, Inner: inner);

    /// <summary>The spec's shape for the log (kind, how many keys or addresses, the inspection and row), never addresses.</summary>
    public string Shape => Kind switch
    {
        ObjectSetKind.Types => $"types ({Types?.Count ?? 0})",
        ObjectSetKind.Namespace => "namespace",
        ObjectSetKind.Assembly => "assembly",
        ObjectSetKind.Objects => $"objects ({Addresses?.Count ?? 0} addresses)",
        ObjectSetKind.InspectionRow => $"inspection {Name} row {Row}{(Baseline is null ? string.Empty : " with a baseline")}",
        ObjectSetKind.RetainedBy => $"retained by {Inner?.Shape}",
        _ => Kind.ToString(),
    };

    /// <summary>A key equal for equal specs (lists compare by value), for caches.</summary>
    public string CacheKey => Kind switch
    {
        ObjectSetKind.Types => "T|" + string.Join("|", (Types ?? []).Select(t => t.Name + "\u0001" + (t.Module ?? string.Empty)).Order(StringComparer.Ordinal)),
        ObjectSetKind.Objects => "O|" + string.Join(",", (Addresses ?? []).Order().Distinct()),
        ObjectSetKind.InspectionRow => $"I|{Name}|{Row}|{Baseline}",
        ObjectSetKind.RetainedBy => "R|" + Inner?.CacheKey,
        _ => $"{(int)Kind}|{Name}",
    };
}

/// <summary>Bounds on object sets.</summary>
public static class ObjectSetLimits
{
    /// <summary>Addresses an <see cref="ObjectSetKind.Objects"/> spec may name.</summary>
    public const int MaxAddresses = 100_000;

    /// <summary>Type keys an <see cref="ObjectSetKind.Types"/> spec may name.</summary>
    public const int MaxTypes = 10_000;

    /// <summary>How deep <see cref="ObjectSetKind.RetainedBy"/> specs may nest.</summary>
    public const int MaxDepth = 4;
}

/// <summary>How big a resolved set is: its members, their bytes and how many distinct types they have.</summary>
public sealed record ObjectSetInfo(long Objects, long Bytes, int Types);

/// <summary>Column names of a set's dominators result set (sprint 3.1).</summary>
public static class SetDominatorColumns
{
    public const string Address = "Address";
    public const string Type = "Type";
    public const string Bytes = "Bytes";

    /// <summary>What the member retains (exact): everything below it in the dominator tree, other members included.</summary>
    public const string Retained = "Retained";

    /// <summary>Other members below it in the dominator tree.</summary>
    public const string Members = "Members";
}

/// <summary>The members of a set that no other member dominates (sprint 3.1): what the set's retention divides into.</summary>
public sealed record SetDominatorsRequest(Guid SnapshotId, ObjectSetSpec Set);

/// <summary>
/// The answer to <see cref="SetDominatorsRequest"/>: one row per top member, most retained first; the set; the top
/// members' count and the bytes they retain together (no two overlap, so the sum is exact); the members no root reaches;
/// and how the dominators were obtained.
/// </summary>
public sealed record SetDominatorsResult(
    ResultSetInfo Rows,
    ObjectSetInfo Set,
    int TopMembers,
    long TopRetained,
    long UnreachableMembers,
    DominatorIndexInfo Dominators);
