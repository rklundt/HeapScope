namespace HeapScope.Contracts;

/// <summary>
/// The type-to-filter rules (sprint 1.3), shared by the worker (which filters) and the UI (which highlights the matched
/// characters), so both always agree. Not anchored. Smart case: an all lower-case filter ignores case; a filter with
/// capitals is matched as typed. A filter with <c>*</c> matches any run of characters there (its parts appear in order,
/// case-insensitive). A plain filter is a substring; a filter with capitals that is not found is also tried as
/// CamelHumps: <c>SCE</c> or <c>StaCaEn</c> match <c>StaticCacheEntry</c> (each capital starts a word of the name).
/// </summary>
public static class NameFilter
{
    /// <summary>True when <paramref name="name"/> matches <paramref name="filter"/> (an empty filter matches everything).</summary>
    public static bool IsMatch(string name, string? filter) => Match(name, filter) is not null;

    /// <summary>
    /// The matched character ranges (start, length) in <paramref name="name"/>, or null when it does not match. An empty
    /// filter matches with no ranges.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)>? Match(string name, string? filter)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(filter))
        {
            return [];
        }

        filter = filter.Trim();
        if (filter.Contains('*', StringComparison.Ordinal))
        {
            return MatchWildcard(name, filter);
        }

        // Smart case: an all lower-case filter ignores case; one with capitals means them.
        var smartCase = filter.Any(char.IsUpper);
        var index = name.IndexOf(filter, smartCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return [(index, filter.Length)];
        }

        return smartCase ? MatchCamelHumps(name, filter) : null;
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
