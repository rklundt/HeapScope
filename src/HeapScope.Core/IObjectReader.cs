namespace HeapScope.Core;

/// <summary>What an <see cref="ObjectField"/> holds.</summary>
public enum ObjectFieldKind
{
    /// <summary>A primitive or enum value, formatted in <see cref="ObjectField.Value"/>.</summary>
    Primitive = 0,

    /// <summary>A string; <see cref="ObjectField.Value"/> is its text, quoted (truncated when long).</summary>
    String = 1,

    /// <summary>A null reference.</summary>
    Null = 2,

    /// <summary>A reference to another object at <see cref="ObjectField.Address"/>.</summary>
    Reference = 3,

    /// <summary>An embedded value type; its fields are the children.</summary>
    Struct = 4,

    /// <summary>An array (a reference to it, or the inspected object itself); elements are the children.</summary>
    Array = 5,

    /// <summary>A placeholder for elements not listed ("N more").</summary>
    More = 6,
}

/// <summary>
/// One field of an inspected object, or one array element (sprint 1.5). Values are formatted for display in the
/// invariant culture: strings quoted, <c>null</c>, chars in single quotes, booleans lower case, enums as
/// <c>Name (value)</c>, arrays as <c>elementType[length]</c>. Compiler backing fields are named by their property.
/// </summary>
public sealed record ObjectField(string Name, string TypeName, ObjectFieldKind Kind, string Value, ulong Address, IReadOnlyList<ObjectField> Children);

/// <summary>
/// Reads values from the dump a snapshot came from (capabilities <see cref="SnapshotCapabilities.FieldValues"/> and
/// <see cref="SnapshotCapabilities.StringContents"/>), obtained with <see cref="ISnapshot.TryGetService{TService}"/>.
/// Implementations are safe to call from several threads.
/// </summary>
public interface IObjectReader
{
    /// <summary>
    /// The fields of the object at <paramref name="address"/> as a tree (nested structs as children); for an array, its
    /// first <paramref name="maxElements"/> elements, then a <see cref="ObjectFieldKind.More"/> row.
    /// </summary>
    IReadOnlyList<ObjectField> ReadFields(ulong address, int maxElements, CancellationToken cancellationToken);

    /// <summary>The text of the string at <paramref name="address"/>, at most <paramref name="maxLength"/> characters; null when it is not a string.</summary>
    string? ReadString(ulong address, int maxLength);

    /// <summary>
    /// Where the object at <paramref name="address"/> references each object it references: a field path
    /// (<c>Inner.Target</c>) or an array index (<c>[12]</c>, <c>[3].Value</c>). Several paths to one target are joined
    /// with ", ": the first three and a count of the rest ("[0], [1], [2] and 997 more"). At most <paramref name="maxElements"/> array elements are walked.
    /// </summary>
    IReadOnlyDictionary<ulong, string> ReadReferencePaths(ulong address, int maxElements, CancellationToken cancellationToken);
}
