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

namespace HeapScope.Core.Graph;

/// <summary>Converts type names between the forms snapshot kinds record (sprint 1.8).</summary>
public static class TypeNameForms
{
    /// <summary>
    /// The name a gcdump records for a type a full dump names <paramref name="fullName"/>. A gcdump keeps only the
    /// innermost name of a nested type, with the declaring types' generic arguments moved onto it:
    /// <c>System.Collections.Generic.Dictionary&lt;System.Int32, X&gt;+Entry[]</c> becomes
    /// <c>Entry&lt;System.Int32, X&gt;[]</c> and <c>Fixtures.ClosureLeak+&lt;&gt;c__DisplayClass6_0</c> becomes
    /// <c>&lt;&gt;c__DisplayClass6_0</c>. Names of types that are not nested are the same in both kinds.
    /// </summary>
    public static string AsGcDumpName(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var plus = LastTopLevelPlus(fullName);
        if (plus < 0)
        {
            return fullName;
        }

        var declaring = fullName[..plus];
        var nested = fullName[(plus + 1)..];
        var arraySuffix = ArraySuffixStart(nested);
        var nestedName = nested[..arraySuffix];
        var arguments = new List<string>();
        CollectArguments(declaring, arguments);
        if (nestedName.EndsWith('>') && OpeningAngle(nestedName) is > 0 and var open)
        {
            arguments.Add(nestedName[(open + 1)..^1]);
            nestedName = nestedName[..open];
        }

        return arguments.Count == 0
            ? nestedName + nested[arraySuffix..]
            : $"{nestedName}<{string.Join(", ", arguments)}>{nested[arraySuffix..]}";
    }

    // The generic argument lists of every declaring type, outermost first.
    private static void CollectArguments(string declaring, List<string> arguments)
    {
        var depth = 0;
        var start = -1;
        for (var i = 0; i < declaring.Length; i++)
        {
            switch (declaring[i])
            {
                case '<' when depth++ == 0 && i > 0 && declaring[i - 1] is not ('+' or '.'):
                    start = i + 1;
                    break;
                case '<':
                    break;
                case '>' when --depth == 0 && start >= 0:
                    arguments.Add(declaring[start..i]);
                    start = -1;
                    break;
            }
        }
    }

    private static int LastTopLevelPlus(string name)
    {
        var depth = 0;
        var last = -1;
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '<' or '[':
                    depth++;
                    break;
                case '>' or ']':
                    depth--;
                    break;
                case '+' when depth == 0:
                    last = i;
                    break;
            }
        }

        return last;
    }

    // Where trailing array brackets ("[]", "[,]", "[][]") start; the name's length when there are none.
    private static int ArraySuffixStart(string name)
    {
        var end = name.Length;
        while (end > 0 && name[end - 1] == ']')
        {
            var open = name.LastIndexOf('[', end - 1);
            if (open < 0 || name.AsSpan(open + 1, end - open - 2).ContainsAnyExcept(','))
            {
                break;
            }

            end = open;
        }

        return end;
    }

    // The '<' matching the final '>' of a name, or -1.
    private static int OpeningAngle(string name)
    {
        var depth = 0;
        for (var i = name.Length - 1; i >= 0; i--)
        {
            if (name[i] == '>')
            {
                depth++;
            }
            else if (name[i] == '<' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
