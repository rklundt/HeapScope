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

namespace HeapScope.Core;

/// <summary>What an <see cref="ObjectValue"/> is.</summary>
public enum ObjectValueKind
{
    /// <summary>A value the reader could not interpret.</summary>
    Unknown = 0,

    /// <summary>A null reference.</summary>
    Null = 1,

    Boolean = 2,

    Char = 3,

    /// <summary>A signed integer (<see cref="ObjectValue.Integer"/>), or an enum (<see cref="ObjectValue.EnumMembers"/> set).</summary>
    Integer = 4,

    /// <summary>An unsigned integer (<see cref="ObjectValue.Unsigned"/>), or an enum over an unsigned type.</summary>
    Unsigned = 5,

    /// <summary>A <c>float</c> or <c>double</c> (<see cref="ObjectValue.Floating"/>; <see cref="ObjectValue.TypeName"/> says which).</summary>
    Floating = 6,

    /// <summary>A native integer or pointer (<see cref="ObjectValue.Unsigned"/>).</summary>
    Pointer = 7,

    /// <summary>A string: <see cref="ObjectValue.Text"/> (possibly cut) and its full <see cref="ObjectValue.TextLength"/>.</summary>
    String = 8,

    /// <summary>A reference to another object at <see cref="ObjectValue.Address"/> of runtime type <see cref="ObjectValue.TypeName"/>.</summary>
    Reference = 9,

    /// <summary>An array at <see cref="ObjectValue.Address"/> with <see cref="ObjectValue.Dimensions"/>; elements are the field's children.</summary>
    Array = 10,

    /// <summary>An embedded value type of <see cref="ObjectValue.TypeName"/>; its fields are the field's children.</summary>
    Struct = 11,

    /// <summary>A boxed primitive or enum at <see cref="ObjectValue.Address"/>; <see cref="ObjectValue.Boxed"/> is the value inside.</summary>
    Boxed = 12,

    /// <summary>A non-zero reference to something that is not an object in this dump (<see cref="ObjectValue.Address"/>).</summary>
    Invalid = 13,

    /// <summary>Elements not listed: <see cref="ObjectValue.Unsigned"/> of them.</summary>
    More = 14,
}

/// <summary>A named constant of an enum type.</summary>
public readonly record struct EnumMember(string Name, ulong Value);

/// <summary>
/// A value read from a dump, typed (sprint 1.6, from the 1.5 review): the reader reports what is there and the worker
/// formats it for display, so analyses can use the numbers and the formatting is testable without a dump.
/// </summary>
public sealed record ObjectValue(ObjectValueKind Kind)
{
    /// <summary>The runtime type (references, arrays, boxes), the struct type, or the field's primitive or enum type.</summary>
    public string? TypeName { get; init; }

    /// <summary>An array's element type.</summary>
    public string? ElementTypeName { get; init; }

    public long Integer { get; init; }

    public ulong Unsigned { get; init; }

    public double Floating { get; init; }

    public bool Boolean { get; init; }

    public char Char { get; init; }

    /// <summary>A string's text, at most the reader's cut.</summary>
    public string? Text { get; init; }

    /// <summary>A string's full length in characters.</summary>
    public int TextLength { get; init; }

    public ulong Address { get; init; }

    /// <summary>An array's length per dimension.</summary>
    public IReadOnlyList<int>? Dimensions { get; init; }

    /// <summary>The enum's constants when the value is an enum.</summary>
    public IReadOnlyList<EnumMember>? EnumMembers { get; init; }

    /// <summary>The value inside a box.</summary>
    public ObjectValue? Boxed { get; init; }

    /// <summary>An integer or enum value as raw bits (sign-extended for signed values), for enum names and flags.</summary>
    public ulong Bits => Kind == ObjectValueKind.Integer ? unchecked((ulong)Integer) : Unsigned;

    /// <summary>An array's total element count.</summary>
    public long ElementCount => Dimensions is null ? 0 : Dimensions.Aggregate(1L, (total, length) => total * length);

    public static ObjectValue Null { get; } = new(ObjectValueKind.Null);
}

/// <summary>
/// One field of an inspected object, or one array element (sprint 1.5, typed since 1.6). Compiler backing fields are
/// named by their property; array elements by their index (<c>[3]</c>, <c>[1,2]</c>).
/// </summary>
/// <param name="DeclaredType">The field's declared type (the element type for array elements).</param>
public sealed record ObjectField(string Name, string DeclaredType, ObjectValue Value, IReadOnlyList<ObjectField> Children);

/// <summary>
/// Receives a string's text piece by piece (sprint 1.6 review H2): <paramref name="length"/> is the string's full length
/// in characters, or -1 when the address is not a readable string (then once, with an empty piece).
/// </summary>
public delegate void StringPieceHandler(int index, int length, ReadOnlySpan<char> piece, bool last);

/// <summary>
/// Reads values from the dump a snapshot came from (capabilities <see cref="SnapshotCapabilities.FieldValues"/> and
/// <see cref="SnapshotCapabilities.StringContents"/>), obtained with <see cref="ISnapshot.TryGetService{TService}"/>.
/// Implementations are safe to call from several threads.
/// </summary>
public interface IObjectReader
{
    /// <summary>
    /// The fields of the object at <paramref name="address"/> as a tree (nested structs as children); for an array, its
    /// first <paramref name="maxElements"/> elements, then a <see cref="ObjectValueKind.More"/> row. Strings are cut at
    /// <paramref name="maxText"/> characters.
    /// </summary>
    IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, int maxText, CancellationToken cancellationToken);

    /// <summary>
    /// One value by a dotted path from the object at <paramref name="address"/>: field names (raw or as displayed),
    /// through embedded structs and referenced objects (<c>_items</c>, <c>Box.TopLeft.X</c>, <c>Inner.Target</c>). Null
    /// when a step does not exist or a reference on the way is null. Strings are cut at <paramref name="maxText"/>.
    /// </summary>
    ObjectValue? ReadField(ulong address, string path, int maxText = 1000);

    /// <summary>The text of the string at <paramref name="address"/>, at most <paramref name="maxLength"/> characters; null when it is not a string.</summary>
    string? ReadString(ulong address, int maxLength);

    /// <summary>
    /// The text of many strings, at most <paramref name="maxLength"/> characters each, handed to <paramref name="onEach"/>
    /// with each address's position (null for an address that is not a readable string). Cheaper than one call per
    /// string: the reader is entered once per chunk.
    /// </summary>
    void ReadStrings(IReadOnlyList<ulong> addresses, int maxLength, Action<int, string?> onEach, CancellationToken cancellationToken);

    /// <summary>
    /// The full text of many strings without building them: each string arrives in pieces of a pooled buffer (valid only
    /// during the call), in order, the last with <c>last</c> set. For strings of any size, such as hashing every copy
    /// of a large payload; <see cref="ReadStrings"/> materializes its texts and suits short cuts only.
    /// </summary>
    void ReadStringPieces(IReadOnlyList<ulong> addresses, StringPieceHandler onPiece, CancellationToken cancellationToken);

    /// <summary>
    /// Where the object at <paramref name="address"/> references each object it references: a field path
    /// (<c>Inner.Target</c>) or an array index (<c>[12]</c>, <c>[3].Value</c>). Several paths to one target are joined
    /// with ", ": the first three and a count of the rest ("[0], [1], [2] and 997 more"). At most <paramref name="maxElements"/> array elements are walked.
    /// </summary>
    IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken);
}
