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

    /// <summary>Every object of the types of a namespace (<see cref="ObjectSetSpec.Name"/>, as the namespace rows name it).</summary>
    Namespace = 1,

    /// <summary>Every object of the types of an assembly, by module file name (<see cref="ObjectSetSpec.Name"/>).</summary>
    Assembly = 2,

    /// <summary>The objects at <see cref="ObjectSetSpec.Addresses"/> (bookmarks); addresses with no object are left out.</summary>
    Objects = 3,

    /// <summary>The objects a row of an inspection counted (<see cref="ObjectSetSpec.Name"/>, <see cref="ObjectSetSpec.Row"/>).</summary>
    InspectionRow = 4,

    /// <summary>The objects <see cref="ObjectSetSpec.Inner"/> retains, its own reachable members included.</summary>
    RetainedBy = 5,

    /// <summary>
    /// Every object of the types implementing an interface (sprint 3.2; <see cref="ObjectSetSpec.Name"/> as the interface
    /// rows name it, or <see cref="TypeSummaryColumns.NoInterface"/>). Full dumps only.
    /// </summary>
    Interface = 6,

    /// <summary>
    /// The members of a Similar Retention group of <see cref="ObjectSetSpec.Inner"/> (sprint 3.2): the group whose pattern
    /// has the fingerprint <see cref="ObjectSetSpec.Name"/> (<see cref="RetentionGroupDto.Key"/>); <see cref="ObjectSetSpec.Row"/>
    /// is the rank it was listed at, for the log and as a hint.
    /// </summary>
    RetentionGroup = 7,

    /// <summary>
    /// The members of <see cref="ObjectSetSpec.Inner"/> that are new, dead or survived (<see cref="ObjectSetSpec.Change"/>)
    /// against the other snapshot <see cref="ObjectSetSpec.Baseline"/>, matched by address and type (sprint 3.3, ADR-032).
    /// New and survived objects live in the later snapshot, dead ones in the earlier: the set is queried there.
    /// </summary>
    Compared = 8,

    /// <summary>
    /// The objects no root reaches (sprint 3.5): garbage a full dump still holds because no collection has removed it yet.
    /// Full dumps only (a gcdump's collection removed them).
    /// </summary>
    Unreachable = 9,
}

/// <summary>How an object fared between two snapshots of one process, matched by address and type (ADR-032).</summary>
public enum ObjectChange
{
    /// <summary>In the later snapshot with no object of its type at its address in the earlier one.</summary>
    New = 0,

    /// <summary>In the earlier snapshot with no object of its type at its address in the later one.</summary>
    Dead = 1,

    /// <summary>In the later snapshot and at the same address, with the same type, in the earlier one.</summary>
    Survived = 2,
}

/// <summary>A type by name and module file name (null or empty for none), the key of the summary rows (ADR-018).</summary>
public sealed record TypeKeyDto(string Name, string? Module);

/// <summary>
/// An object set (sprint 3.1, ADR-030): what it selects, not its members. The client keeps it (the analysis path replays
/// it) and sends it with each query; the worker resolves it into the members of one snapshot and caches them. Use the
/// factory methods.
/// </summary>
/// <param name="Types">The type keys of <see cref="ObjectSetKind.Types"/>.</param>
/// <param name="Name">
/// The namespace as the summary's namespace rows name it ("(global)" for types without one), the assembly's module file
/// name, the inspection's name, or the interface as the interface rows name it.
/// </param>
/// <param name="Addresses">The objects of <see cref="ObjectSetKind.Objects"/>, at most <see cref="ObjectSetLimits.MaxAddresses"/>.</param>
/// <param name="Row">The inspection row's index, in the order the inspection lists its rows; a Similar Retention group's rank.</param>
/// <param name="Baseline">
/// The other snapshot: an inspection row's baseline (a detector that uses one), or what a <see cref="ObjectSetKind.Compared"/>
/// set is compared with (sprint 3.3, the 3.1 review's M6).
/// </param>
/// <param name="Change">What a <see cref="ObjectSetKind.Compared"/> set selects.</param>
/// <param name="Inner">The set whose retained objects <see cref="ObjectSetKind.RetainedBy"/> selects, or whose Similar Retention group <see cref="ObjectSetKind.RetentionGroup"/> selects.</param>
public sealed record ObjectSetSpec(
    ObjectSetKind Kind,
    IReadOnlyList<TypeKeyDto>? Types = null,
    string? Name = null,
    IReadOnlyList<ulong>? Addresses = null,
    int Row = -1,
    Guid? Baseline = null,
    ObjectSetSpec? Inner = null,
    ObjectChange? Change = null)
{
    public static ObjectSetSpec OfTypes(IEnumerable<TypeKeyDto> types) => new(ObjectSetKind.Types, Types: [.. types]);

    public static ObjectSetSpec OfType(string name, string? module) => OfTypes([new TypeKeyDto(name, module)]);

    public static ObjectSetSpec OfNamespace(string name) => new(ObjectSetKind.Namespace, Name: name);

    public static ObjectSetSpec OfAssembly(string module) => new(ObjectSetKind.Assembly, Name: module);

    public static ObjectSetSpec OfObjects(IEnumerable<ulong> addresses) => new(ObjectSetKind.Objects, Addresses: [.. addresses]);

    public static ObjectSetSpec OfInspectionRow(string inspection, int row, Guid? baseline = null) =>
        new(ObjectSetKind.InspectionRow, Name: inspection, Row: row, Baseline: baseline);

    public static ObjectSetSpec RetainedByOf(ObjectSetSpec inner) => new(ObjectSetKind.RetainedBy, Inner: inner);

    public static ObjectSetSpec OfInterface(string name) => new(ObjectSetKind.Interface, Name: name);

    /// <summary>The <paramref name="change"/> objects of <paramref name="inner"/> against <paramref name="other"/> (sprint 3.3).</summary>
    public static ObjectSetSpec ComparedOf(ObjectSetSpec inner, ObjectChange change, Guid? other = null) =>
        new(ObjectSetKind.Compared, Baseline: other, Inner: inner, Change: change);

    /// <summary>The objects no root reaches (sprint 3.5).</summary>
    public static ObjectSetSpec OfUnreachable() => new(ObjectSetKind.Unreachable);

    public static ObjectSetSpec RetentionGroupOf(ObjectSetSpec inner, RetentionGroupDto group) =>
        new(ObjectSetKind.RetentionGroup, Name: group?.Key ?? throw new ArgumentNullException(nameof(group)), Row: group.Rank, Inner: inner);

}

/// <summary>
/// Texts computed from an <see cref="ObjectSetSpec"/> (sprint 3.1 review): kept off the record, as extension members, so
/// the JSON-RPC formatter never sends them (ADR-016: DTOs are plain data).
/// </summary>
public static class ObjectSetSpecs
{
    /// <summary>Type keys the log names before it counts the rest.</summary>
    public const int ShapeTypes = 20;

    extension(ObjectSetSpec spec)
    {
        /// <summary>
        /// The spec for the log: its kind, the type keys (the first <see cref="ShapeTypes"/>), the namespace or assembly,
        /// the inspection and row, and for objects their count and address hash. Type, namespace, assembly
        /// and inspection names are metadata (ARCHITECTURE.md 11); addresses are never written.
        /// </summary>
        public string Shape => spec.Kind switch
        {
            ObjectSetKind.Types => $"types ({spec.Types?.Count ?? 0}): {string.Join(", ", (spec.Types ?? []).Take(ShapeTypes).Select(t => string.IsNullOrEmpty(t.Module) ? t.Name : $"{t.Name} [{t.Module}]"))}{((spec.Types?.Count ?? 0) > ShapeTypes ? $" and {spec.Types!.Count - ShapeTypes} more" : string.Empty)}",
            ObjectSetKind.Namespace => $"namespace {spec.Name}",
            ObjectSetKind.Assembly => $"assembly {spec.Name}",
            ObjectSetKind.Objects => $"objects ({spec.Addresses?.Count ?? 0} addresses, #{spec.AddressHash})",
            ObjectSetKind.InspectionRow => $"inspection {spec.Name} row {spec.Row}{(spec.Baseline is null ? string.Empty : " with baseline " + spec.Baseline)}",
            ObjectSetKind.RetainedBy => $"retained by {spec.Inner?.Shape}",
            ObjectSetKind.Interface => $"interface {spec.Name}",
            ObjectSetKind.RetentionGroup => $"similar retention group {spec.Row + 1} (pattern #{spec.Name}) of {spec.Inner?.Shape}",
            ObjectSetKind.Compared => $"{spec.Change?.ToString().ToLowerInvariant()} objects (by address) of {spec.Inner?.Shape} compared with snapshot {spec.Baseline}",
            ObjectSetKind.Unreachable => "objects no root reaches",
            _ => spec.Kind.ToString(),
        };

        /// <summary>
        /// A short hash of the distinct addresses in order: the log and the cache name an objects set by it, so support can
        /// match it to the bookmarks it came from without the addresses in the log.
        /// </summary>
        public string AddressHash
        {
            get
            {
                var sorted = (spec.Addresses ?? []).Distinct().Order().ToArray();
                var bytes = new byte[sorted.Length * sizeof(ulong)];
                for (var i = 0; i < sorted.Length; i++)
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(ulong)), sorted[i]);
                }

                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 8);
            }
        }

        /// <summary>
        /// A key equal for equal specs (lists compare by value), for caches. Every kind has its own arm: a kind without one
        /// is refused, so two sets of a new kind never share a key (the 3.1 review).
        /// </summary>
        public string CacheKey => spec.Kind switch
        {
            ObjectSetKind.Types => "T|" + string.Join("|", (spec.Types ?? []).Select(t => t.Name + "\u0001" + (t.Module ?? string.Empty)).Order(StringComparer.Ordinal)),
            ObjectSetKind.Namespace => "N|" + spec.Name,
            ObjectSetKind.Assembly => "A|" + spec.Name?.ToUpperInvariant(),
            ObjectSetKind.Objects => $"O|{spec.Addresses?.Distinct().Count() ?? 0}|{spec.AddressHash}",
            ObjectSetKind.InspectionRow => $"I|{spec.Name?.ToLowerInvariant()}|{spec.Row}|{spec.Baseline}",
            ObjectSetKind.RetainedBy => "R|" + spec.Inner?.CacheKey,
            ObjectSetKind.Interface => "F|" + spec.Name,
            ObjectSetKind.RetentionGroup => $"G|{spec.Name}|{spec.Inner?.CacheKey}",
            ObjectSetKind.Compared => $"C|{spec.Change}|{spec.Baseline}|{spec.Inner?.CacheKey}",
            ObjectSetKind.Unreachable => "U",
            _ => throw new ArgumentException($"Unknown set kind {(int)spec.Kind}."),
        };
    }
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
