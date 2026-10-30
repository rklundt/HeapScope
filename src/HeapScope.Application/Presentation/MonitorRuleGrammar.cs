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

using System.Globalization;

namespace HeapScope.Application.Presentation;

/// <summary>
/// How a front end writes monitor rules: how an interval reads, the culture of its numbers, and what each refused field
/// says. The rules themselves (sizes, percents, at least one snapshot) are the same for every grammar.
/// </summary>
public sealed class MonitorRuleGrammar
{
    private readonly Func<string, TimeSpan?> _interval;

    private MonitorRuleGrammar(
        IFormatProvider provider,
        Func<string, TimeSpan?> interval,
        Func<string, string> everyRefused,
        Func<string, string> aboveRefused,
        Func<string, string> growthRefused,
        string maxRefused)
    {
        Provider = provider;
        _interval = interval;
        EveryRefused = everyRefused;
        AboveRefused = aboveRefused;
        GrowthRefused = growthRefused;
        MaxRefused = maxRefused;
    }

    /// <summary>The culture whole numbers (the snapshot limit) and intervals are read in.</summary>
    public IFormatProvider Provider { get; }

    /// <summary>What a refused interval says, given the text.</summary>
    public Func<string, string> EveryRefused { get; }

    /// <summary>What a refused size limit says, given the text.</summary>
    public Func<string, string> AboveRefused { get; }

    /// <summary>What a refused growth limit says, given the text.</summary>
    public Func<string, string> GrowthRefused { get; }

    /// <summary>What a refused snapshot limit says.</summary>
    public string MaxRefused { get; }

    /// <summary>
    /// The CLI's grammar: an interval with a unit (90s, 5m, 1h) of at least a second, in the invariant culture, so a
    /// script reads the same everywhere; messages name the option.
    /// </summary>
    public static MonitorRuleGrammar Suffixed { get; } = new(
        CultureInfo.InvariantCulture,
        SuffixedInterval,
        text => $"--every: '{text}' is not an interval such as 90s, 5m or 1h.",
        text => $"--above: '{text}' is not a size above zero such as 500MB.",
        text => $"--growth: '{text}' is not a size above zero such as 500MB or a percent such as 50%.",
        "--max must be 1 or more.");

    /// <summary>
    /// The app's grammar: an interval in bare minutes (5, 0.5) of at least a second, in <paramref name="provider"/>'s
    /// format (the current culture), as typed in the monitor form; messages name the form's field.
    /// </summary>
    public static MonitorRuleGrammar Minutes(IFormatProvider provider) => new(
        provider,
        text => double.TryParse(text, NumberStyles.Float, provider, out var minutes) && double.IsFinite(minutes) && minutes * 60 >= 1
            ? TimeSpan.FromMinutes(minutes)
            : null,
        _ => "Every: enter minutes, such as 5 or 0.5.",
        _ => "Above: enter a size above zero, such as 500MB or 2GB.",
        _ => "Growth: enter a size or a percent above zero, such as 100MB or 50%.",
        "Max snapshots: enter a whole number of 1 or more.");

    /// <summary>The interval <paramref name="text"/> names, or null when it is not one this grammar reads.</summary>
    public TimeSpan? Interval(string text) => _interval(text);

    private static TimeSpan? SuffixedInterval(string text)
    {
        var trimmed = text.Trim().ToLowerInvariant();
        if (trimmed.Length < 2 || !double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || !double.IsFinite(amount))
        {
            return null;
        }

        var interval = trimmed[^1] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => default,
        };
        return interval >= TimeSpan.FromSeconds(1) ? interval : null;
    }
}
