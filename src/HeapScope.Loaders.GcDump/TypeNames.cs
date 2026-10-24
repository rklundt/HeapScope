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

using System.Text;
using System.Text.RegularExpressions;
using HeapScope.Core.Graph;

namespace HeapScope.Loaders.GcDump;

/// <summary>
/// Type names of a gcdump in the form the full dump loader writes them (sprint 1.8), as far as they can be recovered.
/// The gcdump writer changes names in four ways: generic arguments are joined by "," without a space, "&lt;&gt;" in
/// compiler names becomes "[]" ("[]c" for "&lt;&gt;c"), arrays above 1 KB and 10 KB get their own nodes named
/// "T[] (Bytes &gt; 10K)", and nested types lose their declaring type ("Entry&lt;K, V&gt;[]" for
/// "Dictionary&lt;K, V&gt;+Entry[]"). The first three are undone here; the last cannot be (see
/// <see cref="TypeNameForms.AsGcDumpName"/> for comparing such names).
/// </summary>
public static partial class TypeNames
{
    /// <summary>Bytes the writer adds to a runtime callable wrapper for its native part.</summary>
    public const ulong RcwOverhead = 1000;

    private const string SizeBucket = " (Bytes > ";
    private const string RcwPrefix = "[RCW ";

    /// <summary>A COM callable wrapper pseudo node: "[CCW 0x... for type X]".</summary>
    public static bool IsComCallableWrapper(string recorded) =>
        recorded.StartsWith("[CCW ", StringComparison.Ordinal) && recorded.EndsWith(']');

    /// <summary>A managed object wrapping a COM object: "[RCW X]", or "[RCW X] (Bytes > 1K)" for a large one.</summary>
    public static bool IsRuntimeCallableWrapper(string recorded)
    {
        var name = WithoutSizeBucket(recorded);
        return name.StartsWith(RcwPrefix, StringComparison.Ordinal) && name.EndsWith(']');
    }

    /// <summary>The name without the writer's size-range suffix (" (Bytes > 10K)").</summary>
    public static string WithoutSizeBucket(string recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        var bucket = recorded.IndexOf(SizeBucket, StringComparison.Ordinal);
        return bucket > 0 && recorded.EndsWith(')') ? recorded[..bucket] : recorded;
    }

    /// <summary>A type the writer saw used but never described (its type events were lost).</summary>
    public static bool IsUnknown(string recorded) => recorded.StartsWith("UNKNOWN 0x", StringComparison.Ordinal);

    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = WithoutSizeBucket(name);
        if (IsRuntimeCallableWrapper(name))
        {
            name = name[RcwPrefix.Length..^1];
        }

        // The writer turned every "[" and "]" into angle brackets: "[,]" is recorded as "<,>" (review M2). No generic
        // argument list is commas only (open generic types have no instances).
        name = MultiDimensional().Replace(name, "[$1]");

        var result = new StringBuilder(name.Length + 8);
        var angles = 0;
        var brackets = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '[' && i + 2 < name.Length && name[i + 1] == ']' && IsIdentifierPart(name[i + 2]))
            {
                // "[]" followed by a name is a compiler-generated "<>" (an array suffix ends the name or a type argument).
                result.Append("<>");
                i++;
                continue;
            }

            switch (c)
            {
                case '<':
                    angles++;
                    break;
                case '>':
                    angles--;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
                case ',' when angles > 0 && brackets == 0 && (i + 1 >= name.Length || name[i + 1] != ' '):
                    result.Append(", ");
                    continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>What can be told from a name alone: strings and arrays. Delegates, exceptions and boxes need base types.</summary>
    public static TypeKind KindOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name == "System.String" ? TypeKind.String
            : name.EndsWith("[]", StringComparison.Ordinal) || name.EndsWith(",]", StringComparison.Ordinal) ? TypeKind.Array
            : TypeKind.None;
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    [GeneratedRegex("<(,+)>")]
    private static partial Regex MultiDimensional();
}
