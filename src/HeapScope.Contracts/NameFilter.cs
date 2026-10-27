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
/// The type-to-filter rules (sprint 1.3, terms in 2.7), shared by the worker (which filters) and the UI (which highlights
/// the matched characters), so both always agree. A filter is terms separated by spaces, all of which must match:
/// <c>-term</c> leaves out names that match it; <c>module:text</c> (or <c>m:text</c>) matches the row's module instead of
/// its name, and a row without a module does not match it. Each term is matched as in 1.3: not anchored; smart case (an
/// all lower-case term ignores case, one with capitals is matched as typed); <c>*</c> matches any run of characters (its
/// parts appear in order, case-insensitive); a plain term is a substring, and a term with capitals that is not found is
/// also tried as CamelHumps: <c>SCE</c> or <c>StaCaEn</c> match <c>StaticCacheEntry</c> (each capital starts a word).
/// </summary>
public static class NameFilter
{
    private static readonly string[] s_modulePrefixes = ["module:", "m:"];

    /// <summary>True when <paramref name="name"/> matches <paramref name="filter"/> (an empty filter matches everything); a module term never matches.</summary>
    public static bool IsMatch(string name, string? filter) => IsMatch(name, null, filter);

    /// <summary>True when the row named <paramref name="name"/>, in <paramref name="module"/> (null or empty: none), matches every term.</summary>
    public static bool IsMatch(string name, string? module, string? filter)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var term in Terms(filter))
        {
            var target = term.OnModule ? module : name;
            var matched = !string.IsNullOrEmpty(target) && MatchTerm(target, term.Text) is not null;
            if (matched == term.Negated)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The matched character ranges (start, length) of the name terms in <paramref name="name"/>, in order, or null when a
    /// name term rules it out. Module terms are not tested here. An empty filter matches with no ranges.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)>? Match(string name, string? filter)
    {
        ArgumentNullException.ThrowIfNull(name);
        var ranges = new List<(int Start, int Length)>();
        foreach (var term in Terms(filter))
        {
            if (term.OnModule)
            {
                continue;
            }

            var matched = MatchTerm(name, term.Text);
            if (term.Negated)
            {
                if (matched is not null)
                {
                    return null;
                }

                continue;
            }

            if (matched is null)
            {
                return null;
            }

            ranges.AddRange(matched);
        }

        ranges.Sort();
        return ranges;
    }

    /// <summary>The filter's terms: text, whether it is negated, whether it tests the module.</summary>
    public static IReadOnlyList<(string Text, bool Negated, bool OnModule)> Terms(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return [];
        }

        var terms = new List<(string, bool, bool)>();
        foreach (var part in filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var text = part;
            var negated = text[0] == '-';
            if (negated)
            {
                text = text[1..];
            }

            var onModule = false;
            foreach (var prefix in s_modulePrefixes)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[prefix.Length..];
                    onModule = true;
                    break;
                }
            }

            // A term still being typed ("-", "module:") filters nothing yet.
            if (text.Length > 0)
            {
                terms.Add((text, negated, onModule));
            }
        }

        return terms;
    }

    private static List<(int, int)>? MatchTerm(string name, string term)
    {
        if (term.Contains('*', StringComparison.Ordinal))
        {
            return MatchWildcard(name, term);
        }

        // Smart case: an all lower-case term ignores case; one with capitals means them.
        var smartCase = term.Any(char.IsUpper);
        var index = name.IndexOf(term, smartCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return [(index, term.Length)];
        }

        return smartCase ? MatchCamelHumps(name, term) : null;
    }

    private static List<(int, int)>? MatchWildcard(string name, string filter)
    {
        var parts = filter.Split('*', StringSplitOptions.RemoveEmptyEntries);
        var ranges = new List<(int, int)>();
        var position = 0;
        foreach (var part in parts)
        {
            var index = name.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            ranges.Add((index, part.Length));
            position = index + part.Length;
        }

        // Like a plain filter, a wildcard filter is not anchored: its parts appear in order anywhere in the name.
        return ranges;
    }

    /// <summary>
    /// Each filter segment (a capital and the lower-case letters after it) starts a word, in order, within one identifier
    /// of the name (identifiers are separated by dots, generic brackets, commas, plus signs and spaces), so the humps of
    /// a long generic name do not combine across unrelated types.
    /// </summary>
    private static List<(int, int)>? MatchCamelHumps(string name, string filter)
    {
        var segments = new List<string>();
        var start = 0;
        for (var i = 1; i <= filter.Length; i++)
        {
            if (i == filter.Length || char.IsUpper(filter[i]))
            {
                segments.Add(filter[start..i]);
                start = i;
            }
        }

        var identifierStart = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && IsIdentifierChar(name[i]))
            {
                continue;
            }

            if (i > identifierStart && MatchInIdentifier(name, identifierStart, i, segments) is { } ranges)
            {
                return ranges;
            }

            identifierStart = i + 1;
        }

        return null;
    }

    private static List<(int, int)>? MatchInIdentifier(string name, int start, int end, List<string> segments)
    {
        var ranges = new List<(int, int)>();
        var position = start;
        foreach (var segment in segments)
        {
            var found = -1;
            for (var i = position; i + segment.Length <= end; i++)
            {
                if ((i == start || char.IsUpper(name[i])) && string.Compare(name, i, segment, 0, segment.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return null;
            }

            ranges.Add((found, segment.Length));
            position = found + segment.Length;
        }

        return ranges;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '`';
}
