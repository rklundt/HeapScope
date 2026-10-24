using System.Globalization;
using HeapScope.Contracts;
using HeapScope.Core;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Display text for values read from a dump (sprint 1.6; the reader returns typed values). Invariant culture throughout:
/// strings quoted and cut with "..." when longer than the reader's cut, <c>null</c>, chars in single quotes, booleans
/// lower case, enums as <c>Name (value)</c> and flag combinations as <c>Read, Write (3)</c>, boxed primitives as
/// <c>12345 (boxed int)</c>, arrays as <c>int[5]</c> or <c>int[2,3]</c>, C# aliases for primitive type names.
/// </summary>
internal static class ObjectFormatter
{
    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.Ordinal)
    {
        ["System.Boolean"] = "bool",
        ["System.Char"] = "char",
        ["System.SByte"] = "sbyte",
        ["System.Byte"] = "byte",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "uint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong",
        ["System.Single"] = "float",
        ["System.Double"] = "double",
        ["System.String"] = "string",
        ["System.Object"] = "object",
        ["System.IntPtr"] = "nint",
        ["System.UIntPtr"] = "nuint",
    };

    public static ObjectFieldDto ToDto(ObjectField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var value = field.Value;
        var typeName = value.Kind is ObjectValueKind.Array or ObjectValueKind.Struct or ObjectValueKind.Boxed ? value.TypeName ?? field.DeclaredType : field.DeclaredType;
        return new ObjectFieldDto(field.Name, typeName, KindOf(value), Format(value), LinkOf(value), [.. field.Children.Select(ToDto)]);
    }

    public static string Format(ObjectValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            ObjectValueKind.Null => "null",
            ObjectValueKind.Boolean => value.Boolean ? "true" : "false",
            ObjectValueKind.Char => "'" + value.Char + "'",
            ObjectValueKind.Integer or ObjectValueKind.Unsigned => Number(value),
            ObjectValueKind.Floating => value.TypeName == "System.Single"
                ? ((float)value.Floating).ToString("R", CultureInfo.InvariantCulture)
                : value.Floating.ToString("R", CultureInfo.InvariantCulture),
            ObjectValueKind.Pointer => Hex(value.Unsigned),
            ObjectValueKind.String => value.Text is null
                ? "null"
                : "\"" + value.Text + (value.TextLength > value.Text.Length ? "..." : string.Empty) + "\"",
            ObjectValueKind.Reference => value.TypeName ?? "?",
            ObjectValueKind.Array => string.Create(CultureInfo.InvariantCulture,
                $"{Alias(value.ElementTypeName)}[{string.Join(",", value.Dimensions ?? [])}]"),
            ObjectValueKind.Struct => Alias(value.TypeName),
            ObjectValueKind.Boxed => value.Boxed is { } inner ? $"{Format(inner)} (boxed {Alias(value.TypeName)})" : Alias(value.TypeName),
            ObjectValueKind.Invalid => Hex(value.Address) + " (not a valid object in this dump)",
            ObjectValueKind.More => string.Create(CultureInfo.InvariantCulture, $"{value.Unsigned:N0} more"),
            _ => "?",
        };
    }

    /// <summary>C# keyword for primitive type names, the name itself otherwise.</summary>
    public static string Alias(string? typeName) =>
        typeName is null ? "?" : s_aliases.TryGetValue(typeName, out var alias) ? alias : typeName;

    /// <summary>A number, or an enum by its constant's name, or a flag combination by the names that make it up.</summary>
    private static string Number(ObjectValue value)
    {
        var number = value.Kind == ObjectValueKind.Integer
            ? value.Integer.ToString(CultureInfo.InvariantCulture)
            : value.Unsigned.ToString(CultureInfo.InvariantCulture);
        if (value.EnumMembers is not { Count: > 0 } members)
        {
            return number;
        }

        var bits = value.Bits;
        foreach (var member in members)
        {
            if (member.Value == bits)
            {
                return $"{member.Name} ({number})";
            }
        }

        // Like Enum.ToString for [Flags]: the largest constants first, until every bit is named; else just the number.
        var remaining = bits;
        var names = new List<EnumMember>();
        foreach (var member in members.Where(m => m.Value != 0).OrderByDescending(m => m.Value))
        {
            if ((remaining & member.Value) == member.Value)
            {
                names.Add(member);
                remaining &= ~member.Value;
            }
        }

        return remaining == 0 && names.Count > 1
            ? $"{string.Join(", ", names.OrderBy(m => m.Value).Select(m => m.Name))} ({number})"
            : number;
    }

    private static ObjectFieldKindDto KindOf(ObjectValue value) => value.Kind switch
    {
        ObjectValueKind.Null => ObjectFieldKindDto.Null,
        ObjectValueKind.String => ObjectFieldKindDto.String,
        ObjectValueKind.Reference or ObjectValueKind.Boxed => ObjectFieldKindDto.Reference,
        ObjectValueKind.Array => ObjectFieldKindDto.Array,
        ObjectValueKind.Struct => ObjectFieldKindDto.Struct,
        ObjectValueKind.More => ObjectFieldKindDto.More,
        _ => ObjectFieldKindDto.Primitive,
    };

    /// <summary>The object a field links to: references, boxes, strings and arrays; 0 for values.</summary>
    private static ulong LinkOf(ObjectValue value) =>
        value.Kind is ObjectValueKind.Reference or ObjectValueKind.Boxed or ObjectValueKind.String or ObjectValueKind.Array ? value.Address : 0;

    private static string Hex(ulong value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:x}");
}
